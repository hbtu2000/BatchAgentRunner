using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.CredentialVault;
using Xunit;

namespace BatchAgentRunner.Tests.CredentialVault;

/// <summary>
/// P1-11 不变量测试：Vault 根目录 ACL 加固（收 ACL）+ manifest 原子写（统一原子写）。
/// 复用 <see cref="VaultSnapshotTests"/> 的 <c>NewVault</c> 自持实例构造（P1-16 后无静态全局态）。
/// </summary>
[Collection("credential-vault")]
public class VaultAclTests
{
    private static string LivePath(string liveRoot) =>
        Path.Combine(liveRoot, ".credentials.json");

    private static void WriteLive(string liveRoot, string content)
    {
        Directory.CreateDirectory(liveRoot);
        File.WriteAllText(LivePath(liveRoot), content);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Backup_后Vault根目录_移除继承ACL且当前用户拥有完全控制()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // ACL 加固仅 Windows（凭证功能本身仅 Windows，见 DpapiCredentialProtector）
        }

        var (vault, vaultRoot, liveRoot, _) = VaultSnapshotTests.NewVault();
        WriteLive(liveRoot, "{\"refresh_token\":\"rt-1\"}");
        await vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a");

        var dirInfo = new DirectoryInfo(vaultRoot);
        var acl = dirInfo.GetAccessControl();

        // ① 不再继承父目录的默认 ACL（P1-11 之「Vault 目录继承默认 ACL」已收口）
        Assert.True(acl.AreAccessRulesProtected,
            "Vault 根目录应移除继承的默认 ACL（SetAccessRuleProtection）");

        // ② 当前用户被显式授予完全控制（带继承，子目录/快照/清单自动继承）
        var userSid = WindowsIdentity.GetCurrent().User!;
        var grantsCurrentUser = acl.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference is SecurityIdentifier sid
                      && sid == userSid
                      && r.AccessControlType == AccessControlType.Allow
                      && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.True(grantsCurrentUser, "当前用户应对 Vault 根目录拥有显式完全控制");
    }

    [Fact]
    public async Task WriteManifest_原子写_账号目录内不残留临时文件()
    {
        var (vault, vaultRoot, liveRoot, _) = VaultSnapshotTests.NewVault();
        WriteLive(liveRoot, "{\"refresh_token\":\"rt-1\"}");
        await vault.BackupFromLiveAsync(AgentKind.Mock, "acc-a");

        // 多次写 manifest（重复备份覆盖 + 标记验证）不应留下任何 .tmp- 残留
        await vault.MarkVerifiedAsync(AgentKind.Mock, "acc-a");

        var accountDir = Path.Combine(vaultRoot, "mock", "acc-a");
        var residues = Directory.GetFiles(accountDir, "manifest.json.tmp-*");
        Assert.Empty(residues);
        Assert.True(File.Exists(Path.Combine(accountDir, "manifest.json")),
            "原子写后 manifest.json 必须存在");
    }
}
