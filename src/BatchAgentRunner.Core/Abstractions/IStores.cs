using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// 任务文件读写端口（REQ-001 / REQ-006）。
/// JSON：原地原子回写；Markdown：只读输入 + sidecar 结果文件。
/// </summary>
public interface ITaskFileStore
{
    /// <summary>加载任务清单；格式非法时抛出 <see cref="TaskFileFormatException"/> 并定位到具体任务/行。</summary>
    Task<TaskFileLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>原子写回（临时文件 + 替换）。Markdown 写 sidecar，不改动用户原文。</summary>
    Task SaveAsync(string path, IReadOnlyList<TaskItem> tasks, CancellationToken cancellationToken = default);

    /// <summary>启动前自动备份（REQ-006，默认保留最近 5 份）。</summary>
    Task<string?> BackupAsync(string path, int retention, CancellationToken cancellationToken = default);

    /// <summary>状态/结果实际写入位置（JSON 为自身，Markdown 为 sidecar 路径）。</summary>
    string ResolveResultPath(string path);

    /// <summary>是否为 Markdown 任务清单（.md/.markdown）。</summary>
    bool IsMarkdown(string path);
}

/// <summary>任务清单加载结果（含加载期非致命问题清单）。</summary>
public sealed record TaskFileLoadResult(
    IReadOnlyList<TaskItem> Tasks,
    string ResultPath,
    bool IsMarkdown,
    IReadOnlyList<TaskFileIssue> Issues);

/// <summary>加载期的非致命问题（如缺失 ID 已自动生成），需提示用户。</summary>
public sealed record TaskFileIssue(string Level, string TaskId, string Message);

/// <summary>任务文件格式非法（定位到行/任务，便于用户直接修）。</summary>
public sealed class TaskFileFormatException : Exception
{
    /// <summary>构造格式异常（行号与任务 id 至少给一个，用于精确定位）。</summary>
    public TaskFileFormatException(string message, int? line = null, string? taskId = null)
        : base(message)
    {
        Line = line;
        TaskId = taskId;
    }

    /// <summary>出错行号（可得时）。</summary>
    public int? Line { get; }

    /// <summary>出错任务 id（可得时）。</summary>
    public string? TaskId { get; }

    /// <summary>带定位前缀的错误消息（第 N 行 / 任务 X：原消息）。</summary>
    public override string Message =>
        (Line.HasValue ? $"第 {Line} 行" : TaskId is not null ? $"任务 {TaskId}" : "任务文件") + "：" + base.Message;
}

/// <summary>冷却记录持久化（REQ-004c：重启后仍然有效）。</summary>
public interface ICooldownStore
{
    /// <summary>加载全部冷却记录。</summary>
    Task<IReadOnlyList<QuotaCooldownRecord>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存冷却记录（全量覆写）。</summary>
    Task SaveAsync(IEnumerable<QuotaCooldownRecord> records, CancellationToken cancellationToken = default);
}

/// <summary>运行配置持久化。</summary>
public interface IConfigStore
{
    /// <summary>加载配置（文件缺失/损坏时回落默认值）。</summary>
    Task<RunOptions> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存配置（原子写）。</summary>
    Task SaveAsync(RunOptions options, CancellationToken cancellationToken = default);

    /// <summary>配置所在目录。</summary>
    string ConfigDirectory { get; }
}

/// <summary>汇总报告写出（REQ-011，P1）。</summary>
public interface IReportWriter
{
    /// <summary>写出报告，返回文件路径。</summary>
    Task<string> WriteAsync(BatchReport report, string taskFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// 运行级指标写出（ARCH P-10 / A-8）：报告给用户看，指标给运维看，二者互补但分离落盘。
/// </summary>
public interface IMetricsWriter
{
    /// <summary>落盘 <c>metrics-&lt;run_id&gt;.json</c>（与报告同目录），返回文件路径。</summary>
    Task<string> WriteAsync(RunMetricsSnapshot snapshot, string taskFilePath,
        CancellationToken cancellationToken = default);
}

/// <summary>自动关机（REQ-015，P1）：可撤销倒计时 + 权限不足降级。</summary>
public interface IShutdownService
{
    /// <summary>当前平台是否支持自动关机。</summary>
    bool IsSupported { get; }

    /// <summary>尝试关机（返回是否已下达关机指令）。</summary>
    Task<bool> TryShutdownAsync(CancellationToken cancellationToken = default);
}
