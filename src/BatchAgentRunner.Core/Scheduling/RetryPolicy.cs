namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 重试与退避策略（REQ-005 / REQ-009 / REQ-009a）。
/// 临时限流先退避重试同一组合；额度耗尽才升级为切换。
/// </summary>
public sealed class RetryPolicy
{
    public int MaxRetries { get; set; } = 2;

    public int RateLimitBackoffRetries { get; set; } = 3;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>指数退避：initial × 2^(attempt-1)，上限 MaxBackoff。</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        if (attempt <= 0)
        {
            return TimeSpan.Zero;
        }

        var factor = Math.Pow(2, attempt - 1);
        var seconds = InitialBackoff.TotalSeconds * factor;
        var capped = Math.Min(seconds, MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(capped);
    }
}
