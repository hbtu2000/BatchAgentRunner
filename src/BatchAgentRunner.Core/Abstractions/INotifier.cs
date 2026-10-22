using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>通知上下文（REQ-018）：批处理事件触发时携带的数据。</summary>
public sealed class NotificationContext
{
    /// <summary>触发事件类型。</summary>
    public NotifyTrigger Trigger { get; init; }
    /// <summary>批次名称（任务文件名）。</summary>
    public string BatchName { get; init; } = string.Empty;
    /// <summary>触发时的批次状态。</summary>
    public BatchStatus BatchStatus { get; init; }
    /// <summary>任务总数。</summary>
    public int Total { get; init; }
    /// <summary>成功完成数。</summary>
    public int Done { get; init; }
    /// <summary>失败数。</summary>
    public int Failed { get; init; }
    /// <summary>待人工数。</summary>
    public int NeedsAttention { get; init; }
    /// <summary>阻塞数。</summary>
    public int Blocked { get; init; }
    /// <summary>批次耗时（秒）。</summary>
    public double DurationSeconds { get; init; }
    /// <summary>报告文件路径（可得时）。</summary>
    public string? ReportPath { get; init; }
    /// <summary>下一恢复时间（阻塞类通知携带，人读文本）。</summary>
    public string? NextRestoreAt { get; init; }
    /// <summary>附加消息（如阻塞原因）。</summary>
    public string? Message { get; init; }
}

/// <summary>通知推送端口（REQ-018）。</summary>
public interface INotifier
{
    /// <summary>本派发器负责的通道。</summary>
    NotificationChannel Channel { get; }

    /// <summary>按规则推送通知（失败不阻断批处理，仅记日志）。</summary>
    Task NotifyAsync(NotificationRule rule, NotificationContext context, CancellationToken cancellationToken = default);
}
