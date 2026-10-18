using System.Text.Json;
using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Infrastructure.CredentialVault;
using BatchAgentRunner.Infrastructure.Logging;
using BatchAgentRunner.Infrastructure.Persistence;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Tests.CredentialVault;

/// <summary>
/// MockAdapter 全链路联调（DESIGN §11）：登录模拟 → 备份 → 引擎预检/任务边界切换
/// → AuthExpired 停用 → 重登覆盖快照；并做安全检查（日志/任务文件 grep 不到凭证片段）。
/// </summary>
[Collection("credential-vault")]
public class MockVaultE2ETests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    private sealed record Env(FileCredentialVault Vault, string Dir, string VaultRoot, string LiveRoot,
        string LiveFile, FakeClock Clock);

    private static Env NewEnv()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bar-e2evault-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var vaultRoot = Path.Combine(dir, "vault");
        var liveRoot = Path.Combine(dir, "live");
        Directory.CreateDirectory(liveRoot);
        CredentialLocationRegistry.SetLiveRootOverride(AgentKind.Mock, liveRoot);
        var clock = new FakeClock(Noon);
        var vault = new FileCredentialVault(vaultRoot, new TestCredentialProtector(),
            new FakeCliProcessGuard(), clock);
        return new Env(vault, dir, vaultRoot, liveRoot, Path.Combine(liveRoot, ".credentials.json"), clock);
    }

    private static RunOptions Options(string taskPath) => new()
    {
        TaskFilePath = taskPath,
        AgentKind = AgentKind.Mock,
        Accounts = new List<AccountConfig>
        {
            new() { Id = "acc-a", DisplayName = "账号 A" },
            new() { Id = "acc-b", DisplayName = "账号 B" }
        },
        ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" } },
        MaxRetriesPerTask = 0,
        RateLimitBackoffRetries = 1,
        RateLimitInitialBackoffSeconds = 1,
        TaskTimeoutMinutes = 1,
        MaxWaitMinutes = 0
    };

    private static async Task WriteTasksAsync(string path, params string[] ids)
    {
        var tasks = ids.Select(id => new TaskItem { Id = id, Instruction = "仿真指令" }).ToList();
        await File.WriteAllTextAsync(path, JsonDefaults.Serialize(tasks));
    }

    private static (BatchEngine Engine, List<BatchEvent> Events, FileRunLogger Logger) BuildEngine(
        RunOptions options, MockAdapter adapter, Env env)
    {
        adapter.Clock = env.Clock;
        var logger = new FileRunLogger(options.TaskFilePath);
        var engine = new BatchEngine(options, adapter, new TaskFileStore(),
            new JsonCooldownStore(Path.Combine(env.Dir, "state")), logger, env.Clock,
            credentialVault: env.Vault);
        var events = new List<BatchEvent>();
        engine.EventRaised += (_, e) => events.Add(e);
        engine.InitializeAsync().Wait();
        return (engine, events, logger);
    }

    [Fact]
    public async Task 全链路_登录备份_任务边界切换_保活回写()
    {
        var env = NewEnv();
        // ① 登录模拟 → ② 按账号备份（acc-a 后 acc-b，模拟两次引导登录）
        await File.WriteAllTextAsync(env.LiveFile, "{\"access_token\":\"sk-A\",\"refresh_token\":\"rt-A0\"}");
        await env.Vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a", "账号 A", "chinese");
        await File.WriteAllTextAsync(env.LiveFile, "{\"access_token\":\"sk-B\",\"refresh_token\":\"rt-B0\"}");
        await env.Vault.BackupFromLiveAsync(AgentKind.Mock, "acc-b", "账号 B", "chinese");

        // acc-a 第 1 次成功、之后额度耗尽；acc-b 一直成功 → 逼出任务边界账号切换
        var callsByAccount = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var adapter = new MockAdapter((req, _) =>
        {
            var n = callsByAccount.AddOrUpdate(req.Account.Id, 1, (_, v) => v + 1);
            return req.Account.Id == "acc-a" && n > 1 ? MockStep.Quota() : MockStep.Ok("仿真输出");
        })
        {
            MockLiveCredentialPath = env.LiveFile
        };

        var taskPath = Path.Combine(env.Dir, "tasks.json");
        await WriteTasksAsync(taskPath, "task-001", "task-002");
        var options = Options(taskPath);
        var (engine, events, logger) = BuildEngine(options, adapter, env);

        var report = await engine.PreflightAsync();
        Assert.Empty(report.DisabledAccounts);
        var batch = await engine.StartAsync();
        logger.Dispose();

        // 批完成，两个任务都成功
        Assert.Equal(BatchStatus.Completed, batch.BatchStatus);
        Assert.Equal(2, batch.Done);

        // 任务边界发生凭证切换（账号 A 额度耗尽 → B），Live 最终是 B
        Assert.Contains(events, e => e.Kind == BatchEventKind.AccountSwitched && e.Message.Contains("任务边界"));
        var liveText = await File.ReadAllTextAsync(env.LiveFile);
        Assert.Contains("sk-B", liveText);
        Assert.DoesNotContain("sk-A", liveText);
        Assert.Equal("acc-b", env.Vault.CurrentLiveAccount(AgentKind.Mock));

        // 保活回写：acc-a 快照已吸收 CLI 运行期间轮换出的新 refresh token
        var aSnapshot = System.Text.Encoding.UTF8.GetString(new TestCredentialProtector().Unprotect(
            File.ReadAllBytes(Path.Combine(env.VaultRoot, "mock", "acc-a", "files", ".credentials.json"))));
        Assert.Contains("sk-A", aSnapshot);
        Assert.Contains("rt-1", aSnapshot); // MockAdapter 轮换出的 token 被回写进快照

        // 安全：运行日志 / 任务文件 / Vault 审计 grep 不到凭证片段（DESIGN §11）
        var logText = File.ReadAllText(logger.LogFilePath);
        Assert.DoesNotContain("sk-A", logText);
        Assert.DoesNotContain("sk-B", logText);
        var taskText = File.ReadAllText(taskPath);
        Assert.DoesNotContain("sk-A", taskText);
        Assert.DoesNotContain("sk-B", taskText);
        var auditText = File.ReadAllText(Path.Combine(env.VaultRoot, "audit.log"));
        Assert.DoesNotContain("sk-A", auditText);
        Assert.Contains("acc-a", auditText);
    }

    [Fact]
    public async Task 全链路_AuthExpired停用_重新引导登录覆盖快照后恢复()
    {
        var env = NewEnv();
        await File.WriteAllTextAsync(env.LiveFile, "{\"access_token\":\"sk-A\",\"refresh_token\":\"rt-A0\"}");
        await env.Vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a");
        await File.WriteAllTextAsync(env.LiveFile, "{\"access_token\":\"sk-B\",\"refresh_token\":\"rt-B0\"}");
        await env.Vault.BackupFromLiveAsync(AgentKind.Mock, "acc-b");

        // acc-a 第 1 次成功、之后额度耗尽；acc-b 第 1 次调用即凭据失效
        var callsByAccount = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var adapter = new MockAdapter((req, _) =>
        {
            var n = callsByAccount.AddOrUpdate(req.Account.Id, 1, (_, v) => v + 1);
            if (req.Account.Id == "acc-a" && n > 1)
            {
                return MockStep.Quota();
            }

            return req.Account.Id == "acc-b" ? MockStep.Auth("token expired") : MockStep.Ok();
        })
        {
            MockLiveCredentialPath = env.LiveFile
        };

        var taskPath = Path.Combine(env.Dir, "tasks.json");
        await WriteTasksAsync(taskPath, "task-001", "task-002");
        var (engine, events, logger) = BuildEngine(Options(taskPath), adapter, env);

        await engine.PreflightAsync();
        var batch = await engine.StartAsync();

        // acc-b 凭据失效 → 停用；acc-a 额度冷却 → 矩阵不可用 → 批暂停
        Assert.Equal(BatchStatus.Blocked, batch.BatchStatus);
        Assert.Contains(events, e => e.Message.Contains("凭据失效，已停用"));
        logger.Dispose();

        // 重新引导登录覆盖 acc-b 旧快照（Mock 启动器直接写入新凭证）
        var orchestrator = new GuidedLoginOrchestrator(env.Vault, new FakeCliProcessGuard(),
            new TestLauncher(() => File.WriteAllText(env.LiveFile,
                "{\"access_token\":\"sk-B2\",\"refresh_token\":\"rt-B2\"}")),
            env.Clock, null, TimeSpan.FromMilliseconds(5));
        await orchestrator.RunAsync(AgentKind.Mock, "acc-b", "账号 B");

        var bSnapshot = System.Text.Encoding.UTF8.GetString(new TestCredentialProtector().Unprotect(
            File.ReadAllBytes(Path.Combine(env.VaultRoot, "mock", "acc-b", "files", ".credentials.json"))));
        Assert.Contains("sk-B2", bSnapshot);
        Assert.True(env.Vault.SnapshotExists(AgentKind.Mock, "acc-b"));

        // 重新启用账号后预检通过，且 last_verified_at 更新（DESIGN §7：预检即验证快照有效性）
        engine.Options.Accounts.First(a => a.Id == "acc-b").Enabled = true;
        var preflight = await engine.PreflightAsync();
        Assert.DoesNotContain(preflight.DisabledAccounts, label => label.Contains('B'));
        Assert.NotNull(env.Vault.GetSnapshot(AgentKind.Mock, "acc-b")!.LastVerifiedAt);
    }

    private sealed class TestLauncher(Action onLaunch) : ICliLoginLauncher
    {
        public Task LaunchAsync(AgentKind cli, CancellationToken cancellationToken = default)
        {
            onLaunch();
            return Task.CompletedTask;
        }
    }
}
