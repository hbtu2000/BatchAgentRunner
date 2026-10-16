using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Events;

/// <summary>引擎向界面/日志广播的事件类型（对应 PRD 第 11 章记录项）。</summary>
public enum BatchEventKind
{
    Info,
    Warning,
    TaskStarted,
    /// <summary>流式输出片段（REQ-007）。</summary>
    TaskOutput,
    TaskFinished,
    ModelSwitched,
    AccountSwitched,
    /// <summary>免费时段外跳过（预期调度行为，不计失败、不告警）。</summary>
    ModelWindowSkipped,
    /// <summary>额度冷却期跳过组合。</summary>
    ModelCooldownSkipped,
    QuotaResetRecorded,
    BatchWaiting,
    BatchWaitingCancelled,
    BatchBlocked,
    BatchResumed,
    BatchTerminated,
    BatchCompleted,
    FallbackUsed,
    /// <summary>危险操作确认被拒绝（REQ-002b）。</summary>
    DangerousOperationBlocked,
    /// <summary>自动应答了某个交互点（REQ-010b）。</summary>
    AutoAnswered,
    PreflightFailed,
    StatusChanged,
    IdSnapshotWarning
}

/// <summary>
/// 引擎事件。界面通过订阅事件流实现 ≤2 秒刷新（事件驱动，非轮询）。
/// </summary>
public sealed record BatchEvent(
    BatchEventKind Kind,
    DateTimeOffset At,
    string Message,
    string? TaskId = null,
    string? Account = null,
    string? Model = null)
{
    public TaskItem? Task { get; init; }

    public BatchStatus? BatchStatus { get; init; }

    /// <summary>BatchWaiting 的目标恢复时间点。</summary>
    public DateTimeOffset? ResumeAt { get; init; }

    public WaitKind WaitKind { get; init; } = WaitKind.None;

    public decimal? Cost { get; init; }

    public bool IsDangerous { get; init; }

    public string? Detail { get; init; }
}
