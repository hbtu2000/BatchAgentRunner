using System.Text.Json.Nodes;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Tests.CredentialVault;

/// <summary>「全程零确认」保障测试：拉起登录前 settings.json 权限模式应被持久化为 bypassPermissions。</summary>
[Collection("credential-vault")]
public class CliPermissionSettingsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bar-perm-" + Guid.NewGuid().ToString("N")[..10]);

    /// <summary>P1-16：测试自持注册表实例（显式覆写 Live 根到临时目录），不再依赖静态全局态。</summary>
    private readonly CredentialLocationRegistry _registry = new();

    public CliPermissionSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _registry.SetLiveRootOverride(AgentKind.CodeBuddy, _root);
    }

    private string SettingsPath => Path.Combine(_root, "settings.json");

    [Fact]
    public void 无settings时_创建并写入bypass权限模式()
    {
        CliPermissionSettings.EnsureBypassPermissions(_registry, _registry.Resolve(AgentKind.CodeBuddy));

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("bypassPermissions", json["permissions"]?["defaultMode"]?.ToString());
    }

    [Fact]
    public void 已有settings时_保留其他字段只改权限模式()
    {
        File.WriteAllText(SettingsPath,
            """{"theme":"dark","permissions":{"defaultMode":"default","allow":["Bash(ls*)"]}}""");

        CliPermissionSettings.EnsureBypassPermissions(_registry, _registry.Resolve(AgentKind.CodeBuddy));

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("dark", json["theme"]?.ToString());
        Assert.Equal("bypassPermissions", json["permissions"]?["defaultMode"]?.ToString());
        Assert.Equal("Bash(ls*)", json["permissions"]?["allow"]?[0]?.ToString());
    }

    [Fact]
    public void settings损坏时_重建为可用的bypass配置()
    {
        File.WriteAllText(SettingsPath, "{not valid json");

        CliPermissionSettings.EnsureBypassPermissions(_registry, _registry.Resolve(AgentKind.CodeBuddy));

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("bypassPermissions", json["permissions"]?["defaultMode"]?.ToString());
    }

    public void Dispose()
    {
        // CA1816：SuppressFinalize 让派生类型引入终结器时无需重新实现 IDisposable
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
