using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using BatchAgentRunner.Infrastructure.TaskFiles;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>
/// 拉起登录前的「全程零确认」保障：在 CLI 主目录 <c>settings.json</c> 中确保 <c>permissions.defaultMode</c> 为 <c>bypassPermissions</c>。
/// <para>
/// P1-12 治理要点（相对旧实现）：
/// ① <b>作用域收窄</b>——仅当 <c>defaultMode</c> <b>尚未被用户显式设置</b>时才写入 bypassPermissions，
///    不再无条件静默覆盖。该文件属 CodeBuddy CLI 自身（<c>~/.codebuddy/settings.json</c>），作用域不止本工具；
///    用户若已显式设值（含 bypass 或任何其它模式），一律保留其选择，避免越权改写其它工具的全局配置。
///    已为期望值时幂等不写，消除无谓覆盖与并发竞态。
/// ② <b>原子写</b>——落盘改走 <see cref="AtomicFileWriter"/>（临时文件 + <c>File.Move</c> 原子替换），
///    不再 <c>File.WriteAllText</c> 直接覆盖，强杀/断电不再损坏用户 CLI 配置。
/// </para>
/// </summary>
public static class CliPermissionSettings
{
    private const string BypassMode = "bypassPermissions";

    /// <summary>
    /// 确保 <c>settings.json</c> 的 <c>permissions.defaultMode</c> 为 <c>bypassPermissions</c>。
    /// 仅当 <c>defaultMode</c> 缺失/为空时才写入；已显式设置（含 bypass 或用户其它选择）则保留并幂等返回，不静默覆盖。
    /// </summary>
    /// <param name="registry">凭证位置注册表（P1-16：显式传入，替代原静态全局态）。</param>
    /// <param name="spec">CLI 位置规格。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task EnsureBypassPermissionsAsync(
        ICredentialLocationRegistry registry,
        CredentialLocationSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(spec);

        var settingsPath = Path.Combine(registry.ResolveLiveRoot(spec), "settings.json");

        JsonNode root = File.Exists(settingsPath)
            ? ParseOrRebuild(settingsPath)
            : new JsonObject();

        var permissions = root["permissions"] as JsonObject ?? new JsonObject();

        // 作用域收窄（P1-12）：仅当 defaultMode 未显式设置时写入；尊重用户在 ~/.codebuddy/settings.json 中的既有选择。
        var existingMode = permissions["defaultMode"]?.ToString();
        if (!string.IsNullOrEmpty(existingMode))
        {
            return; // 幂等：已设置（含 bypass 或用户显式值），不动用户配置，避免越权覆盖其它工具的全局配置
        }

        permissions["defaultMode"] = BypassMode;
        root["permissions"] = permissions;

        await AtomicFileWriter.WriteAsync(
            settingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
    }

    private static JsonNode ParseOrRebuild(string settingsPath)
    {
        try
        {
            // 既有 settings.json 损坏时重建（只影响权限默认值，不动凭证）
            return JsonNode.Parse(File.ReadAllText(settingsPath)) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
