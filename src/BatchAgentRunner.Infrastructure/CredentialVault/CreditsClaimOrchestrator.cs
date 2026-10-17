using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>积分领取的阶段（界面进度提示用）。</summary>
public enum CreditsClaimPhase
{
    Prepare, Switching, ReadingBefore, Claiming, ReadingAfter, Recording, Skipped, Done, Failed
}

/// <summary>积分领取进度（按账号粒度上抛）。</summary>
public sealed record CreditsClaimProgress(CreditsClaimPhase Phase, string AccountId, string Message);

/// <summary>
/// 每日积分领取编排器：对账号池逐个执行
/// 「今日去重检查 → 切换该账号凭证为 Live → 从 Live 凭证提取 accessToken →
///   读领取前积分 → 调领取接口 → 读领取后积分 → 记录（前/后/变化量）并落台账」。
/// 单个账号失败不中断其余账号；每个账号记录落库后立即保存台账（崩溃安全）。
/// </summary>
public sealed class CreditsClaimOrchestrator
{
    private readonly ICredentialVault _vault;
    private readonly ICreditsApiClient _api;
    private readonly ICreditsClaimStore _store;
    private readonly ICredentialTokenReader _tokenReader;
    private readonly IClock _clock;
    private readonly IRunLogger? _logger;
    private readonly Action<CreditsClaimProgress>? _progress;

    public CreditsClaimOrchestrator(ICredentialVault vault, ICreditsApiClient api, ICreditsClaimStore store,
        ICredentialTokenReader? tokenReader = null, IClock? clock = null, IRunLogger? logger = null,
        Action<CreditsClaimProgress>? progress = null)
    {
        _vault = vault;
        _api = api;
        _store = store;
        _tokenReader = tokenReader ?? new LiveCredentialTokenReader();
        _clock = clock ?? new SystemClock();
        _logger = logger;
        _progress = progress;
    }

    /// <param name="force">true 时忽略去重，强制再次尝试（服务端仍会拒绝重复领取）。</param>
    public async Task<CreditsClaimSummary> RunAllAsync(AgentKind cli, IReadOnlyList<AccountConfig> accounts,
        bool force = false, CancellationToken cancellationToken = default)
    {
        var ledger = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var summary = new CreditsClaimSummary { GeneratedAt = _clock.Now, Cli = cli.ToString() };
        var today = _clock.Now.LocalDateTime.Date;

        Report(CreditsClaimPhase.Prepare, null, $"开始为 {accounts.Count} 个账号领取每日积分（{cli}）");

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await ClaimOneAsync(cli, account, ledger, today, force, cancellationToken)
                .ConfigureAwait(false);
            summary.Records.Add(record);

            if (record.Status is CreditsClaimStatus.Claimed or CreditsClaimStatus.AlreadyClaimed
                or CreditsClaimStatus.Failed)
            {
                ledger.Records.Add(record);
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            }
        }

        Report(CreditsClaimPhase.Done, null,
            $"每日积分领取结束：成功 {summary.ClaimedCount}，已领取过 {summary.AlreadyClaimedCount}，" +
            $"失败 {summary.FailedCount}，跳过 {summary.SkippedCount}");
        return summary;
    }

    private async Task<CreditsClaimRecord> ClaimOneAsync(AgentKind cli, AccountConfig account,
        CreditsClaimLedger ledger, DateTime today, bool force, CancellationToken cancellationToken)
    {
        var record = new CreditsClaimRecord
        {
            AccountId = account.Id,
            DisplayName = account.Label,
            Cli = cli.ToString(),
            ClaimedAt = _clock.Now
        };

        try
        {
            // ① 当日去重：台账中该账号今日已有成功/已领取记录时跳过（force 除外）
            if (!force && ledger.Records.Any(r => r.AccountId == account.Id &&
                    r.ClaimedAt.LocalDateTime.Date == today &&
                    r.Status is CreditsClaimStatus.Claimed or CreditsClaimStatus.AlreadyClaimed))
            {
                record.Status = CreditsClaimStatus.Skipped;
                record.Message = "今日已领取过（台账去重），如需重试请勾选强制";
                Report(CreditsClaimPhase.Skipped, account.Id, $"账号 {account.Label} 今日已领取过，跳过");
                return record;
            }

            // ② 切换该账号凭证为 Live（无快照 = 未引导登录过）
            if (!_vault.SnapshotExists(cli, account.Id))
            {
                throw new CredentialVaultException($"账号 {account.Label} 没有凭证快照，请先在该页面完成引导登录");
            }

            Report(CreditsClaimPhase.Switching, account.Id, $"正在切换到账号 {account.Label} 的登录身份…");
            await _vault.SwitchToAsync(cli, account.Id, "credits-claim", cancellationToken).ConfigureAwait(false);

            // ③ 从 Live 凭证提取 accessToken
            var token = _tokenReader.TryReadAccessToken(cli)
                        ?? (cli == AgentKind.Mock ? "mock-token" : null);
            if (string.IsNullOrEmpty(token))
            {
                throw new CredentialVaultException("未能从 Live 凭证文件中提取 accessToken，无法调用积分接口");
            }

            // ④ 领取前积分
            Report(CreditsClaimPhase.ReadingBefore, account.Id, $"正在读取账号 {account.Label} 的当前积分…");
            var before = (await _api.GetBalanceAsync(token, cancellationToken).ConfigureAwait(false)).Balance;
            record.BeforeCredits = before;

            // ⑤ 领取（每日端点）
            Report(CreditsClaimPhase.Claiming, account.Id, $"正在为账号 {account.Label} 领取每日积分…");
            var claim = await _api.ClaimDailyAsync(token, cancellationToken).ConfigureAwait(false);
            record.GrantedCredits = claim.Granted;
            record.Message = claim.Message;

            if (!claim.Success && !claim.AlreadyClaimed)
            {
                throw new CredentialVaultException($"积分接口拒绝领取：{claim.Message ?? "未返回原因"}");
            }

            // ⑥ 领取后积分（已领取过时服务端不会变动，可省一次查询，直接复用响应余额）
            decimal? after;
            if (claim.AlreadyClaimed)
            {
                record.Status = CreditsClaimStatus.AlreadyClaimed;
                after = claim.BalanceAfter ?? await ProbeBalanceAsync(token, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                record.Status = CreditsClaimStatus.Claimed;
                Report(CreditsClaimPhase.ReadingAfter, account.Id, $"领取成功，正在复核账号 {account.Label} 的最新积分…");
                after = (await _api.GetBalanceAsync(token, cancellationToken).ConfigureAwait(false)).Balance
                        ?? claim.BalanceAfter;
            }

            record.AfterCredits = after;
            if (before.HasValue && after.HasValue)
            {
                record.DeltaCredits = after.Value - before.Value;
            }

            _logger?.Write("INFO",
                $"[积分领取] {account.Label}：领取前 {FormatCredits(before)} → 领取后 {FormatCredits(after)}" +
                $"（变化 {(record.DeltaCredits.HasValue ? (record.DeltaCredits.Value >= 0 ? "+" : "") + record.DeltaCredits.Value : "未知")}），" +
                $"状态：{record.Status}");
            Report(record.Status == CreditsClaimStatus.Claimed ? CreditsClaimPhase.Done : CreditsClaimPhase.Skipped,
                account.Id,
                Describe(record));
            return record;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            record.Status = CreditsClaimStatus.Failed;
            record.Error = ex.Message;
            _logger?.Write("WARN", $"[积分领取] {account.Label}：失败（{ex.Message}）");
            Report(CreditsClaimPhase.Failed, account.Id, $"账号 {account.Label} 领取失败：{ex.Message}");
            return record;
        }
    }

    /// <summary>领取接口响应未带余额时的兜底查询；再失败则维持 null（不影响记录本身）。</summary>
    private async Task<decimal?> ProbeBalanceAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            return (await _api.GetBalanceAsync(token, cancellationToken).ConfigureAwait(false)).Balance;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string FormatCredits(decimal? value) => value.HasValue ? value.Value.ToString("0.##") : "未知";

    internal static string Describe(CreditsClaimRecord r)
    {
        var label = string.IsNullOrWhiteSpace(r.DisplayName) ? r.AccountId : r.DisplayName;
        var delta = r.DeltaCredits.HasValue
            ? (r.DeltaCredits.Value >= 0 ? "+" : "") + r.DeltaCredits.Value
            : "未知";
        return r.Status switch
        {
            CreditsClaimStatus.Claimed =>
                $"领取成功：{label} 积分 {FormatCredits(r.BeforeCredits)} → {FormatCredits(r.AfterCredits)}（{delta}）",
            CreditsClaimStatus.AlreadyClaimed =>
                $"今日已领取过：{label} 当前积分 {FormatCredits(r.AfterCredits)}，未重复发放",
            _ => r.Message ?? r.Error ?? "完成"
        };
    }

    private void Report(CreditsClaimPhase phase, string? accountId, string message) =>
        _progress?.Invoke(new CreditsClaimProgress(phase, accountId ?? string.Empty, message));
}
