using BatchAgentRunner.Core.Observability;
using Xunit;

namespace BatchAgentRunner.Tests.Observability;

/// <summary>
/// 运行级指标口径（ARCH P-10 / A-8）。
/// </summary>
/// <remarks>
/// 收口前「降级次数 / 兜底触发 / 冷却跳过 / 误切换率 / 吞吐 / 平均耗时」只能靠人肉数日志，
/// 且口径随人而异（例如待人工任务到底算不算失败）。本文件把口径钉死为可断言的不变量：
/// <b>失败率分母只含真终态，待人工与依赖阻断不计失败；时段外与冷却跳过不计失败、单独计数；
/// 误切换率的分母是切换次数而非任务数</b>。
/// </remarks>
public class RunMetricsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 终态计数_失败率只统计真失败()
    {
        var metrics = new RunMetrics("run-test", Noon);
        metrics.RecordTaskStarted();
        metrics.RecordTaskFinished(TaskStatus.Done, 10);
        metrics.RecordTaskFinished(TaskStatus.Failed, 20);
        metrics.RecordTaskFinished(TaskStatus.Interrupted, 5);
        // 待人工与依赖阻断计入终态（吞吐/耗时口径），但不计失败
        metrics.RecordTaskFinished(TaskStatus.NeedsAttention, 30);
        metrics.RecordTaskFinished(TaskStatus.Blocked, 0);

        var snapshot = metrics.Snapshot(Noon.AddMinutes(10));

        Assert.Equal(1, snapshot.TasksStarted);
        Assert.Equal(1, snapshot.TasksCompleted);
        Assert.Equal(1, snapshot.TasksFailed);
        Assert.Equal(1, snapshot.TasksInterrupted);
        Assert.Equal(5, snapshot.TasksTerminal);
        Assert.Equal(0.2, snapshot.FailureRate);
    }

    [Fact]
    public void 平均耗时_按终态任务摊薄()
    {
        var metrics = new RunMetrics("run-test", Noon);
        metrics.RecordTaskFinished(TaskStatus.Done, 10);
        metrics.RecordTaskFinished(TaskStatus.Done, 30);

        var snapshot = metrics.Snapshot(Noon.AddMinutes(1));

        Assert.Equal(40, snapshot.TotalDurationSeconds);
        Assert.Equal(20, snapshot.AverageDurationSeconds);
    }

    [Fact]
    public void 误切换率_以切换次数为分母()
    {
        var metrics = new RunMetrics("run-test", Noon);
        metrics.RecordSwitch();
        metrics.RecordSwitch();
        metrics.RecordSwitch();
        metrics.RecordSwitch();
        metrics.RecordMisswitch(); // 4 次切换里 1 次白切

        var snapshot = metrics.Snapshot(Noon.AddMinutes(1));

        Assert.Equal(4, snapshot.DegradationSwitches);
        Assert.Equal(1, snapshot.Misswitches);
        Assert.Equal(0.25, snapshot.MisswitchRate);
    }

    [Fact]
    public void 跳过与兜底_单独计数且不计失败()
    {
        var metrics = new RunMetrics("run-test", Noon);
        metrics.RecordCooldownSkip();
        metrics.RecordCooldownSkip();
        metrics.RecordWindowSkip();
        metrics.RecordFallback();
        metrics.RecordDangerousBlock();

        var snapshot = metrics.Snapshot(Noon.AddMinutes(1));

        Assert.Equal(2, snapshot.CooldownSkips);
        Assert.Equal(1, snapshot.WindowSkips);
        Assert.Equal(1, snapshot.FallbackTriggered);
        Assert.Equal(1, snapshot.DangerousBlocked);
        // 预期调度行为不产生失败
        Assert.Equal(0, snapshot.TasksFailed);
        Assert.Equal(0, snapshot.FailureRate);
    }

    [Fact]
    public void 吞吐_按已运行小时数折算()
    {
        var metrics = new RunMetrics("run-test", Noon);
        metrics.MarkBatchStarted(Noon);
        for (var i = 0; i < 6; i++)
        {
            metrics.RecordTaskFinished(TaskStatus.Done, 60);
        }

        // 30 分钟完成 6 条 → 12 条/小时
        var snapshot = metrics.Snapshot(Noon.AddMinutes(30));

        Assert.Equal(1800, snapshot.ElapsedSeconds);
        Assert.Equal(12, snapshot.ThroughputPerHour);
    }

    [Fact]
    public void 无终态任务_派生量为零且不除零()
    {
        var metrics = new RunMetrics("run-test", Noon);
        metrics.RecordSwitch();

        var snapshot = metrics.Snapshot(Noon);

        Assert.Equal(0, snapshot.FailureRate);
        Assert.Equal(0, snapshot.AverageDurationSeconds);
        Assert.Equal(0, snapshot.MisswitchRate);
        Assert.Equal(0, snapshot.ThroughputPerHour);
    }

    [Fact]
    public void 并发埋点_计数不丢失()
    {
        var metrics = new RunMetrics("run-test", Noon);
        Parallel.For(0, 200, _ => metrics.RecordTaskFinished(TaskStatus.Done, 1));

        var snapshot = metrics.Snapshot(Noon.AddSeconds(1));

        Assert.Equal(200, snapshot.TasksCompleted);
        Assert.Equal(200, snapshot.TotalDurationSeconds);
    }

    [Fact]
    public void 指标快照条目_带RunId与结构化字段()
    {
        var metrics = new RunMetrics("run-p10-demo", Noon);
        metrics.RecordTaskFinished(TaskStatus.Done, 12);

        var entry = metrics.ToLogEntry(Noon.AddMinutes(1));

        Assert.Equal(RunLogLevels.Info, entry.Level);
        Assert.Equal(LogEventNames.MetricsSnapshot, entry.Event);
        Assert.Equal("run-p10-demo", entry.RunId);
        Assert.NotNull(entry.Fields);
        Assert.Equal("run-p10-demo", entry.Fields!["run_id"]);
        Assert.Equal("1", entry.Fields["tasks_completed"]);
        Assert.Equal("1", entry.Fields["tasks_terminal"]);
        Assert.Equal("0", entry.Fields["failure_rate"]);
        Assert.Contains("批次指标", entry.RenderBody());
    }
}
