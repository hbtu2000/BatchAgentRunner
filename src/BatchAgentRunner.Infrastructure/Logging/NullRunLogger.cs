using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Infrastructure.Logging;

/// <summary>空实现（未指定任务文件时使用）。</summary>
public sealed class NullRunLogger : IRunLogger
{
    public string LogFilePath => string.Empty;

    public string StructuredLogFilePath => string.Empty;

    public string RunId => string.Empty;

    public void Write(BatchEvent ev)
    {
    }

    public void Write(string level, string message)
    {
    }

    public void Write(RunLogEntry entry)
    {
    }

    public void WriteTaskStarted(string taskId, string? account, string? model, string? expert, string? workspace)
    {
    }

    public void WriteTaskFinished(string taskId, TaskStatus status, double durationSeconds, int retryCount)
    {
    }

    public void WriteModelSwitched(string? fromAccount, string? fromModel, string? toAccount, string? toModel,
        string reason, string? taskId)
    {
    }

    public void WriteWindowSkipped(string model, DateTimeOffset? nextWindowStart)
    {
    }

    public void WriteCooldownSkipped(string account, string model, DateTimeOffset resetAt)
    {
    }

    public void WriteQuotaResetRecorded(string account, string model, DateTimeOffset resetAt, CooldownSource source)
    {
    }

    public void WriteFallbackUsed(string taskId, string model, decimal cost, CostBasis basis)
    {
    }

    public void WriteBatchWaiting(DateTimeOffset resumeAt, string? target, WaitKind kind)
    {
    }

    public void WriteBatchBlocked(string reason, int pendingCount)
    {
    }

    public void WriteBatchResumed(string reason)
    {
    }

    public void WriteBatchTerminated(int doneCount, int unfinishedCount)
    {
    }

    public void WriteDangerousOperation(string taskId, string interactionPoint)
    {
    }

    public void WriteAutoAnswered(string taskId, string interactionPoint, string answer)
    {
    }

    public void WriteTaskOutput(string message)
    {
    }

    public void Dispose()
    {
    }
}
