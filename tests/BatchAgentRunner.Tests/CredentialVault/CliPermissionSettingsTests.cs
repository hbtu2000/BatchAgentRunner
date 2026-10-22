using System.Text.Json.Nodes;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Tests.CredentialVault;

/// <summary>「全程零确认」保障测试（P1-12 收口）：作用域收窄（不静默覆盖用户既有 defaultMode）+ 原子写。</summary>
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
    public async Task 无settings时_创建并写入bypass权限模式()
    {
        await CliPermissionSettings.EnsureBypassPermissionsAsync(
            _registry, _registry.Resolve(AgentKind.CodeBuddy));

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("bypassPermissions", json["permissions"]?["defaultMode"]?.ToString());
    }

    [Fact]
    public async Task 用户已显式设defaultMode时_不静默覆盖保留其选择()
    {
        File.WriteAllText(SettingsPath,
            """{"theme":"dark","permissions":{"defaultMode":"default","allow":["Bash(ls*)"]}}""");

        await CliPermissionSettings.EnsureBypassPermissionsAsync(
            _registry, _registry.Resolve(AgentKind.CodeBuddy));

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        // P1-12 作用域收窄：不得把用户显式设的 "default" 改成 "bypassPermissions"
        Assert.Equal("default", json["permissions"]?["defaultMode"]?.ToString());
        Assert.Equal("dark", json["theme"]?.ToString());
        Assert.Equal("Bash(ls*)", json["permissions"]?["allow"]?[0]?.ToString());
    }

    [Fact]
    public async Task 已为bypass时_幂等不重复写_保留其它字段()
    {
        File.WriteAllText(SettingsPath,
            """{"theme":"dark","permissions":{"defaultMode":"bypassPermissions","allow":["Bash(ls*)"]}}""");
        var before = File.ReadAllText(SettingsPath);

        await CliPermissionSettings.EnsureBypassPermissionsAsync(
            _registry, _registry.Resolve(AgentKind.CodeBuddy));

        // 幂等：内容不变（未触发覆盖写），且不破坏既有字段
        Assert.Equal(before, File.ReadAllText(SettingsPath));
        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("bypassPermissions", json["permissions"]?["defaultMode"]?.ToString());
        Assert.Equal("Bash(ls*)", json["permissions"]?["allow"]?[0]?.ToString());
    }

    [Fact]
    public async Task Settings损坏时_重建为可用的bypass配置()
    {
        File.WriteAllText(SettingsPath, "{not valid json");

        await CliPermissionSettings.EnsureBypassPermissionsAsync(
            _registry, _registry.Resolve(AgentKind.CodeBuddy));

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("bypassPermissions", json["permissions"]?["defaultMode"]?.ToString());
    }

    [Fact]
    public async Task 原子写_目录内不残留临时文件()
    {
        await CliPermissionSettings.EnsureBypassPermissionsAsync(
            _registry, _registry.Resolve(AgentKind.CodeBuddy));

        // P1-12 原子写：落盘走 AtomicFileWriter，目标存在且同目录无 *.tmp-* 残留
        Assert.True(File.Exists(SettingsPath));
        var stray = Directory.GetFiles(_root, "*.tmp-*", SearchOption.AllDirectories);
        Assert.Empty(stray);
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
