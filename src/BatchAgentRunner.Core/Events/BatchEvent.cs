using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Events;

/// <summary>引擎向界面/日志广播的事件类型（对应 PRD 第 11 章记录项）。</summary>
public enum BatchEventKind
{
    /// <summary>一般信息。</summary>
    Info,
    /// <summary>警告（不阻断执行，但需关注）。</summary>
    Warning,
    /// <summary>任务开始执行。</summary>
    TaskStarted,
    /// <summary>流式输出片段（REQ-007）。</summary>
    TaskOutput,
    /// <summary>任务结束（终态落定）。</summary>
    TaskFinished,
    /// <summary>模型切换（降级矩阵展开）。</summary>
    ModelSwitched,
    /// <summary>账号切换（凭证 Vault）。</summary>
    AccountSwitched,
    /// <summary>免费时段外跳过（预期调度行为，不计失败、不告警）。</summary>
    ModelWindowSkipped,
    /// <summary>额度冷却期跳过组合。</summary>
    ModelCooldownSkipped,
    /// <summary>记录了新的额度冷却（重置时间/来源）。</summary>
    QuotaResetRecorded,
    /// <summary>批次进入等待（等待恢复）。</summary>
    BatchWaiting,
    /// <summary>批次等待被取消（用户操作/终止）。</summary>
    BatchWaitingCancelled,
    /// <summary>批次阻塞（无可用组合且不满足自动等待）。</summary>
    BatchBlocked,
    /// <summary>批次从等待恢复执行。</summary>
    BatchResumed,
    /// <summary>批次被人工终止。</summary>
    BatchTerminated,
    /// <summary>批次正常完成。</summary>
    BatchCompleted,
    /// <summary>发生兜底执行（REQ-004b）。</summary>
    FallbackUsed,
    /// <summary>危险操作确认被拒绝（REQ-002b）。</summary>
    DangerousOperationBlocked,
    /// <summary>自动应答了某个交互点（REQ-010b）。</summary>
    AutoAnswered,
    /// <summary>启动前预检失败（CLI 缺失等环境错误）。</summary>
    PreflightFailed,
    /// <summary>批次状态变化。</summary>
    StatusChanged,
    /// <summary>任务 ID 快照与文件不一致（重复/悬空引用）警告。</summary>
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

    /// <summary>关联任务快照（TaskStarted/Finished 等携带，便于界面直接取用）。</summary>
    public TaskItem? Task { get; init; }

    /// <summary>事件发生时的批次状态（状态类事件携带）。</summary>
    public BatchStatus? BatchStatus { get; init; }

    /// <summary>BatchWaiting 的目标恢复时间点。</summary>
    public DateTimeOffset? ResumeAt { get; init; }

    /// <summary>等待类型（免费时段 / 额度重置 / 混合；非等待事件为 None）。</summary>
    public WaitKind WaitKind { get; init; } = WaitKind.None;

    /// <summary>本次事件涉及的费用（可得时）。</summary>
    public decimal? Cost { get; init; }

    /// <summary>是否涉及危险操作（脱敏与人工复核判据）。</summary>
    public bool IsDangerous { get; init; }

    /// <summary>附加详情（如冷却来源、恢复时间等结构化补充）。</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// 是否为「并发下延后统一切换」（REQ-021）：仅 <see cref="BatchEventKind.AccountSwitched"/>
    /// 时间线条目可能为真，用于区分并发延后切换与常规任务边界切换。
    /// </summary>
    public bool DeferredSwitch { get; init; }
}
