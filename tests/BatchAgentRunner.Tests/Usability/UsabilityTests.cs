using System.Text.Json;
using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.Logging;
using BatchAgentRunner.Infrastructure.Persistence;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Tests.Usability;

/// <summary>
/// 可用性场景测试（用户旅程视角）：首次启动 / 手改配置写坏 / 预检指引 / 样例文件 /
/// 文档「5 分钟跑通」/ 终止 / 取消等待 / 重置冷却 / 兜底不误触发。
/// 与 EndToEndPipelineTests 互补：E2E 验证机制正确性，本套件验证「用户照文档操作能否走通」。
/// </summary>
[Trait("Category", "Usability")]
public class UsabilityTests
{
    // 2026-10-15 是周四；12:00 落在 22:00→06:00 免费时段之外
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    // ---------------- 公共脚手架 ----------------

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-ux-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (BatchEngine Engine, MockAdapter Adapter, FileRunLogger Logger, List<BatchEvent> Events) BuildEngine(
        RunOptions options, MockAdapter adapter, string dir, DateTimeOffset? now = null, FakeClock? clock = null)
    {
        clock ??= new FakeClock(now ?? Noon);
        adapter.Clock = clock;
        var logger = new FileRunLogger(options.TaskFilePath);
        var engine = new BatchEngine(options, adapter, new TaskFileStore(),
            new JsonCooldownStore(Path.Combine(dir, "state")), logger, clock);

        var events = new List<BatchEvent>();
        engine.EventRaised += (_, e) => events.Add(e);
        return (engine, adapter, logger, events);
    }

    private static RunOptions Options(string taskPath, params ModelCandidate[] models) => new()
    {
        TaskFilePath = taskPath,
        AgentKind = AgentKind.Mock,
        Accounts = new List<AccountConfig> { new() { Id = "acc1", DisplayName = "账号 1" } },
        ModelCandidates = models.ToList(),
        MaxRetriesPerTask = 0,
        RateLimitBackoffRetries = 1,
        RateLimitInitialBackoffSeconds = 1,
        TaskTimeoutMinutes = 1,
        BackupRetention = 5
    };

    private static ModelCandidate M(string name, FreeTimeWindow? window = null) =>
        new() { Name = name, FreeTimeWindow = window };

    private static List<TaskItem> ReadTasks(string path) =>
        JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(path), JsonDefaults.Options)!;

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var start = DateTime.UtcNow;
        while (!condition() && DateTime.UtcNow - start < TimeSpan.FromMilliseconds(timeoutMs))
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "等待条件超时（10 秒）");
    }

    // ---------------- TC-UX-001 首次启动 ----------------

    [Fact]
    public async Task TC_UX_001_首次启动无配置_示例配置可直接看懂且预检有明确指引()
    {
        // 新用户第一次启动：%LocalAppData% 下没有 config.json
        var store = new JsonConfigStore(Path.Combine(NewDir(), "conf"));
        Assert.False(File.Exists(Path.Combine(store.ConfigDirectory, "config.json")));

        var options = await store.LoadAsync();

        // 断言 1：示例配置结构完整（账号池 / 模型 / 工作区都可直接改着用）
        Assert.Equal(2, options.Accounts.Count);
        Assert.Equal(4, options.ModelCandidates.Count);
        Assert.Contains(options.Workspaces, w => w.IsGlobalDefault);
        Assert.Equal(AgentKind.WorkBuddy, options.AgentKind);

        // 断言 2：任务文件留空 → 预检给出「下一步做什么」的明确指引，而不是崩溃或报错堆栈
        Assert.Equal(string.Empty, options.TaskFilePath);
        var taskPath = Path.Combine(NewDir(), "tasks.json");
        await File.WriteAllTextAsync(taskPath, "[]");
        var (engine, _, logger, _) = BuildEngine(options, new MockAdapter(), NewDir());
        var preflight = await engine.PreflightAsync();
        logger.Dispose();

        Assert.False(preflight.CanStart);
        Assert.Contains(preflight.Errors, e => e.Contains("未选择任务文件"));
    }

    // ---------------- TC-UX-002 手改配置写坏 ----------------

    [Fact]
    public async Task TC_UX_002_手改配置写坏JSON_程序不崩溃且回退示例()
    {
        var dir = Path.Combine(NewDir(), "conf");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        await File.WriteAllTextAsync(configPath, "{ \"task_file_path\": \"x.json\", // 忘了闭合括号");

        var store = new JsonConfigStore(dir);
        var options = await store.LoadAsync(); // 不应抛异常

        // 断言：回退到示例配置（用户手改内容在内存中被重置）
        Assert.Equal(string.Empty, options.TaskFilePath);
        Assert.Equal(2, options.Accounts.Count);

        // 断言：磁盘上的坏文件未被删除/覆盖（用户还有机会手工修复）
        var raw = await File.ReadAllTextAsync(configPath);
        Assert.Contains("task_file_path", raw);
    }

    // ---------------- TC-UX-003 预检错误文案矩阵 ----------------

    [Fact]
    public async Task TC_UX_003_多项配置缺失_预检一次性收集全部中文指引()
    {
        var (engine, _, logger, _) = BuildEngine(new RunOptions
        {
            TaskFilePath = string.Empty,
            Accounts = new List<AccountConfig>(),
            ModelCandidates = new List<ModelCandidate>()
        }, new MockAdapter(), NewDir());

        var report = await engine.PreflightAsync(); // 不抛异常
        logger.Dispose();

        Assert.False(report.CanStart);
        Assert.Contains(report.Errors, e => e.Contains("未选择任务文件"));
        Assert.Contains(report.Errors, e => e.Contains("账号池为空"));
        Assert.Contains(report.Errors, e => e.Contains("降级矩阵为空"));
    }

    // ---------------- TC-UX-004 仓库样例文件可被直接使用 ----------------

    [Fact]
    public async Task TC_UX_004_文档承诺的样例任务文件_两种格式均可解析()
    {
        var store = new TaskFileStore();

        // samples/tasks.json（文档 §1.1 第 2 步引用）—— P2-21/P2-28：改读嵌入式资源副本
        var jsonPath = Path.Combine(NewDir(), "tasks.json");
        File.WriteAllText(jsonPath, TestSampleResources.ReadText("tasks.json"));
        var json = await store.LoadAsync(jsonPath);
        Assert.Empty(json.Issues);
        Assert.Equal(2, json.Tasks.Count);
        Assert.All(json.Tasks, t => Assert.Equal(TaskStatus.Pending, t.Status));
        Assert.All(json.Tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Instruction)));

        // samples/tasks.md（文档 §1 声称 .md 只读输入）
        var mdPath = Path.Combine(NewDir(), "tasks.md");
        File.WriteAllText(mdPath, TestSampleResources.ReadText("tasks.md"));
        var md = await store.LoadAsync(mdPath);
        Assert.Empty(md.Issues);
        Assert.Equal(3, md.Tasks.Count);
        Assert.Equal("task-001", md.Tasks[0].Id);
        Assert.Contains("快速开始", md.Tasks[0].Instruction);
    }

    // ---------------- TC-UX-005 「5 分钟跑通」Mock 全流程复刻 ----------------

    [Fact]
    public async Task TC_UX_005_按文档5分钟跑通_样例副本预检通过到全量回写done()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        File.WriteAllText(taskPath, TestSampleResources.ReadText("tasks.json"));

        var options = Options(taskPath, M("model-a"));
        var (engine, adapter, logger, events) = BuildEngine(options, new MockAdapter(), dir);

        // 文档 §1.1 第 3 步：预检应显示"预检通过：2 条任务，待处理 2 条"
        var preflight = await engine.PreflightAsync();
        Assert.True(preflight.CanStart, "预检未通过：" + string.Join("；", preflight.Errors));
        Assert.Equal(2, preflight.LoadedTaskCount);
        Assert.Equal(2, preflight.PendingTaskCount);

        // 文档 §1.1 第 4 步：开始 → 完成，任务文件 status 回写 done
        var report = await engine.StartAsync();
        logger.Dispose();

        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.All(ReadTasks(taskPath), t => Assert.Equal(TaskStatus.Done, t.Status));
        Assert.Equal(2, adapter.TotalCalls);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchCompleted);
    }

    // ---------------- TC-UX-006 兜底不误触发 ----------------

    [Fact]
    public async Task TC_UX_006_组合从未开跑过_兜底不触发直接阻塞()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        await File.WriteAllTextAsync(taskPath,
            JsonDefaults.Serialize(new List<TaskItem> { new() { Id = "task-001", Instruction = "仿真指令" } }));

        var adapter = new MockAdapter((_, _) =>
        {
            Assert.Fail("组合从未开跑，不应产生任何 CLI 调用");
            return MockStep.Ok();
        });
        var options = Options(taskPath, M("m1", window));
        options.Fallback = new FallbackConfig { Enabled = true };
        options.Accounts[0].FallbackModels = new List<FallbackModel>
        {
            new() { Name = "fb", EstimatedUnitPrice = 0.5m }
        };
        var (engine, _, logger, events) = BuildEngine(options, adapter, dir);

        var report = await engine.StartAsync(waitIfUnavailable: false);
        logger.Dispose();

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Equal(0, adapter.TotalCalls);
        Assert.DoesNotContain(events, e => e.Kind == BatchEventKind.FallbackUsed);
        Assert.Equal(TaskStatus.Pending, ReadTasks(taskPath).Single().Status);
    }

    // ---------------- TC-UX-007 取消等待 ----------------

    [Fact]
    public async Task TC_UX_007_等待恢复中手动取消_转阻塞并保留待处理任务()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await File.WriteAllTextAsync(taskPath,
            JsonDefaults.Serialize(new List<TaskItem> { new() { Id = "task-001", Instruction = "仿真指令" } }));

        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var options = Options(taskPath, M("m1", window));
        // 冻结时钟：等待循环的 DelayAsync 永不完成，引擎确定性地挂起在 Waiting，
        // 取消必然先于「时间推进到恢复点」到达——消除并行运行下的时序竞态。
        var (engine, adapter, logger, events) = BuildEngine(options, new MockAdapter(), dir,
            clock: new FakeClock(Noon) { AutoAdvance = false });

        var run = engine.StartAsync(waitIfUnavailable: true);
        // 事件驱动等待：BatchWaiting 在引擎线程同步触发即置信，替代轮询瞬时状态
        var waitingSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.EventRaised += (_, e) =>
        {
            if (e.Kind == BatchEventKind.BatchWaiting)
            {
                waitingSeen.TrySetResult();
            }
        };
        await waitingSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.CancelWaiting();

        var report = await run;
        logger.Dispose();

        Assert.Equal(BatchStatus.Blocked, report.BatchStatus);
        Assert.Equal(0, adapter.TotalCalls);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchWaiting);
        Assert.Contains(events, e => e.Message.Contains("等待被取消"));
        Assert.Equal(TaskStatus.Pending, ReadTasks(taskPath).Single().Status);
    }

    // ---------------- TC-UX-008 重置冷却后续跑 ----------------

    [Fact]
    public async Task TC_UX_008_额度耗尽转阻塞_重置冷却后续跑成功()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await File.WriteAllTextAsync(taskPath,
            JsonDefaults.Serialize(new List<TaskItem> { new() { Id = "task-001", Instruction = "仿真指令" } }));

        // 第一次：唯一模型额度耗尽 90 分钟（超出 max_wait 60 分钟）→ 无可切组合 → 阻塞。
        // 关闭 wait_for_quota_reset 以保留「手动重置冷却后续跑」这条 UX 路径（默认开启时额度耗尽会自动等待重置）。
        var adapter = new MockAdapter((_, attempt) =>
            attempt <= 1 ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(90)) : MockStep.Ok("重置后输出"));
        var options = Options(taskPath, M("m1"));
        options.WaitForQuotaReset = false;
        var (engine, _, logger1, events1) = BuildEngine(options, adapter, dir);

        var report1 = await engine.StartAsync(waitIfUnavailable: false);
        Assert.Equal(BatchStatus.Blocked, report1.BatchStatus);
        Assert.Equal(1, adapter.TotalCalls);
        Assert.Contains(events1, e => e.Kind == BatchEventKind.BatchBlocked);

        // 用户在监控页点「重置冷却」→ 立即重新评估可用
        engine.ResetAllCooldowns();

        // 点「续跑」→ 同一引擎实例恢复执行（对应界面 ResumeCommand 复用引擎）
        var report2 = await engine.StartAsync(waitIfUnavailable: false);
        logger1.Dispose();

        Assert.Equal(BatchStatus.Completed, report2.BatchStatus);
        Assert.Equal(2, adapter.TotalCalls);
        Assert.Equal(TaskStatus.Done, ReadTasks(taskPath).Single().Status);
    }

    // ---------------- TC-UX-009 运行中终止 ----------------

    [Fact]
    public async Task TC_UX_009_运行中点终止_当前任务中断未跑任务保留()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await File.WriteAllTextAsync(taskPath, JsonDefaults.Serialize(new List<TaskItem>
        {
            new() { Id = "task-001", Instruction = "仿真指令一" },
            new() { Id = "task-002", Instruction = "仿真指令二" }
        }));

        var options = Options(taskPath, M("m1"));
        var (engine, adapter, logger, _) = BuildEngine(options, new MockAdapter(stepDuration: TimeSpan.FromMilliseconds(300)), dir);

        var run = engine.StartAsync();
        await WaitUntilAsync(() => adapter.TotalCalls >= 1 && engine.CurrentTaskId is not null);
        await engine.TerminateAsync(waitCurrentTask: false);

        var report = await run;
        logger.Dispose();

        // 批状态终止，结果不丢
        Assert.Equal(BatchStatus.Terminated, report.BatchStatus);
        var saved = ReadTasks(taskPath);
        Assert.NotEqual(TaskStatus.Done, saved[0].Status);
        Assert.Equal(TaskStatus.Pending, saved[1].Status);
    }
}
