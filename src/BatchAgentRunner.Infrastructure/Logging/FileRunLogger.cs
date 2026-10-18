using System.Globalization;
using System.Text;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Infrastructure.Logging;

/// <summary>
/// 运行日志实现（REQ-014 + PRD 第 11 章「本地工具以运行日志替代埋点」）。
/// 安全约定：不写入凭据明文。
/// </summary>
public sealed class FileRunLogger : IRunLogger
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public FileRunLogger(string? taskFilePath = null, string? logDirectory = null)
    {
        var dir = logDirectory ?? (!string.IsNullOrWhiteSpace(taskFilePath)
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(taskFilePath)) ?? ".", "runs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BatchAgentRunner", "runs"));

        Directory.CreateDirectory(dir);
        LogFilePath = Path.Combine(dir, $"run-{DateTime.Now:yyyyMMddHHmmss}.log");
        _writer = new StreamWriter(new FileStream(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false))
        { AutoFlush = true };

        Write("INFO", $"运行日志开始：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    public string LogFilePath { get; }

    public void Write(string level, string message) =>
        WriteLine($"[{DateTime.Now:HH:mm:ss}] {level,-5} {message}");

    public void Write(BatchEvent ev)
    {
        if (string.IsNullOrEmpty(ev.Message))
        {
            return;
        }

        // 危险操作点醒目标记（REQ-010b）
        var prefix = ev.IsDangerous ? "*** 高危 *** " : string.Empty;
        Write(MapLevel(ev.Kind), $"{prefix}[{ev.Kind}] {ev.Message}");
    }

    public void WriteTaskStarted(string taskId, string? account, string? model, string? expert, string? workspace) =>
        Write("INFO", $"task_started 任务 {taskId} | 账号 {account ?? "-"} | 模型 {model ?? "-"} | 专家 {expert ?? "-"} | 空间 {workspace ?? "-"}");

    public void WriteTaskFinished(string taskId, TaskStatus status, double durationSeconds, int retryCount) =>
        Write("INFO", $"task_finished 任务 {taskId} | 状态 {status} | 耗时 {durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s | 重试 {retryCount}");

    public void WriteModelSwitched(string? fromAccount, string? fromModel, string? toAccount, string? toModel,
        string reason, string? taskId) =>
        Write("WARN", $"model_switched 任务 {taskId ?? "-"} | {fromAccount}+{fromModel} → {toAccount}+{toModel} | 原因 {reason}");

    public void WriteWindowSkipped(string model, DateTimeOffset? nextWindowStart) =>
        Write("INFO", $"model_window_skipped 模型 {model} | 下一可用时段 {nextWindowStart?.ToString("yyyy-MM-dd HH:mm") ?? "-"}");

    public void WriteCooldownSkipped(string account, string model, DateTimeOffset resetAt) =>
        Write("INFO", $"model_cooldown_skipped {account} × {model} | 预计恢复 {resetAt:yyyy-MM-dd HH:mm}");

    public void WriteQuotaResetRecorded(string account, string model, DateTimeOffset resetAt, CooldownSource source) =>
        Write("WARN", $"quota_reset_recorded {account} × {model} | 重置时间 {resetAt:yyyy-MM-dd HH:mm} | 来源 {(source == CooldownSource.CliParsed ? "CLI 解析" : "默认冷却")}");

    public void WriteFallbackUsed(string taskId, string model, decimal cost, CostBasis basis) =>
        Write("WARN", $"fallback_used 任务 {taskId} | 兜底模型 {model} | 花费 ¥{cost.ToString("0.####", CultureInfo.InvariantCulture)}（{(basis == CostBasis.Measured ? "实测" : "估算")}）");

    public void WriteBatchWaiting(DateTimeOffset resumeAt, string? target, WaitKind kind) =>
        Write("WARN", $"batch_waiting_window 等待至 {resumeAt:yyyy-MM-dd HH:mm} | 目标 {target ?? "-"} | 类型 {kind}");

    public void WriteBatchBlocked(string reason, int pendingCount) =>
        Write("ERROR", $"batch_blocked 原因：{reason} | 待续跑 {pendingCount} 条");

    public void WriteBatchResumed(string reason) => Write("INFO", $"batch_resumed {reason}");

    public void WriteBatchTerminated(int doneCount, int unfinishedCount) =>
        Write("WARN", $"batch_terminated 已完成 {doneCount} 条 | 未完成 {unfinishedCount} 条");

    public void WriteDangerousOperation(string taskId, string interactionPoint) =>
        Write("DANGER", $"*** 高危确认点 *** 任务 {taskId} | 交互点：{interactionPoint} | 已拒绝自动批准");

    public void WriteAutoAnswered(string taskId, string interactionPoint, string answer) =>
        Write("INFO", $"auto_answered 任务 {taskId} | 交互点：{interactionPoint} | 默认选项：{answer}");

    private static string MapLevel(BatchEventKind kind) => kind switch
    {
        BatchEventKind.DangerousOperationBlocked => "DANGER",
        BatchEventKind.BatchBlocked or BatchEventKind.PreflightFailed => "ERROR",
        BatchEventKind.TaskFinished or BatchEventKind.BatchCompleted or BatchEventKind.StatusChanged
            or BatchEventKind.TaskStarted or BatchEventKind.TaskOutput => "INFO",
        BatchEventKind.Warning or BatchEventKind.ModelSwitched or BatchEventKind.AccountSwitched
            or BatchEventKind.BatchWaiting or BatchEventKind.BatchTerminated or BatchEventKind.QuotaResetRecorded
            or BatchEventKind.FallbackUsed => "WARN",
        _ => "INFO"
    };

    private void WriteLine(string line)
    {
        StreamWriter writer;
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
        }

        // 双重保护：即便在 lock 释放后、Dispose 抢先关闭 writer 的极小窗口内拿到引用，
        // WriteLine 抛出的 ObjectDisposedException / IOException 也必须在此吞掉，
        // 否则异常会经 Process.AsyncStreamReader 在新建线程池线程上 re-throw，
        // 升级为 AppDomain 未处理异常并终止进程。
        try
        {
            writer.WriteLine(line);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

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
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }

            _writer.Dispose();
        }
    }
}

/// <summary>空实现（未指定任务文件时使用）。</summary>
public sealed class NullRunLogger : IRunLogger
{
    public string LogFilePath => string.Empty;

    public void Write(BatchEvent ev)
    {
    }

    public void Write(string level, string message)
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

    public void Dispose()
    {
    }
}
