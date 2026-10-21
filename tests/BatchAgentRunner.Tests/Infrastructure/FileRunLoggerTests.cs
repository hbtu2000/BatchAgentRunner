using BatchAgentRunner.Core.Events;
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
        var dir = Path.Combine(Path.GetTempPath(), "bar-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }
    }
}
