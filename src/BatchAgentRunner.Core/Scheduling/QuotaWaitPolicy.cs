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
