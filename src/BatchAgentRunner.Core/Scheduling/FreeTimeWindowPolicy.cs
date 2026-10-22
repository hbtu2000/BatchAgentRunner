using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 免费时段窗口决策（A-2 / P-03）：无可用组合时，判定「等待恢复」还是「转为阻塞」。
/// 纯函数式，不触发副作用；阻塞原因字符串原样透传给上层用于中断/事件文案，保证与既有行为逐字节一致。
/// </summary>
public readonly record struct WaitDecision(
    bool ShouldWait,
    bool UnboundedQuota,
    DateTimeOffset? RestoreAt,
    string? BlockReason)
{
    /// <summary>构造「等待恢复」决策。</summary>
    /// <param name="restoreAt">恢复时间点。</param>
    /// <param name="unbounded">是否无限等待（额度重置确定可恢复，不受最长可等待时长约束）。</param>
    /// <returns>等待决策。</returns>
    public static WaitDecision Wait(DateTimeOffset restoreAt, bool unbounded) =>
        new(true, unbounded, restoreAt, null);

    /// <summary>构造「转阻塞」决策。</summary>
    /// <param name="reason">阻塞原因（原样透传上层用于事件 / 中断文案）。</param>
    /// <param name="restoreAt">已知的恢复时间点；未知传 null。</param>
    /// <returns>阻塞决策。</returns>
    public static WaitDecision Block(string reason, DateTimeOffset? restoreAt = null) =>
        new(false, false, restoreAt, reason);
}

/// <summary>
/// 免费时段窗口策略对象（A-2 / P-03）：综合「免费时段窗口 + 额度无限等待」判定等待或阻塞。
/// 沿用既有 <see cref="QuotaWaitPolicy.IsUnboundedQuotaWait"/> 单一事实来源，不重复等待窗口语义。
/// </summary>
public sealed class FreeTimeWindowPolicy
{
    private readonly RunOptions _options;
    // 矩阵以延迟委托持有：同 DegradationPolicy，避免读到 NotifyConfigurationChanged() 前的陈旧矩阵。
    private readonly Func<DegradationMatrix> _matrixProvider;
    private readonly AvailabilityEvaluator _evaluator;

    /// <summary>构造免费时段窗口策略。</summary>
    public FreeTimeWindowPolicy(RunOptions options, Func<DegradationMatrix> matrixProvider, AvailabilityEvaluator evaluator)
    {
        _options = options;
        _matrixProvider = matrixProvider ?? throw new ArgumentNullException(nameof(matrixProvider));
        _evaluator = evaluator;
    }

    /// <summary>
    /// 给定最早可恢复点，判定是否等待：
    /// <list type="bullet">
    ///   <item>全部不可用时（<paramref name="earliest"/> 为 null）→ 直接阻塞。</item>
    ///   <item>额度耗尽且开启「无限等待重置」→ 等待（不受 <see cref="RunOptions.MaxWait"/> 约束）。</item>
    ///   <item>最早恢复点落在 <see cref="RunOptions.MaxWait"/> 窗口内 → 等待。</item>
    ///   <item>否则 → 阻塞。</item>
    /// </list>
    /// </summary>
    public WaitDecision DecideWait(RestoreCandidate? earliest, DateTimeOffset now)
    {
        if (earliest is null)
        {
            return WaitDecision.Block("免费组合矩阵当前不可用（额度耗尽 / 时段外 / 冷却中）");
        }

        var unbounded = QuotaWaitPolicy.IsUnboundedQuotaWait(_options, _matrixProvider(), _evaluator, earliest, now);
        if (unbounded || earliest.RestoreAt <= now.Add(_options.MaxWait))
        {
            return WaitDecision.Wait(earliest.RestoreAt, unbounded);
        }

        return WaitDecision.Block(
            $"免费组合矩阵当前不可用，最早恢复时间 {earliest.RestoreAt:HH:mm} 超出最长可等待时长",
            earliest.RestoreAt);
    }
}
