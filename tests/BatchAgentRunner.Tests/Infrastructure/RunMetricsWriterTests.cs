using System.Text.Json;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Infrastructure.Observability;
using Xunit;

namespace BatchAgentRunner.Tests.Infrastructure;

/// <summary>
/// 运行级指标落盘（ARCH P-10 / A-8「指标可采集」）。
/// </summary>
/// <remarks>
/// 指标只在日志里出现等于"能看不能采"：趋势对比、跨批次聚合都需要一份结构固定的文件。
/// 本文件锁住三件事——<b>文件名带 RunId（可与日志互相关联）、字段 snake_case（与 config/sidecar 同口径）、
/// 派生量与原始计数一并落盘（采集端不必自己算）</b>。
/// </remarks>
public class RunMetricsWriterTests
{
    [Fact]
    public async Task 落盘文件名带RunId_且内容为合法JSON()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var metrics = new RunMetrics("run-p10-abcd12");
            metrics.RecordTaskStarted();
            metrics.RecordTaskFinished(TaskStatus.Done, 12.5);

            var path = await new RunMetricsWriter().WriteAsync(metrics.Snapshot(DateTimeOffset.Now),
                Path.Combine(dir, "tasks.json"));

            Assert.True(File.Exists(path));
            Assert.Contains("run-p10-abcd12", Path.GetFileName(path));
            Assert.EndsWith(".json", path, StringComparison.Ordinal);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("run-p10-abcd12", doc.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("tasks_started").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("tasks_completed").GetInt32());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 字段命名一律snake_case_派生量与计数同落()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var metrics = new RunMetrics("run-p10-naming");
            metrics.MarkBatchStarted(DateTimeOffset.Now.AddMinutes(-10));
            metrics.RecordTaskFinished(TaskStatus.Done, 30);
            metrics.RecordTaskFinished(TaskStatus.Failed, 30);
            metrics.RecordSwitch();
            metrics.RecordMisswitch();

            var path = await new RunMetricsWriter().WriteAsync(metrics.Snapshot(DateTimeOffset.Now),
                Path.Combine(dir, "tasks.json"));

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

            Assert.Contains("total_duration_seconds", properties);
            Assert.Contains("failure_rate", properties);
            Assert.Contains("misswitch_rate", properties);
            Assert.Contains("throughput_per_hour", properties);
            // 与 config / sidecar / 报告三套对外契约同口径：一律 snake_case
            Assert.All(properties, p => Assert.Matches("^[a-z][a-z0-9_]*$", p));

            Assert.Equal(0.5, doc.RootElement.GetProperty("failure_rate").GetDouble());
            Assert.Equal(1, doc.RootElement.GetProperty("misswitch_rate").GetDouble());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
