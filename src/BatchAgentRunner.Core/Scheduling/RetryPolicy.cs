namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 重试与退避策略（REQ-005 / REQ-009 / REQ-009a）。
/// 临时限流先退避重试同一组合；额度耗尽才升级为切换。
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>普通失败最大重试次数（默认 2，不含首次执行）。</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>临时限流退避重试次数（默认 3，与 <see cref="MaxRetries"/> 分开计数）。</summary>
    public int RateLimitBackoffRetries { get; set; } = 3;

    /// <summary>指数退避初始间隔（默认 5 秒；第 n 次重试间隔 = 初始值 × 2^(n-1)，上限 <see cref="MaxBackoff"/>）。</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>退避间隔上限（默认 2 分钟）。</summary>
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
