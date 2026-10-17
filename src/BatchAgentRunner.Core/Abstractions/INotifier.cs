using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>通知上下文（REQ-018）：批处理事件触发时携带的数据。</summary>
public sealed class NotificationContext
{
    public NotifyTrigger Trigger { get; init; }
    public string BatchName { get; init; } = string.Empty;
    public BatchStatus BatchStatus { get; init; }
    public int Total { get; init; }
    public int Done { get; init; }
    public int Failed { get; init; }
    public int NeedsAttention { get; init; }
    public int Blocked { get; init; }
    public double DurationSeconds { get; init; }
    public string? ReportPath { get; init; }
    public string? NextRestoreAt { get; init; }
    public string? Message { get; init; }
}

/// <summary>通知推送端口（REQ-018）。</summary>
public interface INotifier
{
    NotificationChannel Channel { get; }

    Task NotifyAsync(NotificationRule rule, NotificationContext context, CancellationToken cancellationToken = default);
}
