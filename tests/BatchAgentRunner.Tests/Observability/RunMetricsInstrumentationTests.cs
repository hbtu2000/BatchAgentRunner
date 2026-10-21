using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Tests.Observability;

/// <summary>
/// 运行级指标埋点的引擎级契约（ARCH P-10 / A-8）。
/// </summary>
/// <remarks>
/// 单测（<see cref="RunMetricsTests"/>）锁的是"口径"，本文件锁的是"埋点真的被走到"——
/// 指标若埋错位置（例如只在成功路径计数、或漏了冷却跳过），单测全绿也毫无意义。
/// 断言一律直接读 <see cref="BatchEngine.Metrics"/>，不解析日志文本。
/// </remarks>
[Trait("Category", "Invariant")]
public class RunMetricsInstrumentationTests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 21, 12, 0);

    [Fact]
    public async Task 全部完成_记录吞吐与耗时且批次结束输出快照()
    {
        var logger = new FakeRunLogger();
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var engine = Build(Options(M("m1")), adapter, new[] { "task-001", "task-002", "task-003" }, logger);

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        var snapshot = engine.Metrics.Snapshot(Noon.AddMinutes(1));
        Assert.Equal(3, snapshot.TasksStarted);
        Assert.Equal(3, snapshot.TasksCompleted);
        Assert.Equal(3, snapshot.TasksTerminal);
        Assert.Equal(0, snapshot.FailureRate);

        // 关联三件套：引擎 / 日志 / 指标必须是同一个 RunId
        Assert.Equal(logger.RunId, engine.RunId);
        Assert.Equal(engine.RunId, snapshot.RunId);

        // 批次终态固定输出一次指标快照（不必再从日志里人肉数）
        var entry = logger.LastOf(LogEventNames.MetricsSnapshot);
        Assert.NotNull(entry);
        Assert.Equal(engine.RunId, entry!.RunId);
    }

    [Fact]
    public async Task 额度耗尽切换_记录降级次数()
    {
        var logger = new FakeRunLogger();
        var adapter = new MockAdapter((req, attempt) =>
            req.Model.Name == "m1" && attempt == 1
                ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(30))
                : MockStep.Ok("ok"));
        var engine = Build(Options(M("m1"), M("m2")), adapter, new[] { "task-001" }, logger);

        await engine.StartAsync();

        var snapshot = engine.Metrics.Snapshot(Noon.AddMinutes(1));
        Assert.True(snapshot.DegradationSwitches >= 1, $"降级切换未被埋点：{snapshot.DegradationSwitches}");
        Assert.Equal(1, snapshot.TasksCompleted);
        // 切到 m2 后任务成功 → 这次切换有效，不计误切换
        Assert.Equal(0, snapshot.Misswitches);
        Assert.Contains(logger.Entries, e => e.Event == LogEventNames.ModelSwitched);
    }

    [Fact]
    public async Task 冷却期内跳过_计跳过不计失败()
    {
        var logger = new FakeRunLogger();
        var adapter = new MockAdapter((req, _) =>
            req.Model.Name == "m1" ? MockStep.Quota(resetIn: TimeSpan.FromHours(2)) : MockStep.Ok());
        var engine = Build(Options(M("m1"), M("m2")), adapter, new[] { "task-001", "task-002" }, logger);

        await engine.StartAsync();

        var snapshot = engine.Metrics.Snapshot(Noon.AddMinutes(1));
        Assert.True(snapshot.CooldownSkips >= 1, $"冷却跳过未被埋点：{snapshot.CooldownSkips}");
        Assert.Equal(0, snapshot.TasksFailed);
        Assert.Equal(0, snapshot.FailureRate);
        Assert.Equal(2, snapshot.TasksCompleted);
    }

    [Fact]
    public async Task 事件流_全部携带RunId()
    {
        var logger = new FakeRunLogger();
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var events = new List<BatchAgentRunner.Core.Events.BatchEvent>();
        var engine = Build(Options(M("m1")), adapter, new[] { "task-001" }, logger);
        engine.EventRaised += (_, e) => events.Add(e);

        await engine.StartAsync();

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(engine.RunId, e.RunId));
    }

    // ---------------- 装配 ----------------

    private static BatchEngine Build(RunOptions options, IAgentCliAdapter adapter, IEnumerable<string> taskIds,
        FakeRunLogger logger)
    {
        var clock = new FakeClock(Noon);
        var store = new InMemoryTaskFileStore
        {
            Tasks = taskIds.Select(id => new TaskItem { Id = id, Instruction = "指令 " + id }).ToList()
        };
        var engine = new BatchEngine(options, adapter, store, new InMemoryCooldownStore(), logger,
            new BatchEngineDependencies { Clock = clock });
        if (adapter is MockAdapter mock)
        {
            mock.Clock = clock;
        }

        engine.InitializeAsync().Wait();
        return engine;
    }

    private static RunOptions Options(params ModelCandidate[] models) => new()
    {
        TaskFilePath = Path.Combine(Path.GetTempPath(), "bar-metrics-tasks.json"),
        AgentKind = AgentKind.Mock,
        Accounts = new List<AccountConfig> { new() { Id = "acc1", DisplayName = "账号 1" } },
        ModelCandidates = models.ToList(),
        Concurrency = 1,
        MaxRetriesPerTask = 1,
        RateLimitBackoffRetries = 1,
        TaskTimeoutMinutes = 1
    };

    private static ModelCandidate M(string name) => new() { Name = name };
}
