using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Tests.CredentialVault;

/// <summary>引导登录流程测试（DESIGN §5 / §11：清场→拉起→检测→校验→备份）。</summary>
[Collection("credential-vault")]
public class GuidedLoginFlowTests
{
    private static readonly DateTimeOffset T1 = At.Local(2026, 10, 15, 14, 0);

    /// <summary>测试用登录启动器：拉起时记录 Live 是否已被清场，并可选写入新凭证。</summary>
    private sealed class FakeLauncher(Action onLaunch) : ICliLoginLauncher
    {
        public bool LiveWasClearedAtLaunch { get; private set; }
        public int LaunchCount { get; private set; }

        public Task LaunchAsync(AgentKind cli, CancellationToken cancellationToken = default)
        {
            LaunchCount++;
            LiveWasClearedAtLaunch = !File.Exists(Path.Combine(
                CredentialLocationRegistry.ResolveLiveRoot(CredentialLocationRegistry.Resolve(cli)),
                ".credentials.json"));
            onLaunch();
            return Task.CompletedTask;
        }
    }

    private sealed record Fixture(GuidedLoginOrchestrator Orchestrator, FileCredentialVault Vault,
        string VaultRoot, string LiveRoot, FakeLauncher Launcher, List<GuidedLoginProgress> Progress);

    private Fixture NewFixture(Action<string> onLaunch, bool cliRunning = false)
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "bar-vault-" + Guid.NewGuid().ToString("N")[..10]);
        var liveRoot = Path.Combine(Path.GetTempPath(), "bar-live-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(vaultRoot);
        Directory.CreateDirectory(liveRoot);
        CredentialLocationRegistry.SetLiveRootOverride(AgentKind.Mock, liveRoot);
        var clock = new FakeClock(T1);
        var vault = new FileCredentialVault(vaultRoot, new TestCredentialProtector(),
            new FakeCliProcessGuard(), clock);
        var progress = new List<GuidedLoginProgress>();
        var launcher = new FakeLauncher(() => onLaunch(liveRoot));
        var orchestrator = new GuidedLoginOrchestrator(vault, new FakeCliProcessGuard(cliRunning),
            launcher, clock, progress.Add, TimeSpan.FromMilliseconds(5));
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
}
