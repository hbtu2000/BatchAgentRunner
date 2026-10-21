namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 任务终态判定单一事实来源（P2-5 偿还：原 <see cref="TaskItem.IsTerminal"/> /
/// <c>StageResult.IsTerminal</c> / <c>DagScheduler.IsSettled</c> / <c>TaskDispatcher</c> 内联四份各自定义，
/// 新增终态状态时必漏改——收敛为本扩展方法）。
/// </summary>
public static class TaskStatusExtensions
{
    /// <summary>是否为终态（Done / Failed / Interrupted / NeedsAttention / Blocked）。</summary>
    public static bool IsTerminal(this TaskStatus status) =>
        status is TaskStatus.Done or TaskStatus.Failed or TaskStatus.Interrupted
            or TaskStatus.NeedsAttention or TaskStatus.Blocked;
}
