using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 「额度耗尽放开等待上限」判定单一事实来源（P2-1 偿还：原 <c>BatchEngine</c> / <c>TaskRunner</c> /
/// <c>WorkflowExecutor</c> 三处逐字相同的谓词收敛于此——等待窗口语义只此一处定义）。
/// 语义：勾选「等待额度重置」且最早恢复点为配额重置型、且矩阵当前不可用纯由额度导致时，
/// 不限最长可等待时长（额度每日必然重置，确定可恢复，DESIGN-额度耗尽暂停续跑）。
/// </summary>
public static class QuotaWaitPolicy
{
    /// <summary>
    /// 是否对「额度耗尽」场景放开等待上限（无限等待直到重置）。
    /// 口径收敛于此（P2-1 偿还：原三处重复谓词）：仅当最早恢复组合的等待类型为额度重置时为真。</summary>
    /// <param name="options">运行配置，其中 WaitForQuotaReset 为放开前提。</param>
    /// <param name="matrix">降级矩阵，用于判定不可用是否纯由额度导致。</param>
    /// <param name="evaluator">组合可用性评估器（时段 / 冷却判定）。</param>
    /// <param name="candidate">最早恢复候选，其等待类型须为「额度重置」。</param>
    /// <param name="now">判定时刻（时段与冷却判定的基准）。</param>
    public static bool IsUnboundedQuotaWait(
        RunOptions options,
        DegradationMatrix matrix,
        AvailabilityEvaluator evaluator,
        RestoreCandidate? candidate,
        DateTimeOffset now) =>
        options.WaitForQuotaReset
        && candidate is { WaitKind: WaitKind.QuotaReset }
        && matrix.AllUnavailablePurelyByQuota(now, evaluator);
}
