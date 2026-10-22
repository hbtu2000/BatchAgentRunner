using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Infrastructure.Logging;

/// <summary>
/// 运行日志实现（REQ-014 + PRD 第 11 章「本地工具以运行日志替代埋点」）。
/// ARCH P-10 收口：结构化接线 + RunId 关联 + 统一脱敏。
/// </summary>
/// <remarks>
/// 治理前十余个 <c>Write*</c> 方法各自拼一行文本直接刷盘：事件名、任务、账号、明细全挤在字符串里，
/// 日志只能 <c>tail</c> 看，既不能按批次关联、也不能按字段采集，脱敏更无从统一施加。
/// 收口后所有写入一律先落成 <see cref="RunLogEntry"/>，再由
/// <see cref="Write(RunLogEntry)"/> 这一<b>唯一落盘点</b>渲染出两路输出：
/// <list type="bullet">
///   <item><b>文本</b>（<c>run-*.log</c>）：人读 tail，正文与收口前逐字一致；</item>
///   <item><b>结构化</b>（<c>run-*.jsonl</c>）：机器采集，带 <c>run_id / task_id / account / model / fields</c>。</item>
/// </list>
/// 安全约定：正文字段在渲染入口经 <see cref="SecretMasker"/> 统一脱敏，写入点无法绕过。
/// </remarks>
public sealed class FileRunLogger : IRunLogger
{
    /// <summary>P2-24：INFO 级批量落盘阈值——原每条事件 AutoFlush 同步刷盘，
    /// CLI 高频输出（TaskOutput 分片）下syscall 风暴；现按批落盘，重要级别仍即时刷盘。</summary>
    private const int FlushBatchLines = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private readonly StreamWriter _structuredWriter;
    private int _pendingLines;
    private bool _disposed;

    /// <summary>打开 run-*.log 与 run-*.jsonl，并按任务文件或默认 runs 目录定位。</summary>
    public FileRunLogger(string? taskFilePath = null, string? logDirectory = null, string? runId = null)
    {
        var dir = logDirectory ?? (!string.IsNullOrWhiteSpace(taskFilePath)
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(taskFilePath)) ?? ".", "runs")
            : AppPaths.RunsDirectory);

        Directory.CreateDirectory(dir);
        RunId = string.IsNullOrWhiteSpace(runId) ? RunLogEntry.NewRunId() : runId.Trim();

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        LogFilePath = Path.Combine(dir, $"run-{stamp}.log");
        StructuredLogFilePath = Path.Combine(dir, $"run-{stamp}.jsonl");
        _writer = new StreamWriter(new FileStream(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));
        _structuredWriter = new StreamWriter(
            new FileStream(StructuredLogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));

        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.RunStarted,
            $"运行日志开始：{DateTime.Now:yyyy-MM-dd HH:mm:ss}")
        { RunId = RunId, Flush = true });
    }

    /// <summary>人读文本日志路径（run-时间戳.log）。</summary>
    public string LogFilePath { get; }

    /// <summary>结构化日志路径（run-时间戳.jsonl，一行一条 JSON）。</summary>
    public string StructuredLogFilePath { get; }

    /// <summary>本次运行的唯一标识，写入每条日志以支持按批次关联。</summary>
    public string RunId { get; }

    /// <summary>按级别写入一条纯文本事件，事件名留空。</summary>
    /// <param name="level">日志级别，写入前会归一化。</param>
    /// <param name="message">日志正文，落盘前统一脱敏。</param>
    public void Write(string level, string message) =>
        Write(new RunLogEntry(RunLogLevels.Normalize(level), string.Empty, message));

    /// <summary>把引擎事件转成日志条目落盘；任务输出事件跳过，避免重复。</summary>
    public void Write(BatchEvent ev)
    {
        // 任务输出（TaskOutput）改由适配器在 CliAdapterBase.ExecuteAsync 的 onOut 回调里
        // 经 WriteTaskOutput 实时落盘（即时刷盘、带时间戳），此处跳过以免与适配器直写重复。
        if (ev.Kind == BatchEventKind.TaskOutput)
        {
            return;
        }

        if (string.IsNullOrEmpty(ev.Message))
        {
            return;
        }

        Write(new RunLogEntry(RunLogLevels.Normalize(MapLevel(ev.Kind)), LogEventNames.FromKind(ev.Kind), ev.Message)
        {
            At = ev.At,
            TaskId = ev.TaskId,
            Account = ev.Account,
            Model = ev.Model,
            // 危险操作点醒目标记（REQ-010b）
            IsDangerous = ev.IsDangerous,
            Fields = BuildFields(ev)
        });
    }

    /// <summary>
    /// 任务执行期间逐行实时落盘（REQ：实时可观测）。即时刷盘，使 runs/run-*.log 可作为「实时 tail」
    /// 观测 CLI stream-json 输出，不再依赖批次结束才批量刷盘（修复 18:44 批次「看似无实时输出」问题）。
    /// 仅由 CliAdapterBase.ExecuteAsync 任务执行路径调用；预检/列模型路径不触发。
    /// </summary>
    public void WriteTaskOutput(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.TaskOutput, message) { Flush = true });
    }

    /// <summary>记录任务开始，含账号、模型、专家与工作目录。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="account">账号标识；null 显示为 -。</param>
    /// <param name="model">模型名；null 显示为 -。</param>
    /// <param name="expert">专家 Id；null 显示为 -。</param>
    /// <param name="workspace">工作目录；null 显示为 -。</param>
    public void WriteTaskStarted(string taskId, string? account, string? model, string? expert, string? workspace) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.TaskStarted,
            $"任务 {taskId} | 账号 {account ?? "-"} | 模型 {model ?? "-"} | 专家 {expert ?? "-"} | 空间 {workspace ?? "-"}")
        { TaskId = taskId, Account = account, Model = model });

    /// <summary>记录任务结束，含终态、耗时与重试次数。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="status">任务终态（成功 / 失败 / 跳过等）。</param>
    /// <param name="durationSeconds">执行耗时（秒）。</param>
    /// <param name="retryCount">已重试次数。</param>
    public void WriteTaskFinished(string taskId, TaskStatus status, double durationSeconds, int retryCount) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.TaskFinished,
            $"任务 {taskId} | 状态 {status} | 耗时 {durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s | 重试 {retryCount}")
        {
            TaskId = taskId,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = status.ToString(),
                ["duration_seconds"] = RunLogEntry.Num(durationSeconds),
                ["retry_count"] = retryCount.ToString(CultureInfo.InvariantCulture)
            }
        });

    /// <summary>记录账号/模型切换，含切换前后的组合与原因。</summary>
    /// <param name="fromAccount">切换前账号。</param>
    /// <param name="fromModel">切换前模型。</param>
    /// <param name="toAccount">切换后账号。</param>
    /// <param name="toModel">切换后模型。</param>
    /// <param name="reason">触发切换的原因。</param>
    /// <param name="taskId">关联任务；null 显示为 -。</param>
    public void WriteModelSwitched(string? fromAccount, string? fromModel, string? toAccount, string? toModel,
        string reason, string? taskId) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.ModelSwitched,
            $"任务 {taskId ?? "-"} | {fromAccount}+{fromModel} → {toAccount}+{toModel} | 原因 {reason}")
        { TaskId = taskId, Account = toAccount, Model = toModel });

    /// <summary>记录因不在免费时段而跳过，含下一可用时段。</summary>
    /// <param name="model">被跳过的模型。</param>
    /// <param name="nextWindowStart">下一可用时段起点；null 显示为 -。</param>
    public void WriteWindowSkipped(string model, DateTimeOffset? nextWindowStart) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.WindowSkipped,
            $"模型 {model} | 下一可用时段 {nextWindowStart?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-"}")
        { Model = model });

    /// <summary>记录因额度冷却而跳过，含预计恢复时间。</summary>
    /// <param name="account">账号标识。</param>
    /// <param name="model">模型名。</param>
    /// <param name="resetAt">预计恢复时间。</param>
    public void WriteCooldownSkipped(string account, string model, DateTimeOffset resetAt) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.CooldownSkipped,
            $"{account} × {model} | 预计恢复 {resetAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}")
        { Account = account, Model = model });

    /// <summary>记录额度重置时间，并标注来源是 CLI 解析还是默认冷却。</summary>
    /// <param name="account">账号标识。</param>
    /// <param name="model">模型名。</param>
    /// <param name="resetAt">额度重置时间。</param>
    /// <param name="source">重置时间来源：CLI 解析或默认冷却。</param>
    public void WriteQuotaResetRecorded(string account, string model, DateTimeOffset resetAt, CooldownSource source) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.QuotaResetRecorded,
            $"{account} × {model} | 重置时间 {resetAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} | 来源 {(source == CooldownSource.CliParsed ? "CLI 解析" : "默认冷却")}")
        { Account = account, Model = model });

    /// <summary>记录启用兜底模型，含花费与计价依据。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="model">兜底模型名。</param>
    /// <param name="cost">本次花费（元）。</param>
    /// <param name="basis">费用口径：实测或估算。</param>
    public void WriteFallbackUsed(string taskId, string model, decimal cost, CostBasis basis) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.FallbackUsed,
            $"任务 {taskId} | 兜底模型 {model} | 花费 ¥{cost.ToString("0.####", CultureInfo.InvariantCulture)}（{(basis == CostBasis.Measured ? "实测" : "估算")}）")
        {
            TaskId = taskId,
            Model = model,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cost"] = RunLogEntry.Num(cost),
                ["cost_basis"] = basis.ToString()
            }
        });

    /// <summary>记录批次进入等待，含恢复时间、等待目标与类型。</summary>
    /// <param name="resumeAt">预计恢复时间。</param>
    /// <param name="target">等待目标（账号或模型）；null 显示为 -。</param>
    /// <param name="kind">等待类型（冷却 / 时段等）。</param>
    public void WriteBatchWaiting(DateTimeOffset resumeAt, string? target, WaitKind kind) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.BatchWaiting,
            $"等待至 {resumeAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} | 目标 {target ?? "-"} | 类型 {kind}")
        { Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["wait_kind"] = kind.ToString() } });

    /// <summary>以 ERROR 级记录批次阻塞，含原因与待续跑任务数。</summary>
    /// <param name="reason">阻塞原因。</param>
    /// <param name="pendingCount">待续跑任务数。</param>
    public void WriteBatchBlocked(string reason, int pendingCount) =>
        Write(new RunLogEntry(RunLogLevels.Error, LogEventNames.BatchBlocked,
            $"原因：{reason} | 待续跑 {pendingCount} 条"));

    /// <summary>记录批次从等待或阻塞中恢复。</summary>
    /// <param name="reason">恢复原因。</param>
    public void WriteBatchResumed(string reason) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.BatchResumed, reason));

    /// <summary>记录批次被终止，含已完成与未完成数量。</summary>
    /// <param name="doneCount">已完成任务数。</param>
    /// <param name="unfinishedCount">未完成任务数。</param>
    public void WriteBatchTerminated(int doneCount, int unfinishedCount) =>
        Write(new RunLogEntry(RunLogLevels.Warn, LogEventNames.BatchTerminated,
            $"已完成 {doneCount} 条 | 未完成 {unfinishedCount} 条"));

    /// <summary>以最高级别记录危险确认点，并标明已拒绝自动批准。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="interactionPoint">命中的交互提示文本。</param>
    public void WriteDangerousOperation(string taskId, string interactionPoint) =>
        Write(new RunLogEntry(RunLogLevels.Danger, LogEventNames.DangerousOperation,
            $"*** 高危确认点 *** 任务 {taskId} | 交互点：{interactionPoint} | 已拒绝自动批准")
        { TaskId = taskId });

    /// <summary>记录已自动应答的交互点及所填答案。</summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="interactionPoint">发生自动应答的交互提示文本。</param>
    /// <param name="answer">自动填入的答案。</param>
    public void WriteAutoAnswered(string taskId, string interactionPoint, string answer) =>
        Write(new RunLogEntry(RunLogLevels.Info, LogEventNames.AutoAnswered,
            $"任务 {taskId} | 交互点：{interactionPoint} | 默认选项：{answer}")
        { TaskId = taskId });

    /// <summary>
    /// 唯一落盘点：脱敏 → 渲染文本 → 渲染结构化 JSON → 按策略刷盘。
    /// 任何新增写入方法都必须转条目后经此进入，否则会绕过脱敏与结构化（由 <c>ObservabilityHygieneTests</c> 拦截）。
    /// </summary>
    public void Write(RunLogEntry entry)
    {
        var at = entry.At == default ? DateTimeOffset.Now : entry.At;
        var level = RunLogLevels.Normalize(entry.Level);
        var rendered = entry with
        {
            At = at,
            Level = level,
            RunId = string.IsNullOrEmpty(entry.RunId) ? RunId : entry.RunId,
            Message = SecretMasker.Mask(entry.Message),
            Fields = MaskFields(entry.Fields)
        };

        // 文本行沿用收口前格式：[HH:mm:ss] LEVEL {正文}
        var line = $"[{at.ToLocalTime():HH:mm:ss}] {level,-5} {rendered.RenderBody()}";
        WriteLine(line, ToJson(rendered), RunLogLevels.IsImportant(level) || entry.Flush);
    }

    /// <summary>引擎事件的附加结构化字段（成本 / 批状态 / 等待类型 / 明细）。</summary>
    private static Dictionary<string, string>? BuildFields(BatchEvent ev)
    {
        Dictionary<string, string>? fields = null;

        void Add(string key, string value)
        {
            fields ??= new Dictionary<string, string>(StringComparer.Ordinal);
            fields[key] = value;
        }

        if (ev.Cost.HasValue)
        {
            Add("cost", RunLogEntry.Num(ev.Cost.Value));
        }

        if (ev.BatchStatus.HasValue)
        {
            Add("batch_status", ev.BatchStatus.Value.ToString());
        }

        if (ev.WaitKind != WaitKind.None)
        {
            Add("wait_kind", ev.WaitKind.ToString());
        }

        if (!string.IsNullOrEmpty(ev.Detail))
        {
            Add("detail", ev.Detail!);
        }

        if (ev.DeferredSwitch)
        {
            Add("deferred_switch", "true");
        }

        return fields;
    }

    private static Dictionary<string, string>? MaskFields(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is not { Count: > 0 })
        {
            return null;
        }

        var masked = new Dictionary<string, string>(fields.Count, StringComparer.Ordinal);
        foreach (var pair in fields)
        {
            masked[pair.Key] = SecretMasker.Mask(pair.Value);
        }

        return masked;
    }

    /// <summary>结构化行（JSON Lines）：一行一条，含关联字段，供采集端直接解析。</summary>
    private static string ToJson(RunLogEntry entry)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ts"] = entry.At.ToString("o", CultureInfo.InvariantCulture),
            ["level"] = entry.Level,
            ["event"] = entry.Event,
            ["run_id"] = entry.RunId,
            ["task_id"] = entry.TaskId,
            ["account"] = entry.Account,
            ["model"] = entry.Model,
            ["message"] = entry.Message
        };

        if (entry.IsDangerous)
        {
            payload["dangerous"] = true;
        }

        if (entry.Fields is { Count: > 0 })
        {
            payload["fields"] = entry.Fields;
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string MapLevel(BatchEventKind kind) => kind switch
    {
        BatchEventKind.DangerousOperationBlocked => RunLogLevels.Danger,
        BatchEventKind.BatchBlocked or BatchEventKind.PreflightFailed => RunLogLevels.Error,
        BatchEventKind.TaskFinished or BatchEventKind.BatchCompleted or BatchEventKind.StatusChanged
            or BatchEventKind.TaskStarted or BatchEventKind.TaskOutput => RunLogLevels.Info,
        BatchEventKind.Warning or BatchEventKind.ModelSwitched or BatchEventKind.AccountSwitched
            or BatchEventKind.BatchWaiting or BatchEventKind.BatchTerminated or BatchEventKind.QuotaResetRecorded
            or BatchEventKind.FallbackUsed => RunLogLevels.Warn,
        _ => RunLogLevels.Info
    };

    private void WriteLine(string line, string structuredLine, bool important)
    {
        StreamWriter writer;
        StreamWriter structuredWriter;
        lock (_sync)
        {
            // 释放后到达的延迟写入（CLI 子进程 AsyncStreamReader 在线程池排队）直接丢弃，
            // 不进入已关闭的 writer，避免抛 ObjectDisposedException
            // （2026-10-18 实测崩溃根因：Cannot write to a closed TextWriter）。
            if (_disposed)
            {
                return;
            }

            writer = _writer;
            structuredWriter = _structuredWriter;
        }

        // 双重保护：即便在 lock 释放后、Dispose 抢先关闭 writer 的极小窗口内拿到引用，
        // WriteLine 抛出的 ObjectDisposedException / IOException 也必须在此吞掉，
        // 否则异常会经 Process.AsyncStreamReader 在新建线程池线程上 re-throw，
        // 升级为 AppDomain 未处理异常并终止进程。
        try
        {
            writer.WriteLine(line);
            structuredWriter.WriteLine(structuredLine);
            // P2-24：重要级别即时刷盘（配额重置/兜底/阻塞/高危不因批量滞后丢失）；
            // INFO 累积到阈值才刷盘，削平 CLI 高频输出分片的 syscall 风暴。
            if (important || ++_pendingLines >= FlushBatchLines)
            {
                writer.Flush();
                structuredWriter.Flush();
                _pendingLines = 0;
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>刷盘并关闭文本与结构化两路日志文件，重复调用安全。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer.Flush();
                _structuredWriter.Flush();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }

            _writer.Dispose();
            _structuredWriter.Dispose();
        }
    }
}
