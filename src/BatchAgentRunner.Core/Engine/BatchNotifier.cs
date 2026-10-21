using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 批处理通知协作者（P1-1 拆分产物 ⑤，§3.4 <c>INotifier</c> 侧的引擎装配）：
/// 从 <see cref="BatchEngine"/> 抽出「按触发时机组装 <see cref="NotificationContext"/> 并分发」。
/// 无内部状态（上下文全部由提供函数现算），推送失败只记日志不阻断批处理（REQ-018）。
/// </summary>
public interface IBatchNotifier
{
    Task DispatchAsync(NotifyTrigger? trigger, DateTimeOffset? batchStartedAt = null,
        string? message = null);
}

/// <summary>默认实现：未注入分发器 / 无规则 / trigger 为 null 时为空操作。</summary>
public sealed class BatchNotifier : IBatchNotifier
{
    private readonly RunOptions _options;
    private readonly IReadOnlyList<NotificationRule> _notifications;
    private readonly INotificationDispatcher? _dispatcher;
    private readonly Func<BatchStatus> _status;
    private readonly Func<IReadOnlyList<TaskItem>> _tasks;
    private readonly IClock _clock;
    private readonly IRunLogger _logger;

    public BatchNotifier(
        RunOptions options,
        IReadOnlyList<NotificationRule> notifications,
        INotificationDispatcher? dispatcher,
        Func<BatchStatus> status,
        Func<IReadOnlyList<TaskItem>> tasks,
        IClock clock,
        IRunLogger logger)
    {
        _options = options;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _status = status;
        _tasks = tasks;
        _clock = clock;
        _logger = logger;
    }

    public async Task DispatchAsync(NotifyTrigger? trigger, DateTimeOffset? batchStartedAt = null,
        string? message = null)
    {
        if (trigger is null || _dispatcher is null || _notifications.Count == 0)
        {
            return;
        }

        var now = _clock.Now;
        var tasks = _tasks();
        var context = new NotificationContext
        {
            Trigger = trigger.Value,
            BatchName = string.IsNullOrWhiteSpace(_options.TaskFilePath)
                ? "批处理"
                : Path.GetFileNameWithoutExtension(_options.TaskFilePath),
            BatchStatus = _status(),
            Total = tasks.Count,
            Done = tasks.Count(t => t.Status == TaskStatus.Done),
            Failed = tasks.Count(t => t.Status == TaskStatus.Failed),
            NeedsAttention = tasks.Count(t => t.Status == TaskStatus.NeedsAttention),
            Blocked = tasks.Count(t => t.Status == TaskStatus.Blocked),
            DurationSeconds = batchStartedAt.HasValue
                ? Math.Round((now - batchStartedAt.Value).TotalSeconds, 3)
                : 0,
            Message = message
        };

        try
        {
            await _dispatcher.DispatchAsync(_notifications, context, default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Write(RunLogLevels.Warn, $"通知推送失败（已忽略）：{ex.Message}");
        }
    }
}
