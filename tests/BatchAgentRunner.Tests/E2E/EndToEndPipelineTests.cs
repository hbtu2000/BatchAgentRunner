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
using BatchAgentRunner.Infrastructure.Reporting;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Tests.E2E;

/// <summary>
/// 管线级端到端测试：真实 Infrastructure（任务文件原子回写 / 备份 / 冷却落盘 /
/// 运行日志 / 报告写出）+ 真实 BatchEngine 调度 + MockAdapter 仿真执行，
/// 全部文件落在测试专属临时目录。用例定义见 tests/docs/01-E2E-TEST-CASES.md。
/// </summary>
public class EndToEndPipelineTests
{
    // 2026-10-15 是周四；12:00 落在 22:00→06:00 免费时段之外
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    // ---------------- 公共脚手架 ----------------

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-e2e-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir!.FullName;
    }

    private static RunOptions Options(string taskPath, params ModelCandidate[] models) => new()
    {
        TaskFilePath = taskPath,
        AgentKind = AgentKind.Mock,
        Accounts = new List<AccountConfig>
        {
            new() { Id = "acc1", DisplayName = "账号 1" }
        },
        ModelCandidates = models.ToList(),
        MaxRetriesPerTask = 0,
        RateLimitBackoffRetries = 1,
        RateLimitInitialBackoffSeconds = 1,
        TaskTimeoutMinutes = 1,
        BackupRetention = 5
    };

    private static ModelCandidate M(string name, FreeTimeWindow? window = null) =>
        new() { Name = name, FreeTimeWindow = window };

    private static async Task<string> WriteJsonTasksAsync(string path, params TaskItem[] tasks)
    {
        var json = JsonDefaults.Serialize(tasks.ToList());
        await File.WriteAllTextAsync(path, json);
        return json;
    }

    private static TaskItem TaskItem(string id, string instruction = "仿真指令") =>
        new() { Id = id, Instruction = instruction };

    private static (BatchEngine Engine, FakeClock Clock, FileRunLogger Logger, List<BatchEvent> Events) BuildEngine(
        RunOptions options, MockAdapter adapter, string cooldownDir, DateTimeOffset? now = null)
    {
        var clock = new FakeClock(now ?? Noon);
        adapter.Clock = clock;
        var logger = new FileRunLogger(options.TaskFilePath);
        var engine = new BatchEngine(options, adapter, new TaskFileStore(), new JsonCooldownStore(cooldownDir),
            logger, clock);

        var events = new List<BatchEvent>();
        engine.EventRaised += (_, e) => events.Add(e);

        engine.InitializeAsync().Wait();
        return (engine, clock, logger, events);
    }

    private static List<TaskItem> ReadTasks(string path) =>
        JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(path), JsonDefaults.Options)!;

    // ---------------- TC-E2E-001 ----------------

    [Fact]
    public async Task TC_E2E_001_JSON任务文件全链路_回写备份报告日志()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await WriteJsonTasksAsync(taskPath, TaskItem("task-001"), TaskItem("task-002"));

        var adapter = new MockAdapter((_, _) => MockStep.Ok("仿真输出完成"));
        var options = Options(taskPath, M("m1"));
        var (engine, _, logger, events) = BuildEngine(options, adapter, Path.Combine(dir, "state"));

        var report = await engine.StartAsync();
        logger.Dispose();

        // 断言 1：批完成
        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(2, report.Done);

        // 断言 2：原地原子回写
        var saved = ReadTasks(taskPath);
        Assert.All(saved, t => Assert.Equal(TaskStatus.Done, t.Status));
        Assert.All(saved, t => Assert.Equal("m1", t.ModelUsed));
        Assert.All(saved, t => Assert.Equal("账号 1", t.AccountUsed));

        // 断言 3：启动前备份保留原始 pending 内容
        var backups = Directory.GetFiles(dir, "tasks.json.backup-*");
        Assert.NotEmpty(backups);
        var backupText = await File.ReadAllTextAsync(backups[0]);
        Assert.Contains("pending", backupText, StringComparison.OrdinalIgnoreCase);

        // 断言 4：报告落盘
        var reportWriter = new ReportWriter();
        var mdPath = await reportWriter.WriteAsync(report, taskPath);
        var jsonPath = mdPath[..^3] + ".json";
        Assert.True(File.Exists(mdPath), $"报告 MD 未生成：{mdPath}");
        Assert.True(File.Exists(jsonPath), $"报告 JSON 未生成：{jsonPath}");
        Assert.Contains("批处理汇总报告", await File.ReadAllTextAsync(mdPath));

        // 断言 5：运行日志（logger 已释放，AutoFlush 保证内容已落盘）
        Assert.True(File.Exists(logger.LogFilePath), $"运行日志未生成：{logger.LogFilePath}");
        Assert.Contains("task_finished", await File.ReadAllTextAsync(logger.LogFilePath));
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchCompleted);
    }

    // ---------------- TC-E2E-002 ----------------

    [Fact]
    public async Task TC_E2E_002_MarkdownSidecar回写_重启续跑不重跑()
    {
        var dir = NewDir();
        var mdPath = Path.Combine(dir, "tasks.md");
        var originalMd = """
            # Tasks

            ## task-001
            - instruction: 任务一

            ## task-002
            - instruction: 任务二

            ## task-003
            - instruction: 任务三
            """;
        await File.WriteAllTextAsync(mdPath, originalMd);

        // 第一轮：task-002 普通失败（MaxRetries=0 直接终态）
        var adapter1 = new MockAdapter((req, _) =>
            req.TaskId == "task-002" ? MockStep.Error("任务执行失败") : MockStep.Ok("完成"));
        var options = Options(mdPath, M("m1"));
        var (engine1, clock1, logger1, _) = BuildEngine(options, adapter1, Path.Combine(dir, "state"));

        var report1 = await engine1.StartAsync();
        logger1.Dispose();

        Assert.Equal(BatchStatus.Completed, report1.BatchStatus);
        Assert.Equal(2, report1.Done);
        Assert.Equal(1, report1.Failed);

        // 断言 1：sidecar 生成且状态正确
        var sidecarPath = mdPath + ".result.json";
        Assert.True(File.Exists(sidecarPath), "sidecar 结果文件未生成");
        var sidecar = ReadTasks(sidecarPath);
        Assert.Equal(TaskStatus.Done, sidecar.Single(t => t.Id == "task-001").Status);
        Assert.Equal(TaskStatus.Failed, sidecar.Single(t => t.Id == "task-002").Status);
        Assert.Equal(TaskStatus.Done, sidecar.Single(t => t.Id == "task-003").Status);

        // 断言 2：Markdown 本体未被修改（只读输入）
        Assert.Equal(originalMd, await File.ReadAllTextAsync(mdPath));

        // 第二轮：全新引擎实例（模拟重启），sidecar 状态应全部保留
        var adapter2 = new MockAdapter((_, _) =>
        {
            Assert.Fail("续跑不应发起任何 CLI 调用");
            return MockStep.Ok();
        });
        var (engine2, clock2, logger2, _) = BuildEngine(options, adapter2, Path.Combine(dir, "state"),
            Noon.AddMinutes(1));

        var report2 = await engine2.StartAsync();
        logger2.Dispose();

        // 断言 3/4：零调用、跳过已完成、失败计数保留
        Assert.Equal(0, adapter2.TotalCalls);
        Assert.Equal(3, report2.SkippedAlreadyDone);
        Assert.Equal(BatchStatus.Completed, report2.BatchStatus);
        Assert.Equal(1, report2.Failed);
    }

    // ---------------- TC-E2E-003 ----------------

    [Fact]
    public async Task TC_E2E_003_冷却落盘_重启后仍生效零无效调用()
    {
        var dir = NewDir();
        var cooldownDir = Path.Combine(dir, "state");
        var taskPath = Path.Combine(dir, "tasks.json");
        await WriteJsonTasksAsync(taskPath, TaskItem("task-001"), TaskItem("task-002"));

        // 第一轮：m1 恒额度耗尽（60 分钟后重置），m2 成功
        var adapter1 = new MockAdapter((req, _) =>
            req.Model.Name == "m1" ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(60)) : MockStep.Ok("m2 输出"));
        var options = Options(taskPath, M("m1"), M("m2"));
        var (engine1, clock1, logger1, events1) = BuildEngine(options, adapter1, cooldownDir);

        var report1 = await engine1.StartAsync();
        logger1.Dispose();

        Assert.Equal(BatchStatus.Completed, report1.BatchStatus);
        Assert.All(ReadTasks(taskPath), t => Assert.Equal("m2", t.ModelUsed));
        Assert.Equal(1, adapter1.CallLog.Count(c => c.Contains("+m1 ")));

        // 断言 3：冷却落盘
        var cooldownPath = Path.Combine(cooldownDir, "cooldowns.json");
        Assert.True(File.Exists(cooldownPath), "cooldowns.json 未落盘");
        Assert.Contains("m1", await File.ReadAllTextAsync(cooldownPath));

        // 第二轮：任务文件替换为全新 pending 任务，时钟 +1 分钟（冷却中）
        await WriteJsonTasksAsync(taskPath, TaskItem("task-100"));
        var adapter2 = new MockAdapter((_, _) => MockStep.Ok("m2 输出"));
        var (engine2, clock2, logger2, events2) = BuildEngine(options, adapter2, cooldownDir,
            Noon.AddMinutes(1));

        var report2 = await engine2.StartAsync();
        logger2.Dispose();

        // 断言 2：m1 冷却期内零调用
        Assert.Equal(0, adapter2.CallLog.Count(c => c.Contains("+m1 ")));
        Assert.Equal(1, adapter2.TotalCalls);
        Assert.Equal(BatchStatus.Completed, report2.BatchStatus);
        Assert.Equal("m2", ReadTasks(taskPath).Single().ModelUsed);

        // 断言 4：重启加载冷却记录
        Assert.Contains(events2, e => e.Message.Contains("已加载 1 条额度冷却记录"));
    }

    // ---------------- TC-E2E-004 ----------------

    [Fact]
    public async Task TC_E2E_004_时段外启动等待_到点自动恢复完成()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await WriteJsonTasksAsync(taskPath, TaskItem("task-001"), TaskItem("task-002"));

        // 12:00 在 22:00→06:00 窗口之外
        var window = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var adapter = new MockAdapter((_, _) => MockStep.Ok("窗口内输出"));
        var options = Options(taskPath, M("m1", window));
        var (engine, clock, logger, events) = BuildEngine(options, adapter, Path.Combine(dir, "state"));

        var report = await engine.StartAsync(waitIfUnavailable: true);
        logger.Dispose();

        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchWaiting);
        Assert.Contains(events, e => e.Kind == BatchEventKind.BatchResumed);
        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        Assert.Equal(2, report.Done);
        Assert.True(report.WindowWaitSeconds > 0, "免费时段外累计等待应为正数");

        var saved = ReadTasks(taskPath);
        Assert.All(saved, t => Assert.Equal(TaskStatus.Done, t.Status));
    }

    // ---------------- TC-E2E-005 ----------------

    [Fact]
    public async Task TC_E2E_005_全组合额度耗尽_兜底模型收尾()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await WriteJsonTasksAsync(taskPath, TaskItem("task-001"));

        // m1 恒额度耗尽；兜底模型 fb 正常执行（兜底失败场景属引擎另测的边缘情况）
        var adapter = new MockAdapter((req, _) =>
            req.Model.Name == "m1" ? MockStep.Quota(resetIn: TimeSpan.FromMinutes(90)) : MockStep.Ok("兜底输出"));
        var options = Options(taskPath, M("m1"));
        options.Fallback = new FallbackConfig
        {
            Enabled = true,
            MaxCostPerTask = 1.0m,
            MaxCostPerBatch = 10.0m
        };
        // v1.12：兜底模型池挂在账号下
        options.Accounts[0].FallbackModels = new List<FallbackModel>
        {
            new() { Name = "fb", EstimatedUnitPrice = 0.5m }
        };
        var (engine, _, logger, events) = BuildEngine(options, adapter, Path.Combine(dir, "state"));

        var report = await engine.StartAsync();
        logger.Dispose();

        // 断言 1：兜底完成
        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        var task = ReadTasks(taskPath).Single();
        Assert.Equal(TaskStatus.Done, task.Status);
        Assert.True(task.FallbackUsed);
        Assert.Equal("fb", task.ModelUsed);
        Assert.Contains(events, e => e.Kind == BatchEventKind.FallbackUsed);

        // 断言 2：报告成本
        Assert.Equal(1, report.Fallback.UsedCount);
        Assert.Equal(0.5m, report.Fallback.TotalCost);

        // 断言 3：冷却先于兜底记录
        var cooldownPath = Path.Combine(dir, "state", "cooldowns.json");
        Assert.True(File.Exists(cooldownPath));
        Assert.Contains("m1", await File.ReadAllTextAsync(cooldownPath));
    }

    // ---------------- TC-E2E-006 ----------------

    [Fact]
    public async Task TC_E2E_006_危险操作拦截_任务失败批继续()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await WriteJsonTasksAsync(taskPath, TaskItem("task-001"), TaskItem("task-002"), TaskItem("task-003"));

        var adapter = new MockAdapter((req, _) =>
            req.TaskId == "task-002" ? MockStep.Dangerous("rm -rf /") : MockStep.Ok("正常输出"));
        var options = Options(taskPath, M("m1"));
        var (engine, _, logger, events) = BuildEngine(options, adapter, Path.Combine(dir, "state"));

        var report = await engine.StartAsync();
        logger.Dispose();
        var logText = await File.ReadAllTextAsync(logger.LogFilePath);

        // 断言 1：危险任务失败，其余完成
        Assert.Equal(BatchStatus.Completed, report.BatchStatus);
        var saved = ReadTasks(taskPath);
        Assert.Equal(TaskStatus.Done, saved.Single(t => t.Id == "task-001").Status);
        var failed = saved.Single(t => t.Id == "task-002");
        Assert.Equal(TaskStatus.Failed, failed.Status);
        Assert.Contains("危险操作", failed.Error);
        Assert.Equal(TaskStatus.Done, saved.Single(t => t.Id == "task-003").Status);

        // 断言 2：事件与批状态
        Assert.Contains(events, e => e.Kind == BatchEventKind.DangerousOperationBlocked);

        // 断言 3：运行日志高危标记
        Assert.Contains("*** 高危 ***", logText);
    }

    // ---------------- TC-E2E-007 ----------------

    [Fact]
    public async Task TC_E2E_007_预检拦截_错误清晰不抛异常()
    {
        var dir = NewDir();
        var adapter = new MockAdapter();

        // ① 任务文件不存在
        var options1 = Options(Path.Combine(dir, "不存在的任务.json"), M("m1"));
        var (engine1, _, logger1, _) = BuildEngine(options1, adapter, Path.Combine(dir, "s1"));
        var preflight1 = await engine1.PreflightAsync();
        logger1.Dispose();

        Assert.Equal(0, preflight1.LoadedTaskCount);
        Assert.Contains(preflight1.Errors, e => e.Contains("任务文件不存在"));

        // ② 任务文件有效但工作空间目录不存在
        var taskPath = Path.Combine(dir, "tasks.json");
        await WriteJsonTasksAsync(taskPath, TaskItem("task-001"));
        var options2 = Options(taskPath, M("m1"));
        options2.Workspaces = new List<WorkspaceConfig>
        {
            new()
            {
                Name = "坏工作区",
                Path = Path.Combine(dir, "missing-workspace"),
                IsGlobalDefault = true
            }
        };
        var (engine2, _, logger2, _) = BuildEngine(options2, adapter, Path.Combine(dir, "s2"));
        var preflight2 = await engine2.PreflightAsync();
        logger2.Dispose();

        Assert.Equal(1, preflight2.LoadedTaskCount);
        Assert.Contains(preflight2.Errors, e => e.Contains("坏工作区") && e.Contains("不存在"));
    }

    // ---------------- TC-E2E-008 ----------------

    [Fact]
    public async Task TC_E2E_008_配置全链路_注释模板加载往返无损()
    {
        var dir = NewDir();
        var templatePath = Path.Combine(RepoRoot(), "samples", "config.example.json");
        var configDir = Path.Combine(dir, "conf");
        Directory.CreateDirectory(configDir);
        File.Copy(templatePath, Path.Combine(configDir, "config.json"));

        var store = new JsonConfigStore(configDir);

        // 断言 1：注释模板可加载（v1.12：候选/兜底模型池挂在账号下）
        var loaded = await store.LoadAsync();
        Assert.True(loaded.Accounts.Count >= 2, "账号池应至少 2 个");
        var acc1 = loaded.Accounts.Single(a => a.Id == "acc-1");
        Assert.NotEmpty(acc1.ModelCandidates!);
        Assert.NotEmpty(acc1.FallbackModels!);
        Assert.NotNull(acc1.ModelCandidates![0].FreeTimeWindow);
        var acc2 = loaded.Accounts.Single(a => a.Id == "acc-2");
        Assert.Contains(acc2.ModelCandidates!, m => m.Name == "Hy4" && m.FreeTimeWindow is null);

        // 保存 → 读取落盘原文
        await store.SaveAsync(loaded);
        var raw = await File.ReadAllTextAsync(Path.Combine(store.ConfigDirectory, "config.json"));

        // 断言 2：落盘为合法 JSON、派生属性不入盘（按行检测注释，避免误伤默认 API 基址等 URL 中的 //）
        Assert.DoesNotContain(raw.Split('\n'), l => l.TrimStart().StartsWith("//"));
        Assert.DoesNotContain("\"task_timeout\"", raw);
        Assert.DoesNotContain("\"max_task_duration\"", raw);
        Assert.DoesNotContain("\"max_wait\"", raw);
        Assert.DoesNotContain("\"default_cooldown\"", raw);

        // 断言 3：重载往返无损（含各账号自有池）
        var reloaded = await store.LoadAsync();
        Assert.Equal(loaded.Accounts.Count, reloaded.Accounts.Count);
        Assert.Equal(
            loaded.Accounts.SelectMany(a => a.ModelCandidates ?? new List<ModelCandidate>()).Select(m => m.Name),
            reloaded.Accounts.SelectMany(a => a.ModelCandidates ?? new List<ModelCandidate>()).Select(m => m.Name));
        Assert.Equal(
            loaded.Accounts.SelectMany(a => a.FallbackModels ?? new List<FallbackModel>()).Select(m => m.Name),
            reloaded.Accounts.SelectMany(a => a.FallbackModels ?? new List<FallbackModel>()).Select(m => m.Name));
        Assert.Equal(loaded.DegradationStrategy, reloaded.DegradationStrategy);
        Assert.Equal(loaded.DefaultCooldownMinutes, reloaded.DefaultCooldownMinutes);
    }
}
