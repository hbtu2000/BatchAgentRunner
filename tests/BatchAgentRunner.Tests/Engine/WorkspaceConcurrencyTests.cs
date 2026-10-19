using System.Collections.Concurrent;
using System.Text.Json;
using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Infrastructure.Logging;
using BatchAgentRunner.Infrastructure.Persistence;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Tests.Engine;

/// <summary>
/// 工作空间级并发 + 并行下统一账号切换的引擎级验证：
/// REQ-020（按工作空间串行 / 不同工作空间可并行，并行度受不同工作空间数约束）、
/// REQ-021（并行下在飞任务额度耗尽 → 冻结 + 派发暂停 + 原账号兜底收尾，全部在飞结束后统一切换，
/// 并产生 deferred_switch 埋点）。
/// 用例定义见 docs/CHANGE-REQ-019-021-工作空间上限与按空间串行并发及并行统一账号切换.md。
/// </summary>
[Trait("Category", "E2E")]
public class WorkspaceConcurrencyTests
{
    // 2026-10-15 是周四；12:00 落在任何免费时段之外，所有组合默认可用
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-ws-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static RunOptions Options(string taskPath, int concurrency, AccountConfig[] accounts,
        WorkspaceConfig[] workspaces)
    {
        return new RunOptions
        {
            TaskFilePath = taskPath,
            AgentKind = AgentKind.Mock,
            Accounts = accounts.ToList(),
            ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" } },
            Workspaces = workspaces.ToList(),
            DefaultWorkspace = workspaces.Length > 0 ? workspaces[0].Path : null,
            Concurrency = concurrency,
            MaxAttemptsPerTask = 20,
            MaxRetriesPerTask = 0,
            RateLimitBackoffRetries = 0,
            TaskTimeoutMinutes = 1,
            BackupRetention = 5,
            Fallback = new FallbackConfig { Enabled = false }
        };
    }

    private static TaskItem MakeTask(string id, string? workspace) => new()
    {
        Id = id,
        Instruction = "仿真指令",
        Workspace = workspace
    };

    private static async Task<string> WriteJsonTasksAsync(string path, params TaskItem[] tasks)
    {
        var json = JsonDefaults.Serialize(tasks.ToList());
        await File.WriteAllTextAsync(path, json);
        return json;
    }

    private static (BatchEngine Engine, List<BatchEvent> Events) Build(
        RunOptions options, IAgentCliAdapter adapter, ICredentialVault? vault, string cooldownDir)
    {
        var clock = new FakeClock(Noon);
        if (adapter is MockAdapter ma)
        {
            ma.Clock = clock;
        }

        var logger = new FileRunLogger(options.TaskFilePath);
        var engine = new BatchEngine(options, adapter, new TaskFileStore(), new JsonCooldownStore(cooldownDir),
            logger, new BatchEngineDependencies { Clock = clock, CredentialVault = vault });

        var events = new List<BatchEvent>();
        engine.EventRaised += (_, e) => events.Add(e);
        engine.InitializeAsync().Wait();
        return (engine, events);
    }

    // ---------------- REQ-020：按工作空间串行 ----------------

    [Fact]
    public async Task REQ020_同工作空间两任务在并发2下仍串行执行()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await WriteJsonTasksAsync(taskPath,
            MakeTask("t1", "ws-a"),
            MakeTask("t2", "ws-a")); // 同工作空间

        var workspaces = new[]
        {
            new WorkspaceConfig { Name = "ws-a", Path = dir, IsGlobalDefault = true }
        };
        var options = Options(taskPath, concurrency: 2, new[] { new AccountConfig { Id = "acc1", DisplayName = "账号1" } }, workspaces);
        var adapter = new ConcurrencyProbeAdapter(TimeSpan.FromMilliseconds(80));

        var (engine, _) = Build(options, adapter, null, Path.Combine(dir, "state"));
        await engine.StartAsync();

        // 同工作空间：任意时刻至多 1 个任务在飞 → 全局最大并发 = 1（即便配置并发=2）
        Assert.Equal(1, adapter.MaxConcurrent);
        Assert.All(engine.Tasks, t => Assert.Equal(TaskStatus.Done, t.Status));
    }

    [Fact]
    public async Task REQ020_不同工作空间两任务在并发2下并行执行()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await WriteJsonTasksAsync(taskPath,
            MakeTask("t1", "ws-a"),
            MakeTask("t2", "ws-b")); // 不同工作空间

        var workspaces = new[]
        {
            new WorkspaceConfig { Name = "ws-a", Path = dir },
            new WorkspaceConfig { Name = "ws-b", Path = dir, IsGlobalDefault = true }
        };
        var options = Options(taskPath, concurrency: 2, new[] { new AccountConfig { Id = "acc1", DisplayName = "账号1" } }, workspaces);
        var adapter = new ConcurrencyProbeAdapter(TimeSpan.FromMilliseconds(80));

        var (engine, _) = Build(options, adapter, null, Path.Combine(dir, "state"));
        await engine.StartAsync();

        // 不同工作空间：并行度上界 = min(2, 2) = 2，两任务同时执行 → 全局最大并发 = 2
        Assert.Equal(2, adapter.MaxConcurrent);
        Assert.All(engine.Tasks, t => Assert.Equal(TaskStatus.Done, t.Status));
    }

    // ---------------- REQ-021：并行下统一账号切换 ----------------

    [Fact]
    public async Task REQ021_并行下额度耗尽触发冻结_原账号兜底收尾_在飞全部结束后统一切换()
    {
        var dir = NewDir();
        var taskPath = Path.Combine(dir, "tasks.json");
        await WriteJsonTasksAsync(taskPath,
            MakeTask("t1", "ws-a"),
            MakeTask("t2", "ws-b")); // 两任务不同工作空间 → 并行在飞

        var workspaces = new[]
        {
            new WorkspaceConfig { Name = "ws-a", Path = dir },
            new WorkspaceConfig { Name = "ws-b", Path = dir, IsGlobalDefault = true }
        };
        // 两个账号，共享全局模型池 m1；acc1 首次组合，acc2 为切换目标
        var accounts = new[]
        {
            new AccountConfig { Id = "acc1", DisplayName = "账号1" },
            new AccountConfig { Id = "acc2", DisplayName = "账号2" }
        };
        var options = Options(taskPath, concurrency: 2, accounts, workspaces);
        // m1 在 acc1 下额度耗尽 → 矩阵下一步指向 acc2（不同账号）→ 触发延后切换
        var adapter = MockAdapter.QuotaExhaustedFor(new[] { "m1" });
        var vault = new RecordingVault();

        var (engine, events) = Build(options, adapter, vault, Path.Combine(dir, "state"));
        var report = await engine.StartAsync();

        // 断言 1：在飞任务触发了槽位冻结 + 切换延后事件
        Assert.Contains(events, e => e.Kind == BatchEventKind.SlotFrozen);
        Assert.Contains(events, e => e.Kind == BatchEventKind.AccountSwitchDeferred);

        // 断言 2：产生了「延后统一切换」的埋点（AccountSwitched 且 DeferredSwitch=true）
        Assert.Contains(events, e => e.Kind == BatchEventKind.AccountSwitched && e.DeferredSwitch);
        Assert.Contains(report.Timeline, t => t.DeferredSwitch);

        // 断言 3：统一切换确实执行到目标账号 acc2（reason = deferred-unified）
        Assert.Contains(vault.Switches, s => string.Equals(s.AccountId, "acc2", StringComparison.OrdinalIgnoreCase)
                                            && s.Reason == "deferred-unified");

        // 断言 4：原账号兜底池为空（Fallback 关闭）→ 冻结任务转入 waiting/blocked 而非切换 Live 凭证跨账号
        Assert.All(engine.Tasks, t => Assert.NotEqual(TaskStatus.Done, t.Status));
    }

    // ---------------- 测试替身 ----------------

    /// <summary>并发探针适配器：记录执行期间的全局最大并发任务数，用于验证「按工作空间串行」不变量。</summary>
    private sealed class ConcurrencyProbeAdapter : IAgentCliAdapter
    {
        private int _active;
        public int MaxConcurrent { get; private set; }
        private readonly TimeSpan _duration;

        public ConcurrencyProbeAdapter(TimeSpan duration) => _duration = duration;

        public AgentKind Kind => AgentKind.Mock;

        public Task<PreflightResult> ValidateEnvironmentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PreflightResult.Pass("仿真适配器可用"));

        public Task<PreflightResult> ValidateAccountAsync(AccountConfig account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PreflightResult.Pass($"账号 {account.Label} 可用"));

        public Task<IReadOnlyList<string>> ListModelsAsync(AccountConfig account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<ModelCatalogEntry>> ListModelCatalogAsync(AccountConfig account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelCatalogEntry>>(Array.Empty<ModelCatalogEntry>());

        public async Task<AgentExecutionResult> ExecuteAsync(AgentRequest request,
            CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _active);
            MaxConcurrent = Math.Max(MaxConcurrent, n);
            try
            {
                await Task.Delay(_duration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消时直接退出；并发计数在 finally 中回收
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }

            return AgentExecutionResult.Ok(request, "仿真输出", null);
        }
    }

    /// <summary>记录型凭证 Vault：捕获每次 SwitchToAsync 调用的账号与目标，用于验证统一切换目标。</summary>
    private sealed class RecordingVault : ICredentialVault
    {
        public List<(string AccountId, string Reason)> Switches { get; } = new();

        public string? CurrentLiveAccount(AgentKind cli) => null;

        public bool SnapshotExists(AgentKind cli, string accountId) => true;

        public CredentialSnapshotInfo? GetSnapshot(AgentKind cli, string accountId) => null;

        public Task<CredentialSnapshotInfo> BackupFromLiveAsync(AgentKind cli, string accountId,
            string? displayName = null, string? site = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<CredentialSnapshotInfo>(null!);

        public Task SwitchToAsync(AgentKind cli, string accountId, string reason,
            CancellationToken cancellationToken = default)
        {
            Switches.Add((accountId, reason));
            return Task.CompletedTask;
        }

        public Task MarkVerifiedAsync(AgentKind cli, string accountId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteSnapshotAsync(AgentKind cli, string accountId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
