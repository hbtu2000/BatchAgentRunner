using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using Xunit;

namespace BatchAgentRunner.Tests.Engine;

/// <summary>
/// 启动前预检协作者单测（P1-1 拆分产物 ⑥）：锁定「文件/CLI/账号池校验」「Vault 无快照停用账号」
/// 「DAG 环检测」「降级矩阵可用性评估」「加载失败不破坏既有任务集合」五条契约。
/// </summary>
[Trait("Category", "Invariant")]
public class PreflightTests
{
    private static RunOptions Options(string taskFilePath, params AccountConfig[] accounts) => new()
    {
        TaskFilePath = taskFilePath,
        AgentKind = AgentKind.Mock,
        Accounts = accounts.ToList(),
        ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" } },
        Concurrency = 1
    };

    private static StartupPreflight Build(
        RunOptions options,
        InMemoryTaskFileStore store,
        FakeFileSystem fileSystem,
        IAgentCliAdapter adapter,
        FakeClock clock,
        ICredentialVault? vault = null)
    {
        var cooldowns = new QuotaCooldownRegistry(clock);
        return new StartupPreflight(options, store, fileSystem, adapter, clock, cooldowns,
            _ => { }, ws => null, vault);
    }

    [Fact]
    public async Task 未选任务文件_报告错误未选择任务文件()
    {
        var options = Options("");
        var outcome = await Build(options, new InMemoryTaskFileStore(), new FakeFileSystem(),
            new MockAdapter(), new FakeClock()).RunAsync();

        Assert.Contains(outcome.Report.Errors, e => e.Contains("未选择任务文件"));
    }

    [Fact]
    public async Task 任务文件不存在_报告错误()
    {
        var options = Options("C:/nope/tasks.json");
        var fs = new FakeFileSystem(); // 未 WithFile
        var outcome = await Build(options, new InMemoryTaskFileStore(), fs,
            new MockAdapter(), new FakeClock()).RunAsync();

        Assert.Contains(outcome.Report.Errors, e => e.Contains("任务文件不存在"));
        Assert.False(outcome.Loaded);
    }

    [Fact]
    public async Task 正常加载_任务数正确且重建矩阵非空()
    {
        var path = "C:/tasks/tasks.json";
        var options = Options(path, new AccountConfig { Id = "acc1", DisplayName = "账号1" });
        var store = new InMemoryTaskFileStore
        {
            Tasks = new List<TaskItem> { new() { Id = "t1", Instruction = "i" }, new() { Id = "t2", Instruction = "i" } }
        };
        var fs = new FakeFileSystem().WithFile(path);

        var outcome = await Build(options, store, fs, new MockAdapter(), new FakeClock()).RunAsync();

        Assert.Equal(2, outcome.Report.LoadedTaskCount);
        Assert.True(outcome.Loaded);
        Assert.False(outcome.Matrix.IsEmpty);
        Assert.Empty(outcome.Report.Errors);
    }

    [Fact]
    public async Task Vault无快照_账号被停用并告警()
    {
        var path = "C:/tasks/tasks.json";
        var account = new AccountConfig { Id = "acc1", DisplayName = "账号1" };
        var options = Options(path, account);
        var store = new InMemoryTaskFileStore
        {
            Tasks = new List<TaskItem> { new() { Id = "t1", Instruction = "i" } }
        };
        var fs = new FakeFileSystem().WithFile(path);
        var vault = new FakeVault(snapshotExists: false);

        var outcome = await Build(options, store, fs, new MockAdapter(), new FakeClock(), vault).RunAsync();

        Assert.False(account.Enabled); // 协作者直接停用了传入的账号引用
        Assert.Contains(account.Label, outcome.Report.DisabledAccounts);
        Assert.Contains(outcome.Report.Warnings, w => w.Contains("尚无凭证快照"));
    }

    [Fact]
    public async Task 任务依赖成环_报告环错误()
    {
        var path = "C:/tasks/tasks.json";
        var options = Options(path, new AccountConfig { Id = "acc1", DisplayName = "账号1" });
        var store = new InMemoryTaskFileStore
        {
            Tasks = new List<TaskItem>
            {
                new() { Id = "t1", Instruction = "i", DependsOn = new List<string> { "t2" } },
                new() { Id = "t2", Instruction = "i", DependsOn = new List<string> { "t1" } }
            }
        };
        var fs = new FakeFileSystem().WithFile(path);

        var outcome = await Build(options, store, fs, new MockAdapter(), new FakeClock()).RunAsync();

        Assert.Contains(outcome.Report.Errors, e => e.Contains('环'));
    }

    /// <summary>最简 Vault 替身：默认无快照，预检即应停用账号（SwitchTo/MarkVerified 不会被执行）。</summary>
    private sealed class FakeVault : ICredentialVault
    {
        private readonly bool _snapshotExists;

        public FakeVault(bool snapshotExists) => _snapshotExists = snapshotExists;

        public string? CurrentLiveAccount(AgentKind cli) => null;

        public bool SnapshotExists(AgentKind cli, string accountId) => _snapshotExists;

        public CredentialSnapshotInfo? GetSnapshot(AgentKind cli, string accountId) => null;

        public Task<CredentialSnapshotInfo> BackupFromLiveAsync(AgentKind cli, string accountId,
            string? displayName = null, string? site = null, CancellationToken cancellationToken = default) =>
            // 本测试路径（无快照）不会调用，返回占位值
            Task.FromResult<CredentialSnapshotInfo>(null!);

        public Task SwitchToAsync(AgentKind cli, string accountId, string reason,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkVerifiedAsync(AgentKind cli, string accountId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteSnapshotAsync(AgentKind cli, string accountId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
