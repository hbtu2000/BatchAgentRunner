using BatchAgentRunner.Core.Abstractions;

namespace BatchAgentRunner.Infrastructure.Http;

/// <summary>
/// 仿真积分 API 客户端（AgentKind.Mock / 无服务端环境联调用）：
/// 每个票据（= 每个账号）初始余额 100，首次领取 +20，重复领取返回「今日已领取」。
/// 行为确定性，供单元测试与 UI 全链路演练「领取前 → 领取 → 领取后」的完整记录。
/// </summary>
public sealed class MockCreditsApiClient : ICreditsApiClient
{
    private readonly Dictionary<string, decimal> _balances = new(StringComparer.Ordinal);
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    public decimal InitialBalance { get; set; } = 100m;

    public decimal GrantAmount { get; set; } = 20m;

    public Task<CreditsBalanceResult> GetBalanceAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        var balance = _balances.TryGetValue(accessToken, out var value) ? value : InitialBalance;
        return Task.FromResult(new CreditsBalanceResult(balance, null));
    }

    public Task<CreditsClaimApiResult> ClaimDailyAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (!_balances.TryAdd(accessToken, InitialBalance))
        {
            // 已有记录：保持当前余额
        }

        if (_claimed.Contains(accessToken))
        {
            return Task.FromResult(new CreditsClaimApiResult(
                Success: false, AlreadyClaimed: true,
                BalanceAfter: _balances[accessToken], Granted: null, Message: "今日已领取过，明天再来"));
        }

        _claimed.Add(accessToken);
        _balances[accessToken] += GrantAmount;
        return Task.FromResult(new CreditsClaimApiResult(
            Success: true, AlreadyClaimed: false,
            BalanceAfter: _balances[accessToken], Granted: GrantAmount, Message: $"领取成功（+{GrantAmount}）"));
    }
}
