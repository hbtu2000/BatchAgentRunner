using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// 运行日志（REQ-014，P1；同时承担 PRD 第 11 章「本地工具以运行日志替代埋点」的职责）。
/// 安全约定：不得写入凭据明文。
/// </summary>
public interface IRunLogger : IDisposable
{
    string LogFilePath { get; }

    void Write(BatchEvent ev);

    void Write(string level, string message);

    void WriteTaskStarted(string taskId, string? account, string? model, string? expert, string? workspace);

    void WriteTaskFinished(string taskId, BatchAgentRunner.Core.Domain.TaskStatus status, double durationSeconds, int retryCount);

    void WriteModelSwitched(string? fromAccount, string? fromModel, string? toAccount, string? toModel,
        string reason, string? taskId);

    void WriteWindowSkipped(string model, DateTimeOffset? nextWindowStart);

    void WriteCooldownSkipped(string account, string model, DateTimeOffset resetAt);

    void WriteQuotaResetRecorded(string account, string model, DateTimeOffset resetAt, CooldownSource source);

    void WriteFallbackUsed(string taskId, string model, decimal cost, CostBasis basis);

    void WriteBatchWaiting(DateTimeOffset resumeAt, string? target, WaitKind kind);

    void WriteBatchBlocked(string reason, int pendingCount);

    void WriteBatchResumed(string reason);

    void WriteBatchTerminated(int doneCount, int unfinishedCount);

    void WriteDangerousOperation(string taskId, string interactionPoint);

    void WriteAutoAnswered(string taskId, string interactionPoint, string answer);
}
