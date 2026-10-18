using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Tests.CredentialVault;

/// <summary>引导登录流程测试（DESIGN §5 / §11：清场→拉起→检测→校验→备份）。</summary>
[Collection("credential-vault")]
[Trait("Category", "Invariant")]
public class GuidedLoginFlowTests
{
    private static readonly DateTimeOffset T1 = At.Local(2026, 10, 15, 14, 0);

    /// <summary>测试用登录启动器：拉起时记录 Live 是否已被清场，并可选写入新凭证。</summary>
    private sealed class FakeLauncher(Action onLaunch, ICredentialLocationRegistry registry) : ICliLoginLauncher
    {
        public bool LiveWasClearedAtLaunch { get; private set; }
        public int LaunchCount { get; private set; }

        public Task LaunchAsync(AgentKind cli, CancellationToken cancellationToken = default)
        {
            LaunchCount++;
            LiveWasClearedAtLaunch = !File.Exists(Path.Combine(
                registry.ResolveLiveRoot(registry.Resolve(cli)),
                ".credentials.json"));
            onLaunch();
            return Task.CompletedTask;
        }
    }

    private sealed record Fixture(GuidedLoginOrchestrator Orchestrator, FileCredentialVault Vault,
        string VaultRoot, string LiveRoot, FakeLauncher Launcher, List<GuidedLoginProgress> Progress);

    private static Fixture NewFixture(Action<string> onLaunch, bool cliRunning = false)
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "bar-vault-" + Guid.NewGuid().ToString("N")[..10]);
        var liveRoot = Path.Combine(Path.GetTempPath(), "bar-live-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(vaultRoot);
        Directory.CreateDirectory(liveRoot);
        var registry = new CredentialLocationRegistry();
        registry.SetLiveRootOverride(AgentKind.Mock, liveRoot);
        var clock = new FakeClock(T1);
        var vault = new FileCredentialVault(vaultRoot, new TestCredentialProtector(),
            new FakeCliProcessGuard(), clock, registry: registry);
        var progress = new List<GuidedLoginProgress>();
        var launcher = new FakeLauncher(() => onLaunch(liveRoot), registry);
        var orchestrator = new GuidedLoginOrchestrator(vault, new FakeCliProcessGuard(cliRunning),
            launcher, clock, progress.Add, TimeSpan.FromMilliseconds(5), registry);
        return new Fixture(orchestrator, vault, vaultRoot, liveRoot, launcher, progress);
    }

    private static string LivePath(string liveRoot) => Path.Combine(liveRoot, ".credentials.json");

    [Fact]
    public async Task FullFlow_检测到新凭证后校验并按账号备份()
    {
        var f = NewFixture(liveRoot => File.WriteAllText(LivePath(liveRoot),
            "{\"access_token\":\"sk-new-login\",\"refresh_token\":\"rt-new\"}"));
        File.WriteAllText(LivePath(f.LiveRoot), "{\"access_token\":\"sk-old\"}"); // 旧登录态

        var info = await f.Orchestrator.RunAsync(AgentKind.Mock, "acc-a", "A 账号", "chinese",
            timeout: TimeSpan.FromSeconds(30),
            validator: (_, _) => Task.FromResult(PreflightResult.Pass("ok")));

        // 清场发生在拉起登录之前（V-4e：可重复起点）
        Assert.True(f.Launcher.LiveWasClearedAtLaunch);
        // 快照按账号落库，Live 保留副本（备份后 CLI 立即可用）
        Assert.True(f.Vault.SnapshotExists(AgentKind.Mock, "acc-a"));
        Assert.Equal("acc-a", f.Vault.CurrentLiveAccount(AgentKind.Mock));
        Assert.Contains("sk-new-login", File.ReadAllText(LivePath(f.LiveRoot)));
        Assert.Contains(f.Progress, p => p.Phase == GuidedLoginPhase.Clearing);
        Assert.Contains(f.Progress, p => p.Phase == GuidedLoginPhase.Waiting);
        Assert.Contains(f.Progress, p => p.Phase == GuidedLoginPhase.BackingUp);
        Assert.Contains(f.Progress, p => p.Phase == GuidedLoginPhase.Done);
        Assert.Equal("chinese", info.Site);
    }

    [Fact]
    public async Task FullFlow_凭证迟迟不出现则超时报错()
    {
        var f = NewFixture(_ => { });

        var ex = await Assert.ThrowsAsync<CredentialVaultException>(() =>
            f.Orchestrator.RunAsync(AgentKind.Mock, "acc-a", timeout: TimeSpan.FromSeconds(30)));
        Assert.Contains("超时", ex.Message);
        Assert.False(f.Vault.SnapshotExists(AgentKind.Mock, "acc-a"));
    }

    [Fact]
    public async Task FullFlow_校验失败时不落快照()
    {
        var f = NewFixture(liveRoot => File.WriteAllText(LivePath(liveRoot),
            "{\"access_token\":\"sk-bad\"}"));

        await Assert.ThrowsAsync<CredentialVaultException>(() =>
            f.Orchestrator.RunAsync(AgentKind.Mock, "acc-a",
                timeout: TimeSpan.FromSeconds(30),
                validator: (_, _) => Task.FromResult(PreflightResult.Fail("极简请求 401"))));

        Assert.False(f.Vault.SnapshotExists(AgentKind.Mock, "acc-a"));
    }

    [Fact]
    public async Task FullFlow_CLI进程运行中被拒绝且不拉起登录()
    {
        var launched = false;
        var f = NewFixture(_ => launched = true, cliRunning: true);

        var ex = await Assert.ThrowsAsync<CredentialVaultException>(() =>
            f.Orchestrator.RunAsync(AgentKind.Mock, "acc-a", timeout: TimeSpan.FromSeconds(30)));
        Assert.Contains("进程仍在运行", ex.Message);
        Assert.False(launched);
    }

    /// <summary>
    /// P0-9 不变量：登录未走完（超时 / 校验失败）时，清理掉的 Live 凭证必须原样还原，
    /// 不能出现「凭证被删且不可恢复」。
    /// </summary>
    [Fact]
    public async Task 登录超时_清理前的Live凭证被原样回滚()
    {
        var oldCredential = "{\"access_token\":\"sk-old\",\"refresh_token\":\"rt-old\"}";
        var f = NewFixture(_ => { }); // 登录无任何新凭证 → 超时
        await File.WriteAllTextAsync(LivePath(f.LiveRoot), oldCredential);

        await Assert.ThrowsAsync<CredentialVaultException>(() =>
            f.Orchestrator.RunAsync(AgentKind.Mock, "acc-a", timeout: TimeSpan.FromSeconds(30)));

        Assert.Equal(oldCredential, await File.ReadAllTextAsync(LivePath(f.LiveRoot)));
        Assert.False(f.Vault.SnapshotExists(AgentKind.Mock, "acc-a"));
        Assert.Contains(f.Progress, p => p.Phase == GuidedLoginPhase.Failed);
    }

    /// <summary>P0-9：登录成功后清掉暂存副本，凭证目录里不留第二份明确保留的凭证。</summary>
    [Fact]
    public async Task 登录成功_暂存凭证备份已清理()
    {
        var f = NewFixture(liveRoot => File.WriteAllText(LivePath(liveRoot),
            "{\"access_token\":\"sk-new-login\",\"refresh_token\":\"rt-new\"}"));
        await File.WriteAllTextAsync(LivePath(f.LiveRoot), "{\"access_token\":\"sk-old\"}");

        await f.Orchestrator.RunAsync(AgentKind.Mock, "acc-a", "A 账号",
            timeout: TimeSpan.FromSeconds(30),
            validator: (_, _) => Task.FromResult(PreflightResult.Pass("ok")));

        var staging = Path.Combine(f.LiveRoot, ".bar-guided-login-backup");
        Assert.False(Directory.Exists(staging), "登录成功后暂存凭证目录应已被清理");
        Assert.DoesNotContain(f.Progress, p => p.Phase == GuidedLoginPhase.Failed);
    }
}
