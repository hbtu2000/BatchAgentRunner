using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// 运行日志（REQ-014，P1；同时承担 PRD 第 11 章「本地工具以运行日志替代埋点」的职责）。
/// 安全约定：不得写入凭据明文（ARCH P-10：由实现侧在渲染入口统一脱敏）。
/// </summary>
public interface IRunLogger : IDisposable
{
    /// <summary>文本文件落点（人读 tail）。</summary>
    string LogFilePath { get; }

    /// <summary>
    /// 结构化日志落点（机器采集，JSON Lines，与 <see cref="LogFilePath"/> 同目录同名）。
    /// 空串表示实现不支持结构化输出。
    /// </summary>
    string StructuredLogFilePath { get; }

    /// <summary>
    /// 批次关联标识：一次运行一个，贯穿日志条目与运行级指标（ARCH P-10 / A-8）。
    /// 引擎与日志共用同一实例，杜绝"日志一个 id、指标另一个 id"的关联断裂。
    /// </summary>
    string RunId { get; }

    void Write(BatchEvent ev);

    void Write(string level, string message);

    /// <summary>
    /// 结构化写入通道：一切日志事实的<b>唯一落盘点</b>（ARCH P-10）。
    /// 其余 <c>Write*</c> 便捷方法语义不变，实现侧一律转成条目后经本方法渲染，
    /// 使「脱敏 / 关联字段 / 结构化落盘」只需在一处生效，新增写入点不可能绕过。
    /// </summary>
    void Write(RunLogEntry entry);

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

    /// <summary>
    /// 任务执行期间逐行实时落盘（REQ：实时可观测）。与 <see cref="Write(BatchEvent)"/> 中
    /// <see cref="BatchEventKind.TaskOutput"/> 互斥——后者已跳过以避免与适配器直写重复。
    /// 该方法即时刷盘，保证运行日志可作为「实时 tail」观测 CLI 输出。
    /// </summary>
    void WriteTaskOutput(string message);
}
