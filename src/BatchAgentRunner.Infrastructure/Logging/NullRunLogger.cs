using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Infrastructure.Logging;

/// <summary>空实现（未指定任务文件时使用）。</summary>
public sealed class NullRunLogger : IRunLogger
{
    /// <summary>空实现恒返回空串，表示没有日志文件。</summary>
    public string LogFilePath => string.Empty;

    /// <summary>空实现恒返回空串，表示没有结构化日志文件。</summary>
    public string StructuredLogFilePath => string.Empty;

    /// <summary>空实现恒返回空串，不参与批次关联。</summary>
    public string RunId => string.Empty;

    /// <summary>空实现：丢弃引擎事件，不落盘。</summary>
    public void Write(BatchEvent ev)
    {
    }

    /// <summary>空实现：丢弃纯文本事件，不落盘。</summary>
    public void Write(string level, string message)
    {
    }

    /// <summary>空实现：丢弃日志条目，不落盘。</summary>
    public void Write(RunLogEntry entry)
    {
    }

    /// <summary>空实现：不记录任务开始。</summary>
    public void WriteTaskStarted(string taskId, string? account, string? model, string? expert, string? workspace)
    {
    }

    /// <summary>空实现：不记录任务结束。</summary>
    public void WriteTaskFinished(string taskId, TaskStatus status, double durationSeconds, int retryCount)
    {
    }

    /// <summary>空实现：不记录账号或模型切换。</summary>
    public void WriteModelSwitched(string? fromAccount, string? fromModel, string? toAccount, string? toModel,
        string reason, string? taskId)
    {
    }

    /// <summary>空实现：不记录免费时段跳过。</summary>
    public void WriteWindowSkipped(string model, DateTimeOffset? nextWindowStart)
    {
    }

    /// <summary>空实现：不记录冷却跳过。</summary>
    public void WriteCooldownSkipped(string account, string model, DateTimeOffset resetAt)
    {
    }

    /// <summary>空实现：不记录额度重置时间。</summary>
    public void WriteQuotaResetRecorded(string account, string model, DateTimeOffset resetAt, CooldownSource source)
    {
    }

    /// <summary>空实现：不记录兜底模型使用。</summary>
    public void WriteFallbackUsed(string taskId, string model, decimal cost, CostBasis basis)
    {
    }

    /// <summary>空实现：不记录批次等待。</summary>
    public void WriteBatchWaiting(DateTimeOffset resumeAt, string? target, WaitKind kind)
    {
    }

    /// <summary>空实现：不记录批次阻塞。</summary>
    public void WriteBatchBlocked(string reason, int pendingCount)
    {
    }

    /// <summary>空实现：不记录批次恢复。</summary>
    public void WriteBatchResumed(string reason)
    {
    }

    /// <summary>空实现：不记录批次终止。</summary>
    public void WriteBatchTerminated(int doneCount, int unfinishedCount)
    {
    }

    /// <summary>空实现：不记录危险操作确认点。</summary>
    public void WriteDangerousOperation(string taskId, string interactionPoint)
    {
    }

    /// <summary>空实现：不记录自动应答。</summary>
    public void WriteAutoAnswered(string taskId, string interactionPoint, string answer)
    {
    }

    /// <summary>空实现：不落盘任务输出。</summary>
    public void WriteTaskOutput(string message)
    {
    }

    /// <summary>空实现：无文件句柄需要释放。</summary>
    public void Dispose()
    {
    }
}
