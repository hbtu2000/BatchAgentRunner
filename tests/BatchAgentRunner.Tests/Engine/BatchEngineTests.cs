using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.Logging;
using Xunit;

namespace BatchAgentRunner.Tests.Engine;

[Trait("Category", "Invariant")]
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
        RunOptions options, IAgentCliAdapter adapter, IEnumerable<string> taskIds, DateTimeOffset now,
        IFileSystem? fileSystem = null)
    {
        var clock = new FakeClock(now);
        var store = new InMemoryTaskFileStore
        {
            Tasks = taskIds.Select(id => new TaskItem { Id = id, Instruction = "指令 " + id }).ToList()
        };
        var engine = new BatchEngine(options, adapter, store, new InMemoryCooldownStore(), new NullRunLogger(),
            new BatchEngineDependencies { Clock = clock, FileSystem = fileSystem });
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
            Enabled = true,
            MaxCostPerTask = 1m,
            MaxCostPerBatch = 5m
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
    public async Task 兜底关闭_免费组合全耗尽且关闭额度无限等待_超出最长可等待_进入阻塞()
    {
        // 关闭 wait_for_quota_reset 后，额度耗尽等待同样受 max_wait 约束：5 小时重置远超 60 分钟 → 阻塞（R5）
        var options = Options(M("m1"));
        options.WaitForQuotaReset = false;
        var adapter = new MockAdapter((_, _) => MockStep.Quota(resetIn: TimeSpan.FromHours(5)));
        var (engine, store, _, events) = Build(options, adapter, new[] { "task-001" }, Noon);

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
    public async Task 等待中调用取消_转入阻塞()
    {
        // REQ-003b：取消 waiting 后应转入 blocked，而非跑完。冻结时钟使等待循环挂起，
        // 期间调用 CancelWaiting 唤醒并令等待循环以「已取消」提前退出。
        var start = At.Local(2026, 10, 15, 8, 0); // 下一时段 22:00，远超最长可等待
        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var (engine, _, clock, events) = Build(Options(M("m1", window)), adapter, new[] { "task-001" }, start);
        clock.AutoAdvance = false; // 冻结时间：等待循环停在首个 2 秒切片，不向前推进

        var task = engine.StartAsync(waitIfUnavailable: true);
        engine.CancelWaiting();
        var report = await task;

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchBlocked);
        Assert.Equal("启动前等待被取消", events.First(e => e.Kind == BatchEventKind.BatchBlocked).Message);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchWaiting);
    }

    [Fact]
    public async Task 等待中配置变更补充可用组合_提前退出等待并继续()
    {
        // REQ-004c：waiting 期间补充额度/新增模型使某组合可用，应即时重评并提前退出等待继续执行。
        var start = At.Local(2026, 10, 15, 8, 0); // 下一时段 22:00
        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var options = Options(M("m1", window));
        var (engine, store, clock, events) = Build(options, adapter, new[] { "task-001" }, start);
        clock.AutoAdvance = false; // 冻结时间：等待循环挂起于首个切片

        var task = engine.StartAsync(waitIfUnavailable: true);
        // 等待循环挂起中：补充一个无窗口模型，使矩阵重建后出现可用组合
        options.ModelCandidates.Add(new ModelCandidate { Name = "m2" });
        engine.NotifyConfigurationChanged();
        clock.AutoAdvance = true; // 放行重评后的派发（MockAdapter stepDuration=零本身不依赖时钟）
        var report = await task;

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.Equal("m2", store.Tasks[0].ModelUsed);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchResumed);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchWaiting);
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
    public async Task 预检_任务文件存在性走IFileSystem端口()
    {
        // P1-10 不变量：任务文件存在性判断必须经注入的 IFileSystem（而非直接碰磁盘），
        // 因此「真实磁盘不存在、端口报告存在」时预检不得报任务文件缺失。
        var options = Options(M("m1"));
        var fake = new FakeFileSystem().WithFile(options.TaskFilePath);
        var (engine, _, _, _) = Build(options, new MockAdapter(), new[] { "task-001" }, Noon, fake);

        var preflight = await engine.PreflightAsync();

        Assert.DoesNotContain(preflight.Errors, e => e.Contains("任务文件不存在"));
        Assert.Contains(fake.ProbedFiles, p => p == engine.Options.TaskFilePath);
    }

    [Fact]
    public async Task 预检_任务文件经端口报告缺失时给出错误()
    {
        // 反向：端口报告不存在（即使磁盘上真有文件）也必须拦截——证明判断确实以端口为准。
        var realPath = Path.GetTempFileName();
        try
        {
            var options = Options(M("m1"));
            options.TaskFilePath = realPath;
            var (engine, _, _, _) = Build(options, new MockAdapter(), new[] { "task-001" }, Noon, new FakeFileSystem());

            var preflight = await engine.PreflightAsync();

            Assert.Contains(preflight.Errors, e => e.Contains("任务文件不存在"));
        }
        finally
        {
            File.Delete(realPath);
        }
    }

    [Fact]
    public async Task 预检_工作空间存在性走IFileSystem端口()
    {
        // P1-10 不变量：工作空间目录存在性同样经 IFileSystem 端口；
        // 端口报告存在时不得误报（即使该目录在真实磁盘上不存在）。
        var wsPath = Path.Combine(Path.GetTempPath(), "bar-nonexistent-ws-" + Guid.NewGuid().ToString("N"));
        var options = Options(M("m1"));
        options.Workspaces = new List<WorkspaceConfig> { new() { Name = "ws", Path = wsPath } };
        var fake = new FakeFileSystem().WithFile(options.TaskFilePath).WithDirectory(wsPath);
        var (engine, _, _, _) = Build(options, new MockAdapter(), new[] { "task-001" }, Noon, fake);

        var preflight = await engine.PreflightAsync();

        Assert.DoesNotContain(preflight.Errors, e => e.Contains("目录不存在"));
        Assert.Contains(fake.ProbedDirs, d => d == wsPath);
    }

    [Fact]
    public async Task 预检_工作空间经端口报告缺失时给出错误()
    {
        var wsPath = Path.Combine(Path.GetTempPath(), "bar-nonexistent-ws-" + Guid.NewGuid().ToString("N"));
        var options = Options(M("m1"));
        options.Workspaces = new List<WorkspaceConfig> { new() { Name = "ws", Path = wsPath } };
        // 任务文件经端口报告存在（否则预检在文件检查处提前返回，到不了工作空间校验）。
        var fake = new FakeFileSystem().WithFile(options.TaskFilePath);
        var (engine, _, _, _) = Build(options, new MockAdapter(), new[] { "task-001" }, Noon, fake);

        var preflight = await engine.PreflightAsync();

        Assert.Contains(preflight.Errors, e => e.Contains("目录不存在"));
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

    [Fact]
    public async Task 全部组合额度耗尽_超出最长可等待_仍无限等待重置后自动续跑完成()
    {
        // 恢复时间 90 分钟，远超默认 max_wait(60) 与单任务总时长上限(60)，
        // 但属于额度耗尽（QuotaReset）→ 无限等待直到重置后自动完成（R1）
        var adapter = new MockAdapter((req, attempt) =>
            attempt == 1 ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(90)) : MockStep.Ok("恢复后完成"));
        var (engine, store, clock, events) = Build(Options(M("m1")), adapter, new[] { "task-001" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchWaiting);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchResumed);
        Assert.True(clock.Now >= Noon.AddMinutes(90));
        // 额度冷却等待 ≈ 90 分钟
        Assert.Equal(90 * 60, report.CooldownWaitSeconds, 1);
    }

    [Fact]
    public async Task 额度耗尽暂停_暂停时长不计入批处理与任务时长()
    {
        // 单任务：首次额度耗尽、等待 90 分钟重置后完成
        var adapter = new MockAdapter((req, attempt) =>
            attempt == 1 ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(90)) : MockStep.Ok("恢复后完成"));
        var (engine, store, _, _) = Build(Options(M("m1")), adapter, new[] { "task-001" }, Noon);

        var report = await engine.StartAsync();

        // 净执行时长 = 墙钟 − 等待 = 0（无真实执行耗时），证明暂停被剔除（R2）
        Assert.Equal(0, report.ActiveDurationSeconds, 1);
        Assert.True(report.DurationSeconds > 1000, "墙钟总时长应包含等待");
        // 单任务时长也剔除本次等待
        Assert.Equal(0, store.Tasks[0].DurationSeconds, 3);
    }

    [Fact]
    public async Task 关闭wait_for_quota_reset_超出最长可等待_仍转阻塞()
    {
        // 关闭开关后，额度耗尽等待同样受 max_wait 约束，超出即阻塞（R5）
        var options = Options(M("m1"));
        options.WaitForQuotaReset = false;
        var adapter = new MockAdapter((_, _) => MockStep.Quota(resetIn: TimeSpan.FromMinutes(90)));
        var (engine, store, _, events) = Build(options, adapter, new[] { "task-001" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Equal(TaskStatus.Interrupted, store.Tasks[0].Status);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchBlocked);
    }

    [Fact]
    public async Task 纯免费时段外_不受额度无限等待影响_仍尊重最长可等待()
    {
        // 仅免费时段约束（FreeWindow，非额度耗尽）：即便开启 wait_for_quota_reset，
        // 仍受 max_wait 约束；下一时段远超 max_wait → 阻塞（R3）
        var start = At.Local(2026, 10, 15, 8, 0); // 下一时段 22:00，超出默认 60 分钟
        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero);
        var options = Options(M("m1", window));
        options.WaitForQuotaReset = true;
        var (engine, _, _, events) = Build(options, adapter, new[] { "task-001" }, start);

        // 执行路径（waitIfUnavailable 仅影响启动前预派发；此处走单任务重试循环）
        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchBlocked);
    }

    // ---------------- 技术债务 P0 不变量（治理方案 §4.6：行号型台账改为断言式登记） ----------------

    /// <summary>P0-1：兜底成本超单任务上限时必须被截断（原实现为 no-op，上限完全失效）。</summary>
    [Fact]
    public async Task 兜底实测成本超出单任务上限时按上限截断()
    {
        var options = Options(M("m1"));
        options.Fallback = new FallbackConfig { Enabled = true, MaxCostPerTask = 1m, MaxCostPerBatch = 5m };
        options.Accounts[0].FallbackModels = new List<FallbackModel>
        {
            new() { Name = "paid-x", EstimatedUnitPrice = 0.2m }
        };

        // 估算单价 0.2 通过上限校验，但实测花费 3.5 远超单任务上限 1.0
        var adapter = new MockAdapter((req, _) =>
            req.IsFallback ? MockStep.Ok("兜底完成", cost: 3.5m)
                           : MockStep.Quota(resetIn: TimeSpan.FromHours(3)));
        var (engine, store, _, _) = Build(options, adapter, new[] { "task-001" }, Noon);

        var report = await engine.StartAsync();

        Assert.Equal(TaskStatus.Done, store.Tasks[0].Status);
        Assert.Equal(1m, store.Tasks[0].FallbackCost);
        Assert.Equal(1m, report.Fallback.TotalCost);
    }

    /// <summary>检测并发落盘的存储替身：记录 SaveAsync 的最大并发数（P0-4 不变量 = 恒为 1）。</summary>
    private sealed class ConcurrencyDetectingTaskFileStore : ITaskFileStore
    {
        private readonly List<TaskItem> _tasks;
        private readonly object _sync = new();
        private int _inFlight;
        private int _maxConcurrentSaves;

        public ConcurrencyDetectingTaskFileStore(IEnumerable<TaskItem> tasks) => _tasks = tasks.ToList();

        public int MaxConcurrentSaves
        {
            get
            {
                lock (_sync)
                {
                    return _maxConcurrentSaves;
                }
            }
        }

        public int SaveCount { get; private set; }

        public IReadOnlyList<TaskItem> Tasks => _tasks;

        public Task<TaskFileLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskFileLoadResult(_tasks, path, false, Array.Empty<TaskFileIssue>()));

        public async Task SaveAsync(string path, IReadOnlyList<TaskItem> tasks,
            CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _inFlight);
            lock (_sync)
            {
                if (current > _maxConcurrentSaves)
                {
                    _maxConcurrentSaves = current;
                }
            }

            try
            {
                // 放大落盘窗口：并发写一旦存在就必然被观测到
                await Task.Delay(30, cancellationToken);
                _tasks.Clear();
                _tasks.AddRange(tasks);
                SaveCount++;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<string?> BackupAsync(string path, int retention, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public string ResolveResultPath(string path) => path;

        public bool IsMarkdown(string path) => false;
    }

    /// <summary>P0-4：并发下任务文件回写必须串行，且终态不回退。</summary>
    [Fact]
    public async Task 并发落盘串行化_终态不回退()
    {
        var options = Options(M("m1"));
        options.Concurrency = 4;
        var adapter = new MockAdapter(stepDuration: TimeSpan.FromMilliseconds(20));
        var tasks = Enumerable.Range(1, 8)
            .Select(i => new TaskItem { Id = $"task-{i:D3}", Instruction = "指令 " + i })
            .ToList();
        var store = new ConcurrencyDetectingTaskFileStore(tasks);
        var engine = new BatchEngine(options, adapter, store, new InMemoryCooldownStore(), new NullRunLogger(),
            new BatchEngineDependencies { Clock = new FakeClock(Noon) });
        await engine.InitializeAsync();

        var report = await engine.StartAsync();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(8, report.Done);
        // 单写者：任何时刻落盘中的写者不超过 1 个
        Assert.Equal(1, store.MaxConcurrentSaves);
        // 终态不回退：全部 8 条都留在已完成的落盘结果里
        Assert.All(store.Tasks, t => Assert.Equal(TaskStatus.Done, t.Status));
    }

    /// <summary>P0-5：并发下兜底成本与用量的累加必须准确（无锁累加会丢更新 / 越限）。</summary>
    [Fact]
    public async Task 并发下兜底成本与用量累计准确()
    {
        var options = Options(M("m1"));
        options.Concurrency = 4;
        // 并发下额度等待会推进共享的 FakeClock，放宽单任务时长上限，隔离出「累计是否准确」这一个变量
        options.MaxTotalMinutesPerTask = 60 * 24;
        options.Fallback = new FallbackConfig { Enabled = true, MaxCostPerTask = 1m, MaxCostPerBatch = 10m };
        options.Accounts[0].FallbackModels = new List<FallbackModel>
        {
            new() { Name = "paid-x", EstimatedUnitPrice = 0.2m }
        };
        var adapter = new MockAdapter((req, _) =>
                req.IsFallback ? MockStep.Ok("兜底完成", cost: 0.2m)
                               : MockStep.Quota(resetIn: TimeSpan.FromHours(3)),
            stepDuration: TimeSpan.FromMilliseconds(20));
        var (engine, store, _, _) = Build(options, adapter,
            Enumerable.Range(1, 8).Select(i => $"task-{i:D3}"), Noon);

        var report = await engine.StartAsync();

        Assert.Equal(8, report.Fallback.UsedCount);
        Assert.Equal(1.6m, report.Fallback.TotalCost);
        Assert.Equal("paid-x", report.Fallback.Model);
        Assert.All(store.Tasks, t => Assert.Equal(0.2m, t.FallbackCost));
    }
}
