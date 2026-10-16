using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.Logging;
using Xunit;

namespace BatchAgentRunner.Tests.Engine;

public class BatchEngineTests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    private static RunOptions Options(params ModelCandidate[] models) => new()
    {
        TaskFilePath = Path.Combine(Path.GetTempPath(), "bar-engine-tasks.json"),
        AgentKind = AgentKind.Mock,
        Accounts = new List<AccountConfig> { new() { Id = "acc1", DisplayName = "账号 1" } },
        ModelCandidates = models.ToList(),
        Concurrency = 1,
        MaxRetriesPerTask = 1,
        RateLimitBackoffRetries = 1,
        TaskTimeoutMinutes = 1
    };

    private static ModelCandidate M(string name, FreeTimeWindow? window = null) =>
        new() { Name = name, FreeTimeWindow = window };

    private static (BatchEngine Engine, InMemoryTaskFileStore Store, FakeClock Clock, List<BatchEvent> Events) Build(
        RunOptions options, IAgentCliAdapter adapter, IEnumerable<string> taskIds, DateTimeOffset now)
    {
        var clock = new FakeClock(now);
        var store = new InMemoryTaskFileStore
        {
            Tasks = taskIds.Select(id => new TaskItem { Id = id, Instruction = "指令 " + id }).ToList()
        };
        var engine = new BatchEngine(options, adapter, store, new InMemoryCooldownStore(), new NullRunLogger(), clock);
        if (adapter is MockAdapter mock)
        {
            mock.Clock = clock;
        }

        engine.InitializeAsync().Wait();

        var events = new List<BatchEvent>();
        engine.EventRaised += (_, e) => events.Add(e);

        return (engine, store, clock, events);
    }

    [Fact]
    public async Task 全部成功_批处理完成并回写()
    {
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var (engine, store, _, events) = Build(Options(M("m1")), adapter,
            new[] { "task-001", "task-002", "task-003" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(3, report.Done);
        Assert.All(store.Tasks, t => Assert.Equal(TaskStatus.Done, t.Status));
        Assert.Equal("m1", store.Tasks[0].ModelUsed);
        Assert.All(store.Tasks, t => Assert.Equal("账号 1", t.AccountUsed));
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchCompleted);
    }

    [Fact]
    public async Task 额度耗尽_同账号切换到下一个候选模型_任务继续()
    {
        // m1 在第 1 次调用额度耗尽，m2 成功
        var adapter = new MockAdapter((req, attempt) =>
            req.Model.Name == "m1" && attempt == 1
                ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(30))
                : MockStep.Ok("ok"));
        var (engine, store, _, events) = Build(Options(M("m1"), M("m2")), adapter, new[] { "task-001" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.Equal("m2", store.Tasks[0].ModelUsed);
        Assert.Contains(events, e => e.Kind == BatchEventKind.ModelSwitched);
        Assert.Contains(events, e => e.Kind == BatchEventKind.QuotaResetRecorded);

        // m1 进入冷却，冷却期内不再被调用
        Assert.True(engine.Cooldowns.IsCooling("acc1", "m1", Noon.AddMinutes(5), out var resetAt));
        Assert.Equal(Noon.AddMinutes(30), resetAt);
    }

    [Fact]
    public async Task 冷却期内不对已耗尽组合发起调用()
    {
        var calls = new List<string>();
        var adapter = new MockAdapter((req, _) =>
        {
            calls.Add(req.Model.Name);
            return req.Model.Name == "m1" ? MockStep.Quota(resetIn: TimeSpan.FromHours(2)) : MockStep.Ok();
        });
        var (engine, store, _, _) = Build(Options(M("m1"), M("m2")), adapter,
            new[] { "task-001", "task-002" }, Noon);

        await engine.StartAsync();

        // m1 第 1 条任务耗尽后进入冷却，后续任务直接跳过 m1（冷却期内无效调用 = 0）
        Assert.Equal(1, calls.Count(c => c == "m1"));
        Assert.Equal(2, calls.Count(c => c == "m2"));
    }

    [Fact]
    public async Task 免费组合全耗尽_兜底开启_用兜底模型收尾当前任务()
    {
        var options = Options(M("m1"));
        options.Fallback = new FallbackConfig
        {
            Enabled = true, MaxCostPerTask = 1m, MaxCostPerBatch = 5m
        };
        // v1.12：兜底模型池挂在账号下
        options.Accounts[0].FallbackModels = new List<FallbackModel>
        {
            new() { Name = "paid-x", EstimatedUnitPrice = 0.2m }
        };

        var adapter = new MockAdapter((req, attempt) =>
            req.IsFallback ? MockStep.Ok("兜底完成", cost: 0.2m)
                           : MockStep.Quota(resetIn: TimeSpan.FromHours(3)));
        var (engine, store, _, events) = Build(options, adapter, new[] { "task-001" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.True(store.Tasks[0].FallbackUsed);
        Assert.Equal(0.2m, store.Tasks[0].FallbackCost);
        Assert.Equal("paid-x", store.Tasks[0].ModelUsed);
        Assert.Contains(events, e => e.Kind == BatchEventKind.FallbackUsed);
        Assert.Equal(1, report.Fallback.UsedCount);
    }

    [Fact]
    public async Task 兜底池按账号顺序与账号内顺序选取第一个可用组合()
    {
        var options = Options(M("m1"));
        options.Fallback = new FallbackConfig { Enabled = true, MaxCostPerTask = 1m, MaxCostPerBatch = 5m };
        options.Accounts[0].FallbackModels = new List<FallbackModel>
        {
            new() { Name = "paid-a1", EstimatedUnitPrice = 0.2m },
            new() { Name = "paid-a2", EstimatedUnitPrice = 0.3m }
        };
        var acc2 = new AccountConfig { Id = "acc2", DisplayName = "账号 2" };
        acc2.FallbackModels = new List<FallbackModel> { new() { Name = "paid-b1", EstimatedUnitPrice = 0.1m } };
        options.Accounts.Add(acc2);

        var adapter = new MockAdapter((req, _) =>
            req.IsFallback ? MockStep.Ok("兜底完成", cost: 0.2m)
                           : MockStep.Quota(resetIn: TimeSpan.FromHours(3)));
        var (engine, store, _, _) = Build(options, adapter, new[] { "task-001" }, Noon);

        var report = await engine.StartAsync();

        // 按「账号顺序 × 账号内兜底池顺序」：acc1 的第一个兜底模型被选中
        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.Equal("paid-a1", store.Tasks[0].ModelUsed);
        Assert.Equal("账号 1", store.Tasks[0].AccountUsed);
        Assert.Equal("paid-a1", report.Fallback.Model);
    }

    [Fact]
    public async Task 兜底关闭_免费组合全耗尽且无可等待_进入阻塞并保留部分输出()
    {
        var adapter = new MockAdapter((_, _) => MockStep.Quota(resetIn: TimeSpan.FromHours(5)));
        var (engine, store, _, events) = Build(Options(M("m1")), adapter, new[] { "task-001" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Equal(TaskStatus.Interrupted, store.Tasks[0].Status);
        Assert.NotNull(store.Tasks[0].Error);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchBlocked);
    }

    [Fact]
    public async Task 全部组合冷却中_按最早恢复时间等待后继续执行()
    {
        var adapter = new MockAdapter((req, attempt) =>
            attempt == 1 ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(5)) : MockStep.Ok("恢复后完成"));
        var (engine, store, clock, events) = Build(Options(M("m1")), adapter, new[] { "task-001" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchWaiting);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchResumed);
        Assert.True(clock.Now >= Noon.AddMinutes(5));
        Assert.True(report.CooldownWaitSeconds > 0);
    }

    [Fact]
    public async Task 启动前全部组合时段外_允许等待则等待到时段开始后完成()
    {
        var start = At.Local(2026, 10, 15, 21, 59);
        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var (engine, store, clock, events) = Build(Options(M("m1", window)), adapter, new[] { "task-001" }, start);

        var report = await engine.StartAsync(waitIfUnavailable: true);

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchWaiting);
        Assert.True(clock.Now >= At.Local(2026, 10, 15, 22, 0));
    }

    [Fact]
    public async Task 启动前全部组合时段外_不允许等待则以阻塞退出()
    {
        var start = At.Local(2026, 10, 15, 8, 0); // 下一时段 22:00，超出默认 60 分钟最长等待
        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var (engine, _, _, events) = Build(Options(M("m1", window)), adapter, new[] { "task-001" }, start);

        var report = await engine.StartAsync(waitIfUnavailable: false);

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchBlocked);
    }

    [Fact]
    public async Task 预检_发现全部组合不可用并给出最早恢复时间()
    {
        var start = At.Local(2026, 10, 15, 8, 0);
        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var (engine, _, _, _) = Build(Options(M("m1", window)), new MockAdapter(), new[] { "task-001" }, start);

        var preflight = await engine.PreflightAsync();

        Assert.True(preflight.AllCombosUnavailable);
        Assert.NotNull(preflight.EarliestRestoreAt);
        Assert.Equal(WaitKind.FreeWindow, preflight.WaitKind);
    }

    [Fact]
    public async Task 危险操作_拒绝自动批准_该任务失败且不阻塞后续()
    {
        var adapter = new MockAdapter((req, _) =>
            req.TaskId == "task-002" ? MockStep.Dangerous("删除文件") : MockStep.Ok());
        var (engine, store, _, events) = Build(Options(M("m1")), adapter,
            new[] { "task-001", "task-002", "task-003" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(2, report.Done);
        Assert.Equal(1, report.Failed);
        Assert.Equal(TaskStatus.Failed, store.Tasks[1].Status);
        Assert.Contains("危险", store.Tasks[1].Error!);
        Assert.Contains(events, e => e.Kind == BatchEventKind.DangerousOperationBlocked);
    }

    [Fact]
    public async Task 临时限流先退避重试同一组合_不立即切换()
    {
        var calls = new List<string>();
        var adapter = new MockAdapter((req, attempt) =>
        {
            calls.Add($"{req.Model.Name}#{attempt}");
            return attempt == 1 ? MockStep.RateLimited() : MockStep.Ok();
        });
        var (engine, store, _, _) = Build(Options(M("m1"), M("m2")), adapter, new[] { "task-001" }, Noon);

        await engine.StartAsync();

        // 限流先退避重试同一组合，未切换模型
        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.Equal("m1", store.Tasks[0].ModelUsed);
        Assert.Equal(new[] { "m1#1", "m1#2" }, calls);
    }

    [Fact]
    public async Task 终止后当前任务中断_续跑跳过已完成任务()
    {
        var adapter = new MockAdapter((_, _) => MockStep.Ok(), stepDuration: TimeSpan.FromMilliseconds(300));
        var (engine, store, _, _) = Build(Options(M("m1")), adapter,
            new[] { "task-001", "task-002", "task-003" }, Noon);

        var run = engine.StartAsync();
        await Task.Delay(400);
        await engine.TerminateAsync();
        var firstReport = await run;

        Assert.Equal(BatchStatus.Terminated, firstReport.BatchStatus);
        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.Contains(store.Tasks, t => t.Status == TaskStatus.Interrupted);
        Assert.Contains(store.Tasks, t => t.Status == TaskStatus.Pending);

        // 续跑：只处理未完成的任务
        var resumed = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, resumed.BatchStatus);
        Assert.Equal(1, resumed.SkippedAlreadyDone);
        Assert.All(store.Tasks, t => Assert.Equal(TaskStatus.Done, t.Status));
    }

    [Fact]
    public async Task 失败即停_开启后单任务失败即暂停()
    {
        var options = Options(M("m1"));
        options.StopOnFailure = true;
        var adapter = new MockAdapter((req, _) =>
            req.TaskId == "task-001" ? MockStep.Error("指令错误") : MockStep.Ok());
        var (engine, store, _, _) = Build(options, adapter, new[] { "task-001", "task-002" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Equal(TaskStatus.Failed, store.Tasks[0].Status);
        Assert.Equal(TaskStatus.Pending, store.Tasks[1].Status);
        Assert.Single(report.FailedTasks);
    }

    [Fact]
    public async Task 受控并发_多任务并行完成()
    {
        var options = Options(M("m1"));
        options.Concurrency = 3;
        var adapter = new MockAdapter(stepDuration: TimeSpan.FromMilliseconds(50));
        var (engine, store, _, _) = Build(options, adapter,
            new[] { "task-001", "task-002", "task-003", "task-004" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(4, report.Done);
    }
}
