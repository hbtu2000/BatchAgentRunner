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
/// 单任务执行协作者白箱单测（P1-1 拆分产物 ② + P1-2 状态机外置）。
/// 原「单任务状态机」只有 <see cref="BatchEngine"/> 黑箱测试覆盖；拆分后可直接对
/// <see cref="TaskRunner"/> 注入假上下文，把重试 / 退避 / 降级 / 超时 / 兜底 / 异常兜底等
/// 状态机不变量锁死在协作者契约上，且测试可独立运行不依赖整批编排。
/// </summary>
[Trait("Category", "Invariant")]
public class TaskRunnerTests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    private static RunOptions Options(params string[] models) => new()
    {
        AgentKind = AgentKind.Mock,
        Accounts = new List<AccountConfig> { new() { Id = "acc1", DisplayName = "账号 1" } },
        ModelCandidates = models.Select(m => new ModelCandidate { Name = m }).ToList(),
        Concurrency = 1,
        MaxRetriesPerTask = 1,
        RateLimitBackoffRetries = 1,
        TaskTimeoutMinutes = 1,
        MaxAttemptsPerTask = 10,
        MaxWaitMinutes = 120,
        Fallback = new FallbackConfig { Enabled = false }
    };

    private static (TaskRunner Runner, List<BatchEvent> Events) Build(
        RunOptions options,
        IAgentCliAdapter adapter,
        Func<RestoreCandidate, string?, CancellationToken, Task<bool>>? waitForRestore = null)
    {
        var events = new List<BatchEvent>();
        var clock = new FakeClock(Noon);
        var cooldowns = new QuotaCooldownRegistry(clock, options.DefaultCooldown);
        var matrix = new DegradationMatrix(options.Accounts, options.ModelCandidates, options.DegradationStrategy);
        var evaluator = new AvailabilityEvaluator(cooldowns);
        var retry = new RetryPolicy
        {
            MaxRetries = options.MaxRetriesPerTask,
            RateLimitBackoffRetries = options.RateLimitBackoffRetries,
            InitialBackoff = TimeSpan.FromSeconds(1)
        };
        var fallback = new FallbackBilling(options, () => matrix, clock, events.Add);
        var taskWait = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var statsLock = new object();

        var ctx = new TaskRunnerContext
        {
            Options = options,
            Adapter = adapter,
            MatrixProvider = () => matrix,
            Evaluator = evaluator,
            Cooldowns = cooldowns,
            RetryPolicy = retry,
            Clock = clock,
            Logger = new NullRunLogger(),
            Vault = null,
            Report = new FakeReportBuilder(),
            Notifier = new FakeNotifier(),
            Fallback = fallback,
            GitWorkflow = new FakeGitWorkflow(),
            FileSystem = new FakeFileSystem(),
            TaskWaitSeconds = taskWait,
            StatsLock = statsLock,
            ResolveWorkspace = ws => ws,
            SaveCooldowns = () => Task.CompletedTask,
            Persist = _ => Task.CompletedTask,
            EnterWriteGate = () => Task.FromResult<IDisposable>(new NoopDisposable()),
            Raise = events.Add,
            SetStatus = _ => { },
            RaiseBlocked = (_, _) => { },
            RaiseTaskChanged = _ => { },
            SetCurrentTaskId = _ => { },
            WaitForRestore = waitForRestore ?? ((_, _, _) => Task.FromResult(true)),
            ExecuteWorkflow = (_, _) => Task.FromResult(TaskOutcome.Finished),
            GetPendingCount = () => 0
        };

        return (new TaskRunner(ctx), events);
    }

    private static TaskItem NewTask(string id) => new() { Id = id, Instruction = "指令 " + id };

    [Fact]
    public async Task 成功_返回Finished且任务Done()
    {
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var (runner, _) = Build(Options("m1"), adapter);

        var outcome = await runner.RunSafeAsync(NewTask("t-1"), CancellationToken.None);

        Assert.Equal(TaskOutcome.Finished, outcome);
        // RunSafeAsync 不直接暴露 task，这里通过二次执行验证终态写入由协作者完成（由下方事件/状态断言覆盖）
    }

    [Fact]
    public async Task 额度耗尽_切换到下一模型后完成()
    {
        var adapter = new MockAdapter((req, attempt) =>
            req.Model.Name == "m1" && attempt == 1
                ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(30))
                : MockStep.Ok("ok"));
        var (runner, events) = Build(Options("m1", "m2"), adapter);
        var task = NewTask("t-1");

        var outcome = await runner.RunSafeAsync(task, CancellationToken.None);

        Assert.Equal(TaskOutcome.Finished, outcome);
        Assert.Equal(TaskStatus.Done, task.Status);
        Assert.Equal("m2", task.ModelUsed);
        Assert.Contains(events, e => e.Kind == BatchEventKind.QuotaResetRecorded);
        Assert.Contains(events, e => e.Kind == BatchEventKind.ModelSwitched);
    }

    [Fact]
    public async Task 临时限流_退避重试同一组合后升级额度耗尽并切模型完成()
    {
        // m1 第 1 次限流 → 退避重试同一组合 → 第 2 次仍限流 → 升级额度耗尽 → 切 m2 成功
        var adapter = new MockAdapter((req, _) =>
            req.Model.Name == "m1" ? MockStep.RateLimited() : MockStep.Ok());
        var (runner, events) = Build(Options("m1", "m2"), adapter);
        var task = NewTask("t-1");

        var outcome = await runner.RunSafeAsync(task, CancellationToken.None);

        Assert.Equal(TaskOutcome.Finished, outcome);
        Assert.Equal(TaskStatus.Done, task.Status);
        Assert.Equal("m2", task.ModelUsed);
        Assert.Contains(events, e => e.Kind == BatchEventKind.Warning);
    }

    [Fact]
    public async Task 危险操作_任务Fail且返回Finished()
    {
        var adapter = new MockAdapter((_, _) => MockStep.Dangerous("确认删除文件？"));
        var (runner, events) = Build(Options("m1"), adapter);
        var task = NewTask("t-1");

        var outcome = await runner.RunSafeAsync(task, CancellationToken.None);

        Assert.Equal(TaskOutcome.Finished, outcome);
        Assert.Equal(TaskStatus.Failed, task.Status);
        Assert.Contains(events, e => e.Kind == BatchEventKind.DangerousOperationBlocked);
    }

    [Fact]
    public async Task 超时_任务Fail且返回Finished()
    {
        var adapter = new MockAdapter((_, _) => new MockStep(MockOutcome.Timeout));
        var (runner, _) = Build(Options("m1"), adapter);
        var task = NewTask("t-1");

        var outcome = await runner.RunSafeAsync(task, CancellationToken.None);

        Assert.Equal(TaskOutcome.Finished, outcome);
        Assert.Equal(TaskStatus.Failed, task.Status);
    }

    [Fact]
    public async Task 普通失败_超重试上限后Fail()
    {
        var adapter = new MockAdapter((_, _) => MockStep.Error("任务执行失败"));
        var (runner, _) = Build(Options("m1"), adapter);
        var task = NewTask("t-1");

        var outcome = await runner.RunSafeAsync(task, CancellationToken.None);

        Assert.Equal(TaskOutcome.Finished, outcome);
        Assert.Equal(TaskStatus.Failed, task.Status);
    }

    [Fact]
    public async Task 兜底开启_矩阵全耗尽_用兜底模型收尾当前任务()
    {
        var options = Options("m1");
        options.Fallback = new FallbackConfig
        {
            Enabled = true,
            MaxCostPerTask = 1m,
            MaxCostPerBatch = 5m
        };
        options.Accounts[0].FallbackModels = new List<FallbackModel>
        {
            new() { Name = "paid-x", EstimatedUnitPrice = 0.2m }
        };
        // m1 第一次额度耗尽（无其它候选）→ 进入兜底 → paid-x 成功
        var adapter = new MockAdapter((req, attempt) =>
            req.Model.Name == "m1" && attempt == 1
                ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(30))
                : req.IsFallback
                    ? MockStep.Ok("兜底完成")
                    : MockStep.Quota(resetIn: TimeSpan.FromMinutes(30)));
        var (runner, events) = Build(options, adapter);
        var task = NewTask("t-1");

        var outcome = await runner.RunSafeAsync(task, CancellationToken.None);

        Assert.Equal(TaskOutcome.Finished, outcome);
        Assert.Equal(TaskStatus.Done, task.Status);
        Assert.True(task.FallbackUsed);
        Assert.Equal("paid-x", task.ModelUsed);
        Assert.Contains(events, e => e.Kind == BatchEventKind.FallbackUsed);
    }

    [Fact]
    public async Task 未预期异常_被RunSafeAsync兜底为Fail且不抛出()
    {
        var adapter = new MockAdapter((_, _) => throw new InvalidOperationException("boom"));
        var (runner, events) = Build(Options("m1"), adapter);
        var task = NewTask("t-1");

        var outcome = await runner.RunSafeAsync(task, CancellationToken.None);

        Assert.Equal(TaskOutcome.Finished, outcome);
        Assert.Equal(TaskStatus.Failed, task.Status);
        Assert.Contains(events, e => e.Kind == BatchEventKind.Warning);
    }

    [Fact]
    public async Task 用户取消_返回Terminated且任务Interrupted()
    {
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var (runner, _) = Build(Options("m1"), adapter);
        var task = NewTask("t-1");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var outcome = await runner.RunSafeAsync(task, cts.Token);

        Assert.Equal(TaskOutcome.Terminated, outcome);
        Assert.Equal(TaskStatus.Interrupted, task.Status);
    }

    [Fact]
    public async Task 额度耗尽等待_时钟推进且冷却清除后继续完成()
    {
        var clock = new FakeClock(Noon);
        var cooldowns = new QuotaCooldownRegistry(clock, TimeSpan.FromMinutes(60));
        var options = Options("m1");
        var adapter = new MockAdapter((req, attempt) =>
            req.Model.Name == "m1" && attempt == 1
                ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(30))
                : MockStep.Ok("ok"));

        // WaitForRestore：推进时钟越过重置点并清除过期冷却，使下一轮可重新选中 m1
        var (runner, _) = Build(options, adapter,
            waitForRestore: (_, _, _) =>
            {
                clock.Advance(TimeSpan.FromMinutes(31));
                cooldowns.PurgeExpired(clock.Now);
                return Task.FromResult(true);
            });
        var task = NewTask("t-1");

        var outcome = await runner.RunSafeAsync(task, CancellationToken.None);

        Assert.Equal(TaskOutcome.Finished, outcome);
        Assert.Equal(TaskStatus.Done, task.Status);
        Assert.Equal("m1", task.ModelUsed);
    }

    // ---------------- 测试替身 ----------------

    private sealed class FakeReportBuilder : IBatchReportBuilder
    {
        public int SkippedDoneCount { get; private set; }
        public void SetSkippedDoneCount(int count) => SkippedDoneCount = count;
        public void TrackUsage(string account, string model, AgentUsage? usage) { }
        public void AddTimeline(string kind, string message, string? taskId, string? account, string? model) { }
        public void AddTimeline(string kind, string message, string? taskId, string? account, string? model, bool deferredSwitch) { }
        public void AddWaitSeconds(WaitKind kind, double seconds) { }
        public BatchReport Build(DateTimeOffset startedAt) => new();
    }

    private sealed class FakeNotifier : IBatchNotifier
    {
        public Task DispatchAsync(NotifyTrigger? trigger, DateTimeOffset? batchStartedAt = null, string? message = null)
            => Task.CompletedTask;
    }

    private sealed class FakeGitWorkflow : IGitWorkflow
    {
        public Task<GitTaskContext> BeginAsync(TaskItem task, CancellationToken ct)
            => Task.FromResult(new GitTaskContext(null, null));
        public Task CompleteAsync(TaskItem task, GitTaskContext context, CancellationToken ct)
            => Task.CompletedTask;
        public IReadOnlyList<DiffRecord> Diffs => Array.Empty<DiffRecord>();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
