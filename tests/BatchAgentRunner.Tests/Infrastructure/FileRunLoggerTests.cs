using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Infrastructure.Logging;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// FileRunLogger 释放安全性回归（2026-10-18 崩溃根因：释放后写入已关闭的 StreamWriter
/// 抛 ObjectDisposedException，经 Process.AsyncStreamReader 在新建线程池线程上 re-throw，
/// 升级为 AppDomain 未处理异常并终止进程）。
/// 修复后：释放后到达的延迟/并发写入必须被安全丢弃，且 Dispose 幂等。
/// </summary>
public class FileRunLoggerTests
{
    [Fact]
    public void Write_AfterDispose_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var logger = new FileRunLogger(logDirectory: dir);
            logger.Write("INFO", "before-dispose");
            logger.Dispose();

            // 释放后延迟/并发写入必须安全（不再抛 ObjectDisposedException）
            var ex = Record.Exception(() => logger.Write("INFO", "after-dispose-line"));
            Assert.Null(ex);

            var ex2 = Record.Exception(() =>
                logger.Write(new BatchEvent(BatchEventKind.Info, DateTimeOffset.Now, "after-dispose-event")));
            Assert.Null(ex2);

            // 重复 Dispose 必须幂等
            var ex3 = Record.Exception(() => logger.Dispose());
            Assert.Null(ex3);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // 清理失败不影响断言
            }
        }
    }

    [Fact]
    public void 重要级别_写入即落盘_读取立即可见()
    {
        // P2-24：WARN/ERROR/DANGER 不因批量落盘滞后——配额重置、兜底、阻塞等高价值
        // 排障记录必须即时可读（FileShare.Read 允许运行中读取）。
        var dir = Path.Combine(Path.GetTempPath(), "bar-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var logger = new FileRunLogger(logDirectory: dir);
            logger.Write("WARN", "quota_reset_recorded 账号 × 模型");

            // 运行中读取：显式 FileShare.ReadWrite，兼容写入方持句柄（FileShare.Read 不允许他人写）
            using var fs = new FileStream(logger.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var content = reader.ReadToEnd();

            Assert.Contains("quota_reset_recorded", content);
            logger.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void INFO级_按批落盘_释放后全部可见()
    {
        // P2-24：INFO 批量落盘不丢行——缓冲中的行在 Dispose 时全部刷出。
        var dir = CreateTempDir();
        try
        {
            var logger = new FileRunLogger(logDirectory: dir);
            for (var i = 0; i < 200; i++)
            {
                logger.Write("INFO", $"task_output 分片 {i}");
            }

            logger.Dispose();

            var content = File.ReadAllText(logger.LogFilePath);
            Assert.Contains("运行日志开始", content);
            Assert.Contains("task_output 分片 0", content);
            Assert.Contains("task_output 分片 199", content);
            Assert.Equal(201, content.Split('\n').Count(l => l.Length > 0));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void 结构化日志_与文本同批落盘且逐行带RunId()
    {
        // ARCH P-10：日志必须可按 RunId 关联、可被机器采集——文本给人看，JSONL 给采集端看。
        var dir = CreateTempDir();
        try
        {
            var logger = new FileRunLogger(logDirectory: dir);
            logger.WriteTaskStarted("task-001", "acc1", "m1", "expert", null);
            logger.Write(new BatchEvent(BatchEventKind.ModelSwitched, DateTimeOffset.Now, "m1 → m2", "task-001",
                "acc1", "m2"));
            logger.Dispose();

            Assert.True(File.Exists(logger.StructuredLogFilePath));
            Assert.Equal(
                Path.GetFileNameWithoutExtension(logger.LogFilePath),
                Path.GetFileNameWithoutExtension(logger.StructuredLogFilePath));

            var lines = File.ReadAllLines(logger.StructuredLogFilePath).Where(l => l.Length > 0).ToList();
            Assert.Contains(lines, l => l.Contains("\"run_id\":\"" + logger.RunId + "\"") && l.Contains("task_started"));
            Assert.Contains(lines, l => l.Contains("model_switched"));
            Assert.All(lines, l => Assert.Contains("\"ts\":", l));

            // 文本侧正文不回归（事件名仍出现在人读行里）
            var text = File.ReadAllText(logger.LogFilePath);
            Assert.Contains("task_started 任务 task-001", text);
            Assert.Contains("model_switched m1 → m2", text);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void 写入内容含密钥_文本与结构化均脱敏()
    {
        // ARCH P-10 安全底线：任何写入点（含 CLI 输出直写）都不得把密钥落到日志
        var dir = CreateTempDir();
        try
        {
            var logger = new FileRunLogger(logDirectory: dir);
            logger.WriteTaskOutput("api_key=abcdef123456");
            logger.Write("INFO", "Authorization: Bearer abcdef1234567890");
            logger.Dispose();

            var text = File.ReadAllText(logger.LogFilePath);
            var structured = File.ReadAllText(logger.StructuredLogFilePath);

            Assert.DoesNotContain("abcdef123456", text);
            Assert.DoesNotContain("abcdef123456", structured);
            Assert.Contains(SecretMasker.Placeholder, text);
            Assert.Contains(SecretMasker.Placeholder, structured);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void 同一批次_日志与指标共用同一RunId()
    {
        var dir = CreateTempDir();
        try
        {
            var logger = new FileRunLogger(logDirectory: dir);
            var metrics = new RunMetrics(logger.RunId);
            metrics.RecordTaskFinished(TaskStatus.Done, 3);
            logger.Write(metrics.ToLogEntry(DateTimeOffset.Now));
            logger.Dispose();

            Assert.False(string.IsNullOrWhiteSpace(logger.RunId));
            Assert.StartsWith("run-", logger.RunId, StringComparison.Ordinal);

            var structured = File.ReadAllText(logger.StructuredLogFilePath);
            Assert.Contains(LogEventNames.MetricsSnapshot, structured);
            Assert.Contains("\"run_id\":\"" + logger.RunId + "\"", structured);
            // 指标条目的结构化字段必须带得上（否则采集端只能再解析正文）
            Assert.Contains("\"fields\":{", structured);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // 清理失败不影响断言
        }
    }
}
