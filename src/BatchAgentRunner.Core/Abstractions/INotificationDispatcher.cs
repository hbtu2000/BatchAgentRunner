using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// 通知分发端口（REQ-018）。接收一组通知规则与上下文，按触发事件与安静时段过滤后推送。
/// 由 Infrastructure 层具体实现（聚合各通道 <see cref="INotifier"/>）。
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>按触发事件与安静时段过滤规则后推送通知。</summary>
    Task DispatchAsync(IEnumerable<NotificationRule> rules, NotificationContext context, CancellationToken cancellationToken = default);
}
