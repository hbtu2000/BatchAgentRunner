using BatchAgentRunner.Agents.Adapters;
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
/// 二期引擎集成测试：工作流任务委托、DAG 依赖阻断与失败策略、环检测、终态通知。
/// 一期既有行为由 <see cref="BatchEngineTests"/> 覆盖，这里只验证二期增量。
/// </summary>
public class BatchEnginePhase2Tests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 17, 12, 0);

    internal sealed class RecordingNotificationDispatcher : INotificationDispatcher
    {
        public List<NotificationContext> Contexts { get; } = new();

        public Task DispatchAsync(IEnumerable<NotificationRule> rules, NotificationContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return Task.CompletedTask;
        }
    }

    private static RunOptions Options() => new()
    {
        TaskFilePath = Path.Combine(Path.GetTempPath(), "bar-p2-tasks.json"),
        AgentKind = AgentKind.Mock,
        Accounts = new List<AccountConfig> { new() { Id = "acc1", DisplayName = "账号 1" } },
        ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" } },
        Concurrency = 1,
        MaxRetriesPerTask = 1,
        RateLimitBackoffRetries = 1,
        TaskTimeoutMinutes = 1
    };

    private static (BatchEngine Engine, InMemoryTaskFileStore Store, FakeClock Clock, List<BatchEvent> Events) Build(
        RunOptions options, IAgentCliAdapter adapter, List<TaskItem> tasks,
        INotificationDispatcher? dispatcher = null)
    {
        var clock = new FakeClock(Noon);
        var store = new InMemoryTaskFileStore { Tasks = tasks };
        var engine = new BatchEngine(options, adapter, store, new InMemoryCooldownStore(), new NullRunLogger(), clock,
            notificationDispatcher: dispatcher);
        if (adapter is MockAdapter mock)
        {
            mock.Clock = clock;
        }

        engine.InitializeAsync().Wait();

        var events = new List<BatchEvent>();
        engine.EventRaised += (_, e) => events.Add(e);

        return (engine, store, clock, events);
    }

    private static TaskItem Plain(string id, params string[] deps) => new()
    {
        Id = id,
        Instruction = "指令 " + id,
        DependsOn = deps.Length == 0 ? null : new List<string>(deps)
    };

    /// <summary>预检会校验任务文件是否真实存在（磁盘），这里建一个空文件占位。</summary>
    private static RunOptions OptionsWithRealFile()
    {
        var options = Options();
        var path = Path.Combine(Path.GetTempPath(), $"bar-p2-preflight-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        options.TaskFilePath = path;
        return options;
    }

    private static TaskItem WorkflowTask(string id, params string[] deps) => new()
    {
        Id = id,
        Instruction = "工作流任务",
        DependsOn = deps.Length == 0 ? null : new List<string>(deps),
        Workflow = new WorkflowSpec
        {
            Name = "两阶段",
            Stages = new List<WorkflowStage>
            {
                new() { Id = "s1", Name = "设计", InstructionTemplate = "输出设计" },
                new() { Id = "s2", Name = "实现", InstructionTemplate = "实现：{output:s1}" }
            }
        }
    };

    // ---------------- 工作流任务委托 ----------------

    [Fact]
    public async Task 工作流任务_由工作流执行器驱动并写入阶段结果()
    {
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero, clock: new FakeClock(Noon));
        var (engine, store, _, _) = Build(Options(), adapter, new List<TaskItem> { WorkflowTask("wf-1") });

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        var t = Assert.Single(store.Tasks);
        Assert.Equal(TaskStatus.Done, t.Status);
        Assert.NotNull(t.StageResults);
        Assert.Equal(2, t.StageResults!.Count);
        Assert.All(t.StageResults, s => Assert.Equal(TaskStatus.Done, s.Status));
    }

    [Fact]
    public async Task 混合批次_工作流与单指令任务都能完成()
    {
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero, clock: new FakeClock(Noon));
        var (engine, store, _, _) = Build(Options(), adapter,
            new List<TaskItem> { Plain("t-1"), WorkflowTask("wf-1"), Plain("t-2") });

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(3, report.Done);
    }

    // ---------------- DAG 依赖 ----------------

    [Fact]
    public async Task 依赖链_按顺序执行_全部成功()
    {
        var order = new List<string>();
        var adapter = new MockAdapter((req, _) =>
        {
            order.Add(req.TaskId);
            return MockStep.Ok();
        }, TimeSpan.Zero, new FakeClock(Noon));
        // 故意反序输入：c 依赖 b，b 依赖 a
        var (engine, store, _, _) = Build(Options(), adapter, new List<TaskItem>
        {
            Plain("c", "b"),
            Plain("b", "a"),
            Plain("a")
        });

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(3, report.Done);
        Assert.Equal(new[] { "a", "b", "c" }, order);
    }

    [Fact]
    public async Task 依赖失败_默认策略_下游标记阻塞且不计失败()
    {
        var adapter = new MockAdapter((req, _) =>
            req.TaskId == "a" ? MockStep.Error("a 挂了") : MockStep.Ok(),
            TimeSpan.Zero, new FakeClock(Noon));
        var (engine, store, _, _) = Build(Options(), adapter, new List<TaskItem>
        {
            Plain("a"),
            Plain("b", "a"),
            Plain("c", "b")
        });

        var report = await engine.StartAsync();

        var byId = store.Tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(TaskStatus.Failed, byId["a"].Status);
        Assert.Equal(TaskStatus.Blocked, byId["b"].Status);
        Assert.Equal(TaskStatus.Blocked, byId["c"].Status); // 逐层阻断
        Assert.Equal(1, report.Failed);
        Assert.Equal(2, report.Blocked);
    }

    [Fact]
    public async Task 依赖失败_策略终止整批_批处理进入阻塞()
    {
        var options = Options();
        options.DependencyFailurePolicy = DependencyFailurePolicy.AbortBatch;
        var adapter = new MockAdapter((req, _) =>
            req.TaskId == "a" ? MockStep.Error("a 挂了") : MockStep.Ok(),
            TimeSpan.Zero, new FakeClock(Noon));
        var (engine, store, _, _) = Build(options, adapter, new List<TaskItem>
        {
            Plain("a"),
            Plain("b", "a"),
            Plain("c", "a")
        });

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Equal(TaskStatus.Blocked, store.Tasks.Single(t => t.Id == "b").Status);
    }

    [Fact]
    public async Task 依赖失败_策略忽略依赖_下游照常执行()
    {
        var options = Options();
        options.DependencyFailurePolicy = DependencyFailurePolicy.SkipDependents;
        var adapter = new MockAdapter((req, _) =>
            req.TaskId == "a" ? MockStep.Error("a 挂了") : MockStep.Ok(),
            TimeSpan.Zero, new FakeClock(Noon));
        var (engine, store, _, _) = Build(options, adapter, new List<TaskItem>
        {
            Plain("a"),
            Plain("b", "a")
        });

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(TaskStatus.Done, store.Tasks.Single(t => t.Id == "b").Status);
    }

    [Fact]
    public async Task 存在循环依赖_启动即阻塞且报告错误()
    {
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero, clock: new FakeClock(Noon));
        var (engine, store, _, _) = Build(Options(), adapter, new List<TaskItem>
        {
            Plain("a", "b"),
            Plain("b", "a")
        });

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Equal(0, adapter.TotalCalls); // 不应发起任何调用
    }

    [Fact]
    public async Task 预检_检出循环依赖()
    {
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero, clock: new FakeClock(Noon));
        var (engine, _, _, _) = Build(OptionsWithRealFile(), adapter, new List<TaskItem>
        {
            Plain("a", "b"),
            Plain("b", "a")
        });

        var preflight = await engine.PreflightAsync();

        Assert.Contains(preflight.Errors, e => e.Contains("循环依赖"));
    }

    [Fact]
    public async Task 预检_悬空依赖给出告警()
    {
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero, clock: new FakeClock(Noon));
        var (engine, _, _, _) = Build(OptionsWithRealFile(), adapter, new List<TaskItem>
        {
            Plain("a", "ghost")
        });

        var preflight = await engine.PreflightAsync();

        Assert.Contains(preflight.Warnings, w => w.Contains("ghost"));
    }

    [Fact]
    public async Task 含依赖批次_并发大于1时强制串行()
    {
        var options = Options();
        options.Concurrency = 4;
        var order = new List<string>();
        var adapter = new MockAdapter((req, _) =>
        {
            order.Add(req.TaskId);
            return MockStep.Ok();
        }, TimeSpan.Zero, new FakeClock(Noon));
        var (engine, _, _, events) = Build(options, adapter, new List<TaskItem>
        {
            Plain("b", "a"),
            Plain("a")
        });

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(new[] { "a", "b" }, order);
        Assert.Contains(events, e => e.Message.Contains("强制串行"));
    }

    // ---------------- 报告与通知 ----------------

    [Fact]
    public async Task 报告_统计待人工与阻塞数量()
    {
        var options = Options();
        var adapter = new MockAdapter((req, _) =>
            req.TaskId == "bad" ? MockStep.Error("炸了") : MockStep.Ok(),
            TimeSpan.Zero, new FakeClock(Noon));
        var (engine, _, _, _) = Build(options, adapter, new List<TaskItem>
        {
            Plain("ok"),
            Plain("bad"),
            Plain("down", "bad")
        });

        var report = await engine.StartAsync();

        Assert.Equal(1, report.Done);
        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.Blocked);
    }

    [Fact]
    public async Task 终态通知_批处理完成时推送()
    {
        var options = Options();
        options.Notifications = new List<NotificationRule>
        {
            new() { Channel = NotificationChannel.Desktop, Triggers = new List<NotifyTrigger> { NotifyTrigger.BatchCompleted } }
        };
        var dispatcher = new RecordingNotificationDispatcher();
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero, clock: new FakeClock(Noon));
        var (engine, _, _, _) = Build(options, adapter, new List<TaskItem> { Plain("a") }, dispatcher);

        await engine.StartAsync();

        var ctx = Assert.Single(dispatcher.Contexts);
        Assert.Equal(NotifyTrigger.BatchCompleted, ctx.Trigger);
        Assert.Equal(BatchStatus.Completed, ctx.BatchStatus);
        Assert.Equal(1, ctx.Done);
    }

    [Fact]
    public async Task 任务失败通知_按规则触发()
    {
        var options = Options();
        options.Notifications = new List<NotificationRule>
        {
            new() { Channel = NotificationChannel.Desktop, Triggers = new List<NotifyTrigger> { NotifyTrigger.TaskFailed } }
        };
        var dispatcher = new RecordingNotificationDispatcher();
        var adapter = new MockAdapter((_, _) => MockStep.Error("失败原因"), TimeSpan.Zero, new FakeClock(Noon));
        var (engine, _, _, _) = Build(options, adapter, new List<TaskItem> { Plain("a") }, dispatcher);

        await engine.StartAsync();

        Assert.Contains(dispatcher.Contexts, c => c.Trigger == NotifyTrigger.TaskFailed);
    }

    [Fact]
    public async Task 未配置通知规则_不触发分发()
    {
        var dispatcher = new RecordingNotificationDispatcher();
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero, clock: new FakeClock(Noon));
        var (engine, _, _, _) = Build(Options(), adapter, new List<TaskItem> { Plain("a") }, dispatcher);

        await engine.StartAsync();

        Assert.Empty(dispatcher.Contexts);
    }
}
