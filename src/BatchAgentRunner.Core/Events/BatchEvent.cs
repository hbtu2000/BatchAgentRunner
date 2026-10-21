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
    IdSnapshotWarning,
    /// <summary>并行下在飞槽位冻结（REQ-021）：在飞任务额度耗尽不即时切换，冻结槽位并进入全局派发暂停。</summary>
    SlotFrozen,
    /// <summary>账号切换延后标记（REQ-021）：账号切换延后至当前在飞任务全部结束后统一执行。</summary>
    AccountSwitchDeferred
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
    /// <summary>
    /// 批次关联标识（ARCH P-10 / A-8）：由 <see cref="Engine.BatchEngine"/> 在广播时统一填充，
    /// 同一次运行的事件共享同一个 RunId，日志与指标据此聚合。
    /// </summary>
    public string? RunId { get; init; }

    public TaskItem? Task { get; init; }

    public BatchStatus? BatchStatus { get; init; }

    /// <summary>BatchWaiting 的目标恢复时间点。</summary>
    public DateTimeOffset? ResumeAt { get; init; }

    public WaitKind WaitKind { get; init; } = WaitKind.None;

    public decimal? Cost { get; init; }

    public bool IsDangerous { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// 是否为「并发下延后统一切换」（REQ-021）：仅 <see cref="BatchEventKind.AccountSwitched"/>
    /// 时间线条目可能为真，用于区分并发延后切换与常规任务边界切换。
    /// </summary>
    public bool DeferredSwitch { get; init; }
}
