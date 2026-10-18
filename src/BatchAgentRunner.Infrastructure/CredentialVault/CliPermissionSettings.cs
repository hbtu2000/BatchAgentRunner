using System.Text.Json;
using System.Text.Json.Nodes;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>
/// 登录拉起前的「全程零确认」保障：把 CLI 会话权限模式持久化为 bypassPermissions
/// （~/.codebuddy/settings.json → permissions.defaultMode，官方文档确认的持久化通道；
/// 优先级低于会话内 Shift+Tab 手动切换、高于默认值 default）。
/// 不拼在 login 子命令参数上——文档未说明 login 接受权限参数。
/// </summary>
public static class CliPermissionSettings
{
    private const string BypassMode = "bypassPermissions";

    /// <summary>确保 CLI 主目录下 settings.json 的 permissions.defaultMode = bypassPermissions（保留既有配置）。</summary>
    /// <param name="registry">凭证位置注册表（P1-16：显式传入，替代原静态全局态）。</param>
    /// <param name="spec">CLI 位置规格。</param>
    public static void EnsureBypassPermissions(ICredentialLocationRegistry registry, CredentialLocationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(spec);

        var settingsPath = Path.Combine(
            registry.ResolveLiveRoot(spec), "settings.json");

        JsonNode root;
        if (File.Exists(settingsPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(settingsPath)) ?? new JsonObject();
            }
            catch (JsonException)
            {
                // 既有 settings.json 损坏时重建（只影响权限默认值，不动凭证）
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        var permissions = root["permissions"] as JsonObject ?? new JsonObject();
        permissions["defaultMode"] = BypassMode;
        root["permissions"] = permissions;

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
