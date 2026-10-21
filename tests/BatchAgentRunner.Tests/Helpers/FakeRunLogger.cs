using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;

namespace BatchAgentRunner.Tests.Helpers;

/// <summary>
/// 内存运行日志（ARCH P-10）：既录结构化条目也录渲染后的文本行。
/// </summary>
/// <remarks>
/// P-10 之前测试里唯一的内存实现是 <c>CreditsClaimTests.RecordingRunLogger</c>（只录文本、私有嵌套类），
/// 「结构化通道是否被走到」「关联字段是否带上」这类断言根本无处下手。
/// 本替身把结构化条目原样留下，使指标快照 / 脱敏结果 / RunId 关联可直接断言，无需解析文本。
/// </remarks>
public sealed class FakeRunLogger : IRunLogger
{
    public string LogFilePath => string.Empty;

    public string StructuredLogFilePath => string.Empty;

    public string RunId { get; init; } = RunLogEntry.NewRunId();

    /// <summary>按写入顺序记录的结构化条目。</summary>
    public List<RunLogEntry> Entries { get; } = new();

    /// <summary>渲染后的文本行（与落盘文本同源）。</summary>
    public List<string> Lines => Entries.Select(e => e.RenderBody()).ToList();

    public void Write(RunLogEntry entry) => Entries.Add(entry);

    public void Write(string level, string message) =>
        Write(new RunLogEntry(RunLogLevels.Normalize(level), string.Empty, message));

    public void Write(BatchEvent ev) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.FromKind(ev.Kind), ev.Message ?? string.Empty)
        {
            At = ev.At,
            TaskId = ev.TaskId,
            Account = ev.Account,
            Model = ev.Model,
            IsDangerous = ev.IsDangerous
        });

    /// <summary>取指定事件名的最后一条条目（便于断言指标快照等一次性记录）。</summary>
    public RunLogEntry? LastOf(string eventName) => Entries.LastOrDefault(e => e.Event == eventName);

    public void WriteTaskStarted(string taskId, string? account, string? model, string? expert, string? workspace) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.TaskStarted, $"任务 {taskId}"));

    public void WriteTaskFinished(string taskId, TaskStatus status, double durationSeconds, int retryCount) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.TaskFinished, $"任务 {taskId} | 状态 {status}"));

    public void WriteModelSwitched(string? fromAccount, string? fromModel, string? toAccount, string? toModel,
        string reason, string? taskId) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.ModelSwitched, $"{fromModel} → {toModel}"));

    public void WriteWindowSkipped(string model, DateTimeOffset? nextWindowStart) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.WindowSkipped, model));

    public void WriteCooldownSkipped(string account, string model, DateTimeOffset resetAt) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.CooldownSkipped, $"{account} × {model}"));

    public void WriteQuotaResetRecorded(string account, string model, DateTimeOffset resetAt, CooldownSource source) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.QuotaResetRecorded, $"{account} × {model}"));

    public void WriteFallbackUsed(string taskId, string model, decimal cost, CostBasis basis) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.FallbackUsed, $"任务 {taskId} | {model}"));

    public void WriteBatchWaiting(DateTimeOffset resumeAt, string? target, WaitKind kind) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.BatchWaiting, kind.ToString()));

    public void WriteBatchBlocked(string reason, int pendingCount) =>
        Write(new RunLogEntry(RunLogLevels.Error, LogEventNames.BatchBlocked, reason));

    public void WriteBatchResumed(string reason) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.BatchResumed, reason));

    public void WriteBatchTerminated(int doneCount, int unfinishedCount) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.BatchTerminated, $"已完成 {doneCount}"));

    public void WriteDangerousOperation(string taskId, string interactionPoint) =>
        Write(new RunLogEntry(RunLogLevels.Danger, LogEventNames.DangerousOperation, $"任务 {taskId}"));

    public void WriteAutoAnswered(string taskId, string interactionPoint, string answer) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.AutoAnswered, $"任务 {taskId}"));

    public void WriteTaskOutput(string message) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.TaskOutput, message));

    public void Dispose()
    {
    }
}
