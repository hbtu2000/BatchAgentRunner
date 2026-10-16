using System.Security.Cryptography;
using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.CredentialVault;
using Xunit;

namespace BatchAgentRunner.Tests.CredentialVault;

/// <summary>
/// Vault 单元测试（DESIGN-账号凭证切换-v1.0 §11 测试要点）。
/// 所有用例共用静态的 CredentialLocationRegistry（Live 根覆盖），串行执行避免相互覆盖。
/// </summary>
[Collection("credential-vault")]
public class VaultSnapshotTests
{
    private static readonly DateTimeOffset T1 = At.Local(2026, 10, 15, 14, 0);

    private static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static (FileCredentialVault Vault, string VaultRoot, string LiveRoot, FakeClock Clock) NewVault(
        bool cliRunning = false, FakeClock? clock = null)
    {
        var vaultRoot = NewDir("bar-vault");
        var liveRoot = NewDir("bar-live");
        CredentialLocationRegistry.SetLiveRootOverride(AgentKind.Mock, liveRoot);
        var fakeClock = clock ?? new FakeClock(T1);
        var vault = new FileCredentialVault(vaultRoot, new TestCredentialProtector(),
            new FakeCliProcessGuard(cliRunning), fakeClock);
        return (vault, vaultRoot, liveRoot, fakeClock);
    }

    internal static string LivePath(string liveRoot) =>
        Path.Combine(liveRoot, ".credentials.json");

    internal static void WriteLive(string liveRoot, string content)
    {
        Directory.CreateDirectory(liveRoot);
        File.WriteAllText(LivePath(liveRoot), content);
    }

    internal static string SnapshotPlain(string vaultRoot, string accountId)
    {
        var path = Path.Combine(vaultRoot, "mock", accountId, "files", ".credentials.json");
        return new TestCredentialProtector().Unprotect(File.ReadAllBytes(path)) is { } bytes
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : throw new InvalidOperationException();
    }

    internal static string ManifestPath(string vaultRoot, string accountId) =>
        Path.Combine(vaultRoot, "mock", accountId, "manifest.json");

    // ---------------- 快照备份 ----------------

    [Fact]
    public async Task Backup_生成加密快照与清单哈希()
    {
        var (vault, vaultRoot, liveRoot, clock) = NewVault();
        WriteLive(liveRoot, "{\"access_token\":\"sk-live-secret-AAA\",\"refresh_token\":\"rt-1\"}");

        var info = await vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a", "A 账号", "chinese");

        // 快照存在、归属记账正确
        Assert.True(vault.SnapshotExists(AgentKind.Mock, "acc-a"));
        Assert.Equal("acc-a", vault.CurrentLiveAccount(AgentKind.Mock));

        // manifest 契约（snake_case 字段 + 明文哈希）
        var manifest = JsonDocument.Parse(File.ReadAllText(ManifestPath(vaultRoot, "acc-a")));
        var files = manifest.RootElement.GetProperty("files");
        Assert.Equal(1, files.GetArrayLength());
        var first = files[0];
        var expectedHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                "{\"access_token\":\"sk-live-secret-AAA\",\"refresh_token\":\"rt-1\"}"))).ToLowerInvariant();
        Assert.Equal(expectedHash, first.GetProperty("sha256").GetString());
        Assert.Equal("chinese", manifest.RootElement.GetProperty("site").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("schema_version").GetInt32());

        // 快照文件静态存储不是明文（加密落盘，DESIGN §8.1）
        var snapshotPath = Path.Combine(vaultRoot, "mock", "acc-a", "files", ".credentials.json");
        var cipherBytes = File.ReadAllBytes(snapshotPath);
        Assert.NotEqual("{\"access_token\":\"sk-live-secret-AAA\",\"refresh_token\":\"rt-1\"}",
            System.Text.Encoding.UTF8.GetString(cipherBytes));
        Assert.Equal("{\"access_token\":\"sk-live-secret-AAA\",\"refresh_token\":\"rt-1\"}",
            SnapshotPlain(vaultRoot, "acc-a"));

        // 备份的是副本：Live 保留原文件
        Assert.Contains("sk-live-secret-AAA", File.ReadAllText(LivePath(liveRoot)));
        Assert.Equal(clock.Now, info.BackedUpAt);
    }

    [Fact]
    public async Task Backup_重复备份覆盖并保留验证时间()
    {
        var clock = new FakeClock(T1);
        var (vault, vaultRoot, liveRoot, _) = NewVault(clock: clock);
        WriteLive(liveRoot, "{\"refresh_token\":\"rt-1\"}");

        await vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a");
        await vault.MarkVerifiedAsync(AgentKind.Mock, "acc-a");
        var verifiedAt = vault.GetSnapshot(AgentKind.Mock, "acc-a")!.LastVerifiedAt;

        clock.Advance(TimeSpan.FromMinutes(30));
        WriteLive(liveRoot, "{\"refresh_token\":\"rt-2\"}");
        var again = await vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a");

        Assert.Equal(clock.Now, again.BackedUpAt);
        Assert.Equal(verifiedAt, again.LastVerifiedAt);
        Assert.Contains("rt-2", SnapshotPlain(vaultRoot, "acc-a"));
    }

    [Fact]
    public async Task Backup_无Live凭证时报错并指引引导登录()
    {
        var (vault, _, liveRoot, _) = NewVault();

        var ex = await Assert.ThrowsAsync<CredentialVaultException>(() =>
            vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a"));
        Assert.Contains("未发现", ex.Message);
    }

    [Fact]
    public async Task Backup_CLI进程运行中被拒绝()
    {
        var (vault, _, liveRoot, _) = NewVault(cliRunning: true);
        WriteLive(liveRoot, "{\"refresh_token\":\"rt-1\"}");

        var ex = await Assert.ThrowsAsync<CredentialVaultException>(() =>
            vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a"));
        Assert.Contains("进程仍在运行", ex.Message);
    }

    [Fact]
    public async Task Backup_账号ID含非法字符被拒绝()
    {
        var (vault, _, liveRoot, _) = NewVault();
        WriteLive(liveRoot, "{\"refresh_token\":\"rt-1\"}");

        await Assert.ThrowsAsync<CredentialVaultException>(() =>
            vault.BackupFromLiveAsync(AgentKind.Mock, ".."));
        await Assert.ThrowsAsync<CredentialVaultException>(() =>
            vault.BackupFromLiveAsync(AgentKind.Mock, "a/b"));
    }

    [Fact]
    public void Dpapi保护器_按当前用户加解密往返()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI 仅 Windows（DESIGN §2 侦查环境）
        }

        var protector = new DpapiCredentialProtector();
        var plaintext = System.Text.Encoding.UTF8.GetBytes("sk-dpapi-roundtrip");
        var cipher = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, cipher);
        Assert.Equal(plaintext, protector.Unprotect(cipher));
    }
}
