using System.Text;
using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Tests.CredentialVault;

/// <summary>切换流程测试（DESIGN §11：保活回写 / 回滚 / 互斥 / 站点校验）。</summary>
[Collection("credential-vault")]
public class VaultSwitchTests
{
    private static readonly DateTimeOffset T1 = At.Local(2026, 10, 15, 14, 0);

    private sealed record Fixture(
        FileCredentialVault Vault, string VaultRoot, string LiveRoot, FakeClock Clock, List<string> Audit);

    private static Fixture NewVault(bool cliRunning = false)
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "bar-vault-" + Guid.NewGuid().ToString("N")[..10]);
        var liveRoot = Path.Combine(Path.GetTempPath(), "bar-live-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(vaultRoot);
        Directory.CreateDirectory(liveRoot);
        CredentialLocationRegistry.SetLiveRootOverride(AgentKind.Mock, liveRoot);
        var audit = new List<string>();
        var clock = new FakeClock(T1);
        var vault = new FileCredentialVault(vaultRoot, new TestCredentialProtector(),
            new FakeCliProcessGuard(cliRunning), clock, audit.Add);
        return new Fixture(vault, vaultRoot, liveRoot, clock, audit);
    }

    private static string LivePath(string liveRoot) => Path.Combine(liveRoot, ".credentials.json");

    private static async Task<Fixture> SetupTwoAccounts()
    {
        var f = NewVault();
        File.WriteAllText(LivePath(f.LiveRoot), "{\"access_token\":\"sk-A\",\"refresh_token\":\"rt-A1\"}");
        await f.Vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a", site: "chinese");
        File.WriteAllText(LivePath(f.LiveRoot), "{\"access_token\":\"sk-B\",\"refresh_token\":\"rt-B1\"}");
        await f.Vault.BackupFromLiveAsync(AgentKind.Mock, "acc-b", site: "chinese");
        return f;
    }

    private static string SnapshotPlain(Fixture f, string accountId) =>
        Encoding.UTF8.GetString(new TestCredentialProtector().Unprotect(
            File.ReadAllBytes(Path.Combine(f.VaultRoot, "mock", accountId, "files", ".credentials.json"))));

    [Fact]
    public async Task Switch_把目标快照原子回填为Live()
    {
        var f = await SetupTwoAccounts();
        Assert.Equal("acc-b", f.Vault.CurrentLiveAccount(AgentKind.Mock));

        await f.Vault.SwitchToAsync(AgentKind.Mock, "acc-a", "manual");

        var live = File.ReadAllText(LivePath(f.LiveRoot));
        Assert.Contains("sk-A", live);
        Assert.DoesNotContain("sk-B", live);
        Assert.Equal("acc-a", f.Vault.CurrentLiveAccount(AgentKind.Mock));
    }

    [Fact]
    public async Task Switch_Live被CLI刷新过时先保活回写()
    {
        var f = await SetupTwoAccounts();
        await f.Vault.SwitchToAsync(AgentKind.Mock, "acc-a", "manual");

        // 模拟 CLI 在 acc-a 使用期间轮换 refresh token：Live 哈希 ≠ acc-a 快照记录
        File.WriteAllText(LivePath(f.LiveRoot), "{\"access_token\":\"sk-A\",\"refresh_token\":\"rt-A2-rotated\"}");

        await f.Vault.SwitchToAsync(AgentKind.Mock, "acc-b", "manual");

        // acc-a 的快照已回写为最新 token（DESIGN §6 ②），且 manifest 哈希同步更新
        Assert.Contains("rt-A2-rotated", SnapshotPlain(f, "acc-a"));
        var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(f.VaultRoot, "mock", "acc-a", "manifest.json")));
        var hash = manifest.RootElement.GetProperty("files")[0].GetProperty("sha256").GetString()!;
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("{\"access_token\":\"sk-A\",\"refresh_token\":\"rt-A2-rotated\"}"))).ToLowerInvariant();
        Assert.Equal(expected, hash);

        // Live 已是 acc-b
        Assert.Contains("sk-B", File.ReadAllText(LivePath(f.LiveRoot)));
    }

    [Fact]
    public async Task Switch_Live未变化时不做无意义回写()
    {
        var f = await SetupTwoAccounts();
        var aBackupAt = f.Vault.GetSnapshot(AgentKind.Mock, "acc-a")!.BackedUpAt;

        await f.Vault.SwitchToAsync(AgentKind.Mock, "acc-a", "manual"); // 切走（无回写对象）
        await f.Vault.SwitchToAsync(AgentKind.Mock, "acc-b", "manual"); // Live 未变 → 不回写 acc-a

        Assert.Equal(aBackupAt, f.Vault.GetSnapshot(AgentKind.Mock, "acc-a")!.BackedUpAt);
    }

    [Fact]
    public async Task Switch_回填失败自动回滚Live()
    {
        var f = await SetupTwoAccounts();
        await f.Vault.SwitchToAsync(AgentKind.Mock, "acc-a", "manual");
        var liveBefore = File.ReadAllText(LivePath(f.LiveRoot));

        // 破坏 acc-b 快照的密文，使解密回填在中途失败 → 引发回滚
        var cipher = Path.Combine(f.VaultRoot, "mock", "acc-b", "files", ".credentials.json");
        File.WriteAllBytes(cipher, new byte[] { 1, 2, 3, 4 });

        var ex = await Assert.ThrowsAsync<CredentialVaultException>(() =>
            f.Vault.SwitchToAsync(AgentKind.Mock, "acc-b", "manual"));
        Assert.Contains("回滚", ex.Message);

        Assert.Equal(liveBefore, File.ReadAllText(LivePath(f.LiveRoot)));
        Assert.Equal("acc-a", f.Vault.CurrentLiveAccount(AgentKind.Mock));
    }

    [Fact]
    public async Task Switch_目标快照缺失报错并指引引导登录()
    {
        var f = await SetupTwoAccounts();

        var ex = await Assert.ThrowsAsync<CredentialVaultException>(() =>
            f.Vault.SwitchToAsync(AgentKind.Mock, "acc-missing", "manual"));
        Assert.Contains("没有凭证快照", ex.Message);
        Assert.Contains("引导登录", ex.Message);
    }

    [Fact]
    public async Task Switch_CLI进程运行中被拒绝()
    {
        var f = await SetupTwoAccounts();
        f.Vault.GetType(); // no-op
        var guard = new FakeCliProcessGuard(true);
        var vault2 = new FileCredentialVault(f.VaultRoot, new TestCredentialProtector(), guard, f.Clock);
        CredentialLocationRegistry.SetLiveRootOverride(AgentKind.Mock, f.LiveRoot);

        var ex = await Assert.ThrowsAsync<CredentialVaultException>(() =>
            vault2.SwitchToAsync(AgentKind.Mock, "acc-a", "manual"));
        Assert.Contains("进程仍在运行", ex.Message);
    }

    [Fact]
    public async Task Switch_站点不一致写入审计警告()
    {
        var f = await SetupTwoAccounts();
        await f.Vault.SwitchToAsync(AgentKind.Mock, "acc-a", "manual");
        f.Audit.Clear();

        // 模拟 acc-b 是国际站账号：直接改其 manifest 站点
        var manifestPath = Path.Combine(f.VaultRoot, "mock", "acc-b", "manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"site\": \"chinese\"", "\"site\": \"intl\""));

        await f.Vault.SwitchToAsync(AgentKind.Mock, "acc-b", "manual");

        Assert.Contains(f.Audit, line => line.Contains("WARN site-mismatch") && line.Contains("chinese") && line.Contains("intl"));
    }

    [Fact]
    public async Task MarkVerified_更新最近验证时间()
    {
        var f = await SetupTwoAccounts();
        f.Clock.Advance(TimeSpan.FromMinutes(5));

        await f.Vault.MarkVerifiedAsync(AgentKind.Mock, "acc-b");

        Assert.Equal(f.Clock.Now, f.Vault.GetSnapshot(AgentKind.Mock, "acc-b")!.LastVerifiedAt);
    }

    [Fact]
    public async Task DeleteSnapshot_删除目录且不影响其他账号()
    {
        var f = await SetupTwoAccounts();

        await f.Vault.DeleteSnapshotAsync(AgentKind.Mock, "acc-b");

        Assert.False(f.Vault.SnapshotExists(AgentKind.Mock, "acc-b"));
        Assert.True(f.Vault.SnapshotExists(AgentKind.Mock, "acc-a"));
    }
}
