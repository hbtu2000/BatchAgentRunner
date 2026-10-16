using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>引导登录的阶段（界面进度提示用）。</summary>
public enum GuidedLoginPhase
{
    Prepare, Clearing, Launching, Waiting, Verifying, BackingUp, Done, Failed
}

/// <summary>引导登录进度（DESIGN §5 ①-⑧ 对应）。</summary>
public sealed record GuidedLoginProgress(GuidedLoginPhase Phase, string Message);

/// <summary>
/// 引导登录编排器（DESIGN §5）：清场 → 拉起 CLI 交互式登录 → 轮询检测 Live 凭证出现/变化
/// （默认 5 分钟超时）→ 可选极简校验 → 按账号备份进 Vault。
/// </summary>
public sealed class GuidedLoginOrchestrator
{
    /// <summary>⑥ 校验：调用 CLI 做一次极简请求确认凭证有效（如适配器的 ValidateAccountAsync）。</summary>
    public delegate Task<PreflightResult> AccountValidatorDelegate(string accountId, CancellationToken ct);

    private readonly ICredentialVault _vault;
    private readonly ICliProcessGuard _processGuard;
    private readonly ICliLoginLauncher _launcher;
    private readonly IClock _clock;
    private readonly Action<GuidedLoginProgress>? _progress;
    private readonly TimeSpan _pollInterval;

    public GuidedLoginOrchestrator(ICredentialVault vault, ICliProcessGuard processGuard,
        ICliLoginLauncher launcher, IClock? clock = null, Action<GuidedLoginProgress>? progress = null,
        TimeSpan? pollInterval = null)
    {
        _vault = vault;
        _processGuard = processGuard;
        _launcher = launcher;
        _clock = clock ?? new SystemClock();
        _progress = progress;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    /// <param name="timeout">等待登录凭证的超时（DESIGN §5 ⑤：默认 5 分钟）。</param>
    public async Task<CredentialSnapshotInfo> RunAsync(AgentKind cli, string accountId,
        string? displayName = null, string? site = null, TimeSpan? timeout = null,
        AccountValidatorDelegate? validator = null, CancellationToken cancellationToken = default)
    {
        var spec = CredentialLocationRegistry.Resolve(cli);
        if (!spec.IsConfigured)
        {
            throw new CredentialVaultException(
                $"{spec.CliId} 的登录态存储位置尚未确认（V-4b），确认前无法执行引导登录");
        }

        Report(GuidedLoginPhase.Prepare, $"开始为账号 {accountId} 执行引导登录");

        // ② 清场：无该 CLI 运行中进程；移走当前 Live 凭证，回到「干净未登录」可重复起点（V-4e）
        if (_processGuard.IsCliRunning(cli))
        {
            throw new CredentialVaultException($"检测到 {spec.CliId} 进程仍在运行，请先退出该 CLI 再执行引导登录");
        }

        var baseline = ClearLiveCredentials(spec);
        Report(GuidedLoginPhase.Clearing, "已清理旧 Live 凭证，等待重新登录");

        // ③ 拉起 CLI 交互式登录（浏览器 OAuth）
        Report(GuidedLoginPhase.Launching, $"正在拉起 {spec.CliId} 交互式登录（浏览器 OAuth）…");
        await _launcher.LaunchAsync(cli, cancellationToken).ConfigureAwait(false);

        // ⑤ 变化检测：以「文件出现或哈希变化」为准，不依赖 CLI 退出码
        Report(GuidedLoginPhase.Waiting,
            $"等待你在浏览器完成登录…（检测凭证文件变化，最长 {FormatTimeout(timeout)}）");
        await WaitLiveChangedAsync(spec, baseline, timeout ?? TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false);

        // ⑥ 校验：极简请求确认凭证有效
        if (validator is not null)
        {
            Report(GuidedLoginPhase.Verifying, "检测到登录凭证，正在校验有效性…");
            var result = await validator(accountId, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                throw new CredentialVaultException($"登录凭证校验未通过：{result.Message}");
            }
        }

        // ⑦ 备份：Live 文件集 → vault\cli\account（副本，Live 保留一份，备份后 CLI 立即可用）
        Report(GuidedLoginPhase.BackingUp, "登录凭证有效，正在备份进 Vault…");
        var info = await _vault.BackupFromLiveAsync(cli, accountId, displayName, site, cancellationToken)
            .ConfigureAwait(false);

        // ⑧ 成功：该账号进入可用账号池
        Report(GuidedLoginPhase.Done,
            $"账号 {accountId} 凭证已备份进 Vault（{info.Files.Count} 个文件），进入可用账号池");
        return info;
    }

    /// <summary>记录清理前的 Live 指纹并删除 Live 凭证文件（M0 V-4e 确认后可换 logout 清理动作）。</summary>
    private static Dictionary<string, string?> ClearLiveCredentials(CredentialLocationSpec spec)
    {
        var baseline = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var file in spec.Files)
        {
            var livePath = CredentialLocationRegistry.ResolveLiveFile(spec, file.RelativePath);
            if (!File.Exists(livePath))
            {
                baseline[file.RelativePath] = null;
                continue;
            }

            baseline[file.RelativePath] = Sha256Of(livePath);
            File.Delete(livePath);
        }

        return baseline;
    }

    private async Task WaitLiveChangedAsync(CredentialLocationSpec spec,
        Dictionary<string, string?> baseline, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _clock.Now + timeout;
        while (true)
        {
            foreach (var file in spec.Files)
            {
                var livePath = CredentialLocationRegistry.ResolveLiveFile(spec, file.RelativePath);
                if (File.Exists(livePath) &&
                    (!baseline.TryGetValue(file.RelativePath, out var old) || old != Sha256Of(livePath)))
                {
                    return;
                }
            }

            if (_clock.Now >= deadline)
            {
                throw new CredentialVaultException(
                    $"等待登录凭证超时（{FormatTimeout(timeout)}内未检测到凭证文件出现或变化）");
            }

            await _clock.DelayAsync(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Report(GuidedLoginPhase phase, string message) =>
        _progress?.Invoke(new GuidedLoginProgress(phase, message));

    private static string Sha256Of(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string FormatTimeout(TimeSpan? timeout) =>
        $"{(int)(timeout ?? TimeSpan.FromMinutes(5)).TotalMinutes} 分钟";
}
