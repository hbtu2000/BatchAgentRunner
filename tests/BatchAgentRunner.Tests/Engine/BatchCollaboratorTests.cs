using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Infrastructure.Logging;
using Xunit;

namespace BatchAgentRunner.Tests.Engine;

/// <summary>
/// 报告聚合协作者单测（P1-1 拆分产物 ⑤）。原报告组装逻辑只有 BatchEngine 黑箱覆盖，
/// 拆分后直接锁定用量/时间线/等待累计/报告字段的聚合契约。
/// </summary>
[Trait("Category", "Invariant")]
public class BatchReportBuilderTests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    private static (BatchReportBuilder Builder, List<TaskItem> Tasks, List<DiffRecord> Diffs) Build(
        RunOptions? options = null,
        BatchStatus status = BatchStatus.Completed)
    {
        var tasks = new List<TaskItem>();
        var diffs = new List<DiffRecord>();
        var opts = options ?? new RunOptions { TaskFilePath = "tasks.json", AgentKind = AgentKind.Mock };
        var builder = new BatchReportBuilder(
            new FakeClock(Noon),
            () => opts,
            () => tasks,
            () => status,
            () => diffs,
            () => new FallbackSummary { TotalCost = 0.2m, UsedCount = 1 });
        return (builder, tasks, diffs);
    }

    [Fact]
    public void 用量按账号与模型双维度累计()
    {
        var (builder, _, _) = Build();

        builder.TrackUsage("账号 1", "m1", new AgentUsage(null, null, 0.2m, CostBasis.Measured));
        builder.TrackUsage("账号 1", "m2", new AgentUsage(null, null, 0.3m, CostBasis.Measured));

        var report = builder.Build(Noon.AddMinutes(-5));

        var account = Assert.Single(report.AccountUsage);
        Assert.Equal("账号 1", account.Name);
        Assert.Equal(2, account.TaskCount);
        Assert.Equal(0.5m, account.Cost);
        Assert.Equal(CostBasis.Measured, account.CostBasis);
        Assert.Equal(2, report.ModelUsage.Count);
    }

    [Fact]
    public void 时间线条目按序追加且可整体快照()
    {
        var (builder, _, _) = Build();

        builder.AddTimeline("切换", "a → b", "task-001", "账号 1", "m2");
        builder.AddTimeline("阻塞", "额度耗尽", null, null, null);

        var report = builder.Build(Noon.AddMinutes(-5));

        Assert.Equal(2, report.Timeline.Count);
        Assert.Equal("切换", report.Timeline[0].Kind);
        Assert.Equal("task-001", report.Timeline[0].TaskId);
        Assert.Equal(Noon, report.Timeline[0].At);
    }

    [Fact]
    public void 等待时长按类型分流并计入有效时长()
    {
        var (builder, _, _) = Build();

        builder.AddWaitSeconds(WaitKind.FreeWindow, 30);
        builder.AddWaitSeconds(WaitKind.QuotaReset, 20);

        var report = builder.Build(Noon.AddSeconds(-100));

        Assert.Equal(30, report.WindowWaitSeconds);
        Assert.Equal(20, report.CooldownWaitSeconds);
        // 有效时长 = 总时长 100 - 窗口 30 - 冷却 20 = 50
        Assert.Equal(50, report.ActiveDurationSeconds);
    }

    [Fact]
    public void 跳过已完成任务数经设置后进入报告()
    {
        var (builder, _, _) = Build();

        builder.SetSkippedDoneCount(3);

        Assert.Equal(3, builder.SkippedDoneCount);
        Assert.Equal(3, builder.Build(Noon).SkippedAlreadyDone);
    }

    [Fact]
    public void 报告统计任务终态分布与失败清单()
    {
        var (builder, tasks, _) = Build();
        tasks.AddRange(new[]
        {
            new TaskItem { Id = "t1", Status = TaskStatus.Done },
            new TaskItem { Id = "t2", Status = TaskStatus.Failed, Error = "boom", RetryCount = 2 },
            new TaskItem { Id = "t3", Status = TaskStatus.Blocked, DependsOn = new List<string> { "t2" }, Error = "依赖失败" }
        });

        var report = builder.Build(Noon);

        Assert.Equal(3, report.Total);
        Assert.Equal(1, report.Done);
        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.Blocked);
        var failed = Assert.Single(report.FailedTasks);
        Assert.Equal("t2", failed.TaskId);
        Assert.Equal("boom", failed.Error);
        // BlockedBy：t3 依赖失败的 t2
        var blocked = Assert.Single(report.Tasks, t => t.TaskId == "t3");
        Assert.Equal(new[] { "t2" }, blocked.BlockedBy);
    }

    [Fact]
    public void 悬空依赖在阻断来源中标注不存在()
    {
        var (builder, tasks, _) = Build();
        tasks.Add(new TaskItem
        {
            Id = "t1",
            Status = TaskStatus.Blocked,
            DependsOn = new List<string> { "ghost" },
            Error = "悬空依赖"
        });

        var report = builder.Build(Noon);

        var entry = Assert.Single(report.Tasks);
        Assert.Equal(new[] { "ghost（不存在）" }, entry.BlockedBy);
    }

    [Fact]
    public void 兜底汇总经提供函数进入报告()
    {
        var (builder, _, _) = Build();

        var report = builder.Build(Noon);

        Assert.Equal(0.2m, report.Fallback.TotalCost);
        Assert.Equal(1, report.Fallback.UsedCount);
    }
}

/// <summary>
/// 通知协作者单测（P1-1 拆分产物 ⑤）：锁定「无 trigger / 无分发器 / 无规则 = 空操作」
/// 与「上下文按任务集现算 + 分发失败只记日志不抛出」两条契约。
/// </summary>
[Trait("Category", "Invariant")]
public class BatchNotifierTests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    private static (BatchNotifier Notifier, FakeNotificationDispatcher Dispatcher, NullRunLogger Logger) Build(
        IReadOnlyList<NotificationRule>? rules = null,
        INotificationDispatcher? dispatcher = null,
        List<TaskItem>? tasks = null)
    {
        var options = new RunOptions { TaskFilePath = "C:/batch/tasks.json", AgentKind = AgentKind.Mock };
        var list = rules ?? new List<NotificationRule> { new() { Triggers = new List<NotifyTrigger> { NotifyTrigger.BatchCompleted } } };
        var fake = dispatcher as FakeNotificationDispatcher ?? new FakeNotificationDispatcher();
        return (new BatchNotifier(options, list, fake, () => BatchStatus.Completed,
            () => (IReadOnlyList<TaskItem>?)(tasks ?? new List<TaskItem>()) ?? new List<TaskItem>(),
            new FakeClock(Noon), new NullRunLogger()), fake, new NullRunLogger());
    }

    [Fact]
    public async Task 无触发时机_不分发()
    {
        var (notifier, dispatcher, _) = Build();

        await notifier.DispatchAsync(null);

        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task 无分发器_静默返回不抛()
    {
        var options = new RunOptions { TaskFilePath = "tasks.json" };
        var notifier = new BatchNotifier(options, new List<NotificationRule> { new() }, null,
            () => BatchStatus.Completed, () => new List<TaskItem>(), new FakeClock(Noon), new NullRunLogger());

        await notifier.DispatchAsync(NotifyTrigger.BatchCompleted);
    }

    [Fact]
    public async Task 无规则_不分发()
    {
        var (notifier, dispatcher, _) = Build(rules: new List<NotificationRule>());

        await notifier.DispatchAsync(NotifyTrigger.BatchCompleted);

        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task 上下文按任务集现算_批名取自任务文件名()
    {
        var tasks = new List<TaskItem>
        {
            new() { Id = "t1", Status = TaskStatus.Done },
            new() { Id = "t2", Status = TaskStatus.Failed },
            new() { Id = "t3", Status = TaskStatus.NeedsAttention },
            new() { Id = "t4", Status = TaskStatus.Blocked }
        };
        var (notifier, dispatcher, _) = Build(tasks: tasks);

        await notifier.DispatchAsync(NotifyTrigger.BatchCompleted, Noon.AddMinutes(-2), "收尾消息");

        Assert.Equal(1, dispatcher.CallCount);
        var ctx = dispatcher.LastContext!;
        Assert.Equal(NotifyTrigger.BatchCompleted, ctx.Trigger);
        Assert.Equal("tasks", ctx.BatchName); // 不含目录与扩展名
        Assert.Equal(BatchStatus.Completed, ctx.BatchStatus);
        Assert.Equal(4, ctx.Total);
        Assert.Equal(1, ctx.Done);
        Assert.Equal(1, ctx.Failed);
        Assert.Equal(1, ctx.NeedsAttention);
        Assert.Equal(1, ctx.Blocked);
        Assert.Equal(120, ctx.DurationSeconds);
        Assert.Equal("收尾消息", ctx.Message);
    }

    [Fact]
    public async Task 分发失败_只记日志不抛出()
    {
        var (notifier, _, _) = Build(dispatcher: new FakeNotificationDispatcher { Throw = true });

        await notifier.DispatchAsync(NotifyTrigger.BatchCompleted);
    }

    private sealed class FakeNotificationDispatcher : INotificationDispatcher
    {
        public int CallCount { get; private set; }

        public NotificationContext? LastContext { get; private set; }

        public bool Throw { get; init; }

        public Task DispatchAsync(IEnumerable<NotificationRule> rules, NotificationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastContext = context;
            if (Throw)
            {
                throw new InvalidOperationException("推送通道故障");
            }

            return Task.CompletedTask;
        }
    }
}
