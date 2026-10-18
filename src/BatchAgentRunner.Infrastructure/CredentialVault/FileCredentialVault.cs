using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>
/// 文件型凭证 Vault 实现（DESIGN-账号凭证切换-v1.0 §3-§8）。
/// 目录布局：
///   vault\&lt;cli&gt;\&lt;account_id&gt;\files\&lt;relative_path&gt;   （受保护内容，DESIGN §4.1）
///   vault\&lt;cli&gt;\&lt;account_id&gt;\manifest.json             （明文元数据 + 明文哈希，DESIGN §4.2）
///   vault\live-state.json                              （Live 归属记账）
///   vault\audit.log                                    （审计日志：时间/账号/动作/结果，不含内容）
/// 安全约定：快照内凭证文件经 <see cref="ICredentialProtector"/> 加密落盘；审计只记账号 id 与哈希前 8 位。
/// </summary>
/// <remarks>
/// CA1001（P0-12）：持有 <see cref="SemaphoreSlim"/> 序列化门，实现 <see cref="IDisposable"/>；
/// 由 <see cref="CredentialVaultHost"/> 在用毕时释放（Vault 为应用级单例，随宿主销毁）。
/// </remarks>
public sealed class FileCredentialVault : ICredentialVault, IDisposable
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;
    private readonly ICredentialProtector _protector;
    private readonly ICliProcessGuard _processGuard;
    private readonly IClock _clock;
    private readonly Action<string>? _auditSink;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileCredentialVault(string? root = null, ICredentialProtector? protector = null,
        ICliProcessGuard? processGuard = null, IClock? clock = null, Action<string>? auditSink = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BatchAgentRunner", "vault");
        _protector = protector ?? new DpapiCredentialProtector();
        _processGuard = processGuard ?? new CliProcessGuard();
        _clock = clock ?? new SystemClock();
        _auditSink = auditSink;
    }

    /// <summary>Vault 根目录（诊断用）。</summary>
    public string Root => _root;

    // ---------------- 查询 ----------------

    public string? CurrentLiveAccount(AgentKind cli)
    {
        var spec = CredentialLocationRegistry.Resolve(cli);
        var state = ReadLiveState();
        return state.TryGetValue(spec.CliId, out var s) ? s.AccountId : null;
    }

    public bool SnapshotExists(AgentKind cli, string accountId) =>
        File.Exists(ManifestPath(cli, NormalizeAccountId(accountId)));

    public CredentialSnapshotInfo? GetSnapshot(AgentKind cli, string accountId)
    {
        var manifest = ReadManifest(cli, NormalizeAccountId(accountId));
        return manifest is null ? null : ToInfo(manifest);
    }

    // ---------------- 备份（DESIGN §5 ⑦） ----------------

    public async Task<CredentialSnapshotInfo> BackupFromLiveAsync(AgentKind cli, string accountId,
        string? displayName = null, string? site = null, CancellationToken cancellationToken = default)
    {
        var id = NormalizeAccountId(accountId);
        var spec = EnsureConfigured(cli);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCliNotRunning(spec, "备份凭证");

            var collected = CollectLiveFiles(spec);
            if (collected.Count == 0)
            {
                var expected = string.Join("; ", spec.Files.Select(f =>
                    CredentialLocationRegistry.ResolveLiveFile(spec, f.RelativePath)));
                throw new CredentialVaultException(
                    $"未发现 {spec.CliId} 的 Live 凭证文件（预期位置：{expected}）。" +
                    "请先完成 CLI 引导登录");
            }

            // 重复备份 = 覆盖并更新时间戳，保留 last_verified_at（DESIGN §11 测试要点）
            var existing = ReadManifest(cli, id);
            var manifest = new SnapshotManifest
            {
                AccountId = id,
                Cli = spec.CliId,
                DisplayName = displayName ?? existing?.DisplayName,
                Site = site ?? existing?.Site,
                BackedUpAt = _clock.Now,
                LastVerifiedAt = existing?.LastVerifiedAt,
                Files = collected.Select(f => new CredentialFileInfo(f.RelativePath, f.Sha256, f.Bytes.Length))
                    .ToList(),
                SchemaVersion = 1
            };

            foreach (var file in collected)
            {
                WriteProtectedSnapshotFile(spec.CliId, id, file.RelativePath, file.Bytes);
            }

            WriteManifest(cli, id, manifest);
            await SetLiveAccountAsync(spec, id, cancellationToken).ConfigureAwait(false);

            Audit($"backup cli={spec.CliId} account={id} files={collected.Count} " +
                  $"sha256={ShortHash(collected[0].Sha256)}");
            return ToInfo(manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------- 切换（DESIGN §6） ----------------

    public async Task SwitchToAsync(AgentKind cli, string accountId, string reason,
        CancellationToken cancellationToken = default)
    {
        var id = NormalizeAccountId(accountId);
        var spec = EnsureConfigured(cli);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCliNotRunning(spec, "切换凭证");

            var target = ReadManifest(cli, id)
                         ?? throw new CredentialVaultException(
                             $"账号 {id} 在 Vault 中没有凭证快照，无法切换：请先在配置页对该账号执行「引导登录」");

            var liveState = ReadLiveState();
            var from = liveState.TryGetValue(spec.CliId, out var s) ? s.AccountId : null;

            // ② 保活回写：Live 属于 Y 且文件哈希 ≠ Y 快照记录 → 先把 Live 复制回 Y 的快照
            if (from is not null && !string.Equals(from, id, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    WriteBackLiveToSnapshot(spec, from);
                }
                catch (Exception ex)
                {
                    // 回写失败 = Y 的最新 token 可能丢失，宁可中止切换也不静默吞掉
                    throw new CredentialVaultException(
                        $"保活回写失败（账号 {from} 的最新凭证无法写回快照），已中止切换：{ex.Message}", ex);
                }
            }

            // ③ 原子覆盖：X 快照 files → Live 位置（先写临时文件 + rename，失败自动回滚）
            RestoreSnapshotToLive(spec, target, cancellationToken);

            // ④ 记账 + 校验站点 + 审计
            await SetLiveAccountAsync(spec, id, cancellationToken).ConfigureAwait(false);
            CheckSiteConsistency(spec, from, id);
            Audit($"switch cli={spec.CliId} from={from ?? "(none)"} to={id} reason={reason} " +
                  $"sha256={(target.Files.Count > 0 ? ShortHash(target.Files[0].Sha256) : "-")}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 保活回写（DESIGN §6 ②，本设计的灵魂）：CLI 运行期间会自动刷新 access/refresh token，
    /// 最新 token 在 Live 里——离开账号 Y 前把 Live 还给 Y，快照因此始终是最新一次使用时的凭证。
    /// Live 未变化时不写（避免无意义覆盖）。
    /// </summary>
    private void WriteBackLiveToSnapshot(CredentialLocationSpec spec, string fromAccount)
    {
        var manifest = ReadManifest(spec.Kind, fromAccount);
        if (manifest is null)
        {
            // Live 归属的账号没有快照（如快照被手工删除）——无从回写，记审计后跳过
            Audit($"WARN keep-alive skipped cli={spec.CliId} account={fromAccount} reason=snapshot-missing");
            return;
        }

        var updated = new List<CredentialFileInfo>();
        var changed = 0;
        foreach (var entry in manifest.Files)
        {
            var livePath = CredentialLocationRegistry.ResolveLiveFile(spec, entry.RelativePath);
            if (!File.Exists(livePath))
            {
                updated.Add(entry);
                continue;
            }

            var bytes = File.ReadAllBytes(livePath);
            var hash = Sha256(bytes);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                WriteProtectedSnapshotFile(spec.CliId, fromAccount, entry.RelativePath, bytes);
                updated.Add(new CredentialFileInfo(entry.RelativePath, hash, bytes.Length));
                changed++;
                continue;
            }

            updated.Add(entry);
        }

        // Live 中存在、但快照清单未登记的文件（如首登后 CLI 新增的文件）也一并收入
        foreach (var liveFile in spec.Files)
        {
            if (manifest.Files.Any(f =>
                    string.Equals(f.RelativePath, liveFile.RelativePath, StringComparison.Ordinal)))
            {
                continue;
            }

            var livePath = CredentialLocationRegistry.ResolveLiveFile(spec, liveFile.RelativePath);
            if (!File.Exists(livePath))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(livePath);
            WriteProtectedSnapshotFile(spec.CliId, fromAccount, liveFile.RelativePath, bytes);
            updated.Add(new CredentialFileInfo(liveFile.RelativePath, Sha256(bytes), bytes.Length));
            changed++;
        }

        if (changed == 0)
        {
            return;
        }

        manifest.Files = updated;
        manifest.BackedUpAt = _clock.Now;
        WriteManifest(spec.Kind, fromAccount, manifest);
        Audit($"keep-alive cli={spec.CliId} account={fromAccount} updated={changed} " +
              $"sha256={(updated.Count > 0 ? ShortHash(updated[0].Sha256) : "-")}");
    }

    /// <summary>
    /// 原子回填（DESIGN §6 ③）：每个目标快照文件先写同目录临时文件再 rename 覆盖；
    /// 任一步失败则把已覆盖/已删除的 Live 文件恢复到切换前状态后抛出。
    /// </summary>
    private void RestoreSnapshotToLive(CredentialLocationSpec spec, SnapshotManifest target,
        CancellationToken cancellationToken)
    {
        var rollback = new List<(string Path, byte[]? PreviousBytes)>();
        try
        {
            foreach (var entry in target.Files)
            {
                var livePath = CredentialLocationRegistry.ResolveLiveFile(spec, entry.RelativePath);
                var directory = Path.GetDirectoryName(Path.GetFullPath(livePath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                rollback.Add((livePath, File.Exists(livePath) ? File.ReadAllBytes(livePath) : null));

                var snapshotPath = SnapshotFilePath(spec.CliId, target.AccountId, entry.RelativePath);
                var plaintext = _protector.Unprotect(File.ReadAllBytes(snapshotPath));

                var tempPath = $"{livePath}.vault-tmp-{Guid.NewGuid():N}";
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(plaintext, 0, plaintext.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, livePath, overwrite: true);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // 目标快照没有、但 Live 有的文件：该账号的凭证文件集以快照为准 → 删除（已捕获可回滚）
            foreach (var liveFile in spec.Files)
            {
                if (target.Files.Any(f =>
                        string.Equals(f.RelativePath, liveFile.RelativePath, StringComparison.Ordinal)))
                {
                    continue;
                }

                var livePath = CredentialLocationRegistry.ResolveLiveFile(spec, liveFile.RelativePath);
                if (File.Exists(livePath))
                {
                    rollback.Add((livePath, File.ReadAllBytes(livePath)));
                    File.Delete(livePath);
                }
            }
        }
        catch (Exception ex)
        {
            RollbackLive(rollback);
            throw new CredentialVaultException(
                $"凭证回填 Live 失败，已回滚到切换前的 Live 状态：{ex.Message}", ex);
        }
    }

    private static void RollbackLive(List<(string Path, byte[]? PreviousBytes)> rollback)
    {
        foreach (var (path, previous) in rollback)
        {
            try
            {
                if (previous is null)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                else
                {
                    File.WriteAllBytes(path, previous);
                }
            }
            catch
            {
                // 回滚尽力而为；原始异常优先抛出
            }
        }
    }

    // ---------------- 验证时间 / 删除 ----------------

    public async Task MarkVerifiedAsync(AgentKind cli, string accountId,
        CancellationToken cancellationToken = default)
    {
        var id = NormalizeAccountId(accountId);
        var spec = EnsureConfigured(cli);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = ReadManifest(cli, id)
                           ?? throw new CredentialVaultException($"账号 {id} 在 Vault 中没有凭证快照");
            manifest.LastVerifiedAt = _clock.Now;
            WriteManifest(cli, id, manifest);
            Audit($"verify cli={spec.CliId} account={id} result=ok");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSnapshotAsync(AgentKind cli, string accountId,
        CancellationToken cancellationToken = default)
    {
        var id = NormalizeAccountId(accountId);
        var spec = EnsureConfigured(cli);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.Combine(_root, spec.CliId, id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            Audit($"delete cli={spec.CliId} account={id}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------- 内部工具 ----------------

    private static CredentialLocationSpec EnsureConfigured(AgentKind cli)
    {
        var spec = CredentialLocationRegistry.Resolve(cli);
        if (!spec.IsConfigured)
        {
            throw new CredentialVaultException(
                $"{spec.CliId} 的登录态存储位置尚未确认（DESIGN-账号凭证切换 V-4b 待实测），" +
                "确认前不允许凭证备份/切换；实测后在 CredentialLocationRegistry 登记路径即可");
        }

        return spec;
    }

    private void EnsureCliNotRunning(CredentialLocationSpec spec, string action)
    {
        if (_processGuard.IsCliRunning(spec.Kind))
        {
            throw new CredentialVaultException(
                $"检测到 {spec.CliId} 进程仍在运行，为避免凭证文件被覆盖已拒绝{action}；" +
                "请先退出该 CLI（批处理请在任务边界/空闲时操作）");
        }
    }

    private static List<(string RelativePath, byte[] Bytes, string Sha256)> CollectLiveFiles(CredentialLocationSpec spec)
    {
        var result = new List<(string, byte[], string)>();
        foreach (var file in spec.Files)
        {
            var livePath = CredentialLocationRegistry.ResolveLiveFile(spec, file.RelativePath);
            if (!File.Exists(livePath))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(livePath);
            result.Add((file.RelativePath, bytes, Sha256(bytes)));
        }

        return result;
    }

    private void CheckSiteConsistency(CredentialLocationSpec spec, string? fromAccount, string toAccount)
    {
        if (fromAccount is null)
        {
            return;
        }

        var fromManifest = ReadManifest(spec.Kind, fromAccount);
        var toManifest = ReadManifest(spec.Kind, toAccount);
        if (fromManifest?.Site is { } a && toManifest?.Site is { } b &&
            !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            Audit($"WARN site-mismatch cli={spec.CliId} from={fromAccount}(site={a}) to={toAccount}(site={b})");
        }
    }

    private Dictionary<string, LiveAccountState> ReadLiveState()
    {
        var path = Path.Combine(_root, "live-state.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, LiveAccountState>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, LiveAccountState>>(File.ReadAllText(path),
                       ManifestOptions) ?? new Dictionary<string, LiveAccountState>(StringComparer.Ordinal);
        }
        catch
        {
            // 记账文件损坏按「未知归属」处理，下次备份/切换会重建
            return new Dictionary<string, LiveAccountState>(StringComparer.Ordinal);
        }
    }

    private async Task SetLiveAccountAsync(CredentialLocationSpec spec, string accountId,
        CancellationToken cancellationToken)
    {
        var state = ReadLiveState();
        state[spec.CliId] = new LiveAccountState
        {
            AccountId = accountId,
            Since = _clock.Now
        };

        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "live-state.json");
        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(tempPath,
            JsonSerializer.Serialize(state, ManifestOptions), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    private SnapshotManifest? ReadManifest(AgentKind cli, string accountId)
    {
        var path = ManifestPath(cli, accountId);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(path), ManifestOptions);
    }

    private void WriteManifest(AgentKind cli, string accountId, SnapshotManifest manifest)
    {
        var dir = AccountDirectory(CredentialLocationRegistry.Resolve(cli).CliId, accountId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestOptions));
    }

    private void WriteProtectedSnapshotFile(string cliId, string accountId, string relativePath, byte[] plaintext)
    {
        var target = SnapshotFilePath(cliId, accountId, relativePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var protectedBytes = _protector.Protect(plaintext);
        var tempPath = $"{target}.tmp-{Guid.NewGuid():N}";
        File.WriteAllBytes(tempPath, protectedBytes);
        File.Move(tempPath, target, overwrite: true);
    }

    private string SnapshotFilePath(string cliId, string accountId, string relativePath) =>
        Path.Combine(AccountDirectory(cliId, accountId), "files",
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string AccountDirectory(string cliId, string accountId) =>
        Path.Combine(_root, cliId, accountId);

    private string ManifestPath(AgentKind cli, string accountId) =>
        Path.Combine(AccountDirectory(CredentialLocationRegistry.Resolve(cli).CliId, accountId), "manifest.json");

    private static string NormalizeAccountId(string accountId)
    {
        var id = accountId.Trim();
        if (id.Length == 0)
        {
            throw new CredentialVaultException("账号 ID 不能为空，无法用作 Vault 目录名");
        }

        if (id is "." or ".." || id.Contains('/') || id.Contains('\\') ||
            id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new CredentialVaultException($"账号 ID 含非法字符，不能用作 Vault 目录名：{id}");
        }

        return id;
    }

    private static CredentialSnapshotInfo ToInfo(SnapshotManifest manifest) => new(
        manifest.AccountId, manifest.Cli, manifest.DisplayName, manifest.Site,
        manifest.BackedUpAt, manifest.LastVerifiedAt, manifest.Files);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ShortHash(string sha256) => sha256[..Math.Min(8, sha256.Length)];

    private void Audit(string message)
    {
        var line = $"[{_clock.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try
        {
            Directory.CreateDirectory(_root);
            File.AppendAllText(Path.Combine(_root, "audit.log"), line + Environment.NewLine);
        }
        catch
        {
            // 审计写失败不阻断业务动作
        }

        _auditSink?.Invoke(line);
    }

    /// <summary>释放序列化门（CA1001）。仅在所有凭证操作已完成后调用。</summary>
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>live-state.json 中的单个 CLI 登录态归属记录。</summary>
public sealed class LiveAccountState
{
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("since")]
    public DateTimeOffset Since { get; set; }
}
