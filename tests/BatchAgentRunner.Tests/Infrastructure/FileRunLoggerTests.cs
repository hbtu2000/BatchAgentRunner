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
}
