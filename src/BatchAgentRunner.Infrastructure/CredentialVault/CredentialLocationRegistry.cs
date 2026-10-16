using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>单个凭证文件在 Live 侧的位置（相对 CLI 主目录）。</summary>
public sealed record CredentialLiveFile(string RelativePath, bool Required = true);

/// <summary>
/// 单个 CLI 的凭证位置注册表项（DESIGN-账号凭证切换-v1.0 §4.3）。
/// 路径全部来自注册表，不散落硬编码——M0 V-4a/V-4b/V-4e 实测后只改这里，不动流程代码。
/// </summary>
public sealed record CredentialLocationSpec(
    AgentKind Kind,
    /// <summary>Vault 子目录名。</summary>
    string CliId,
    /// <summary>CLI 主目录（相对用户主目录，如 ".codebuddy"）；settings.json 等伴生文件的基准。</summary>
    string HomeRelativeRoot,
    IReadOnlyList<CredentialLiveFile> Files,
    IReadOnlyList<string> ProcessNames,
    /// <summary>交互式登录参数；空字符串 = 拉起裸 CLI（未登录时 TUI 自动弹登录方式选择）；null = 登录命令待确认。</summary>
    string? LoginArguments,
    /// <summary>凭证文件的根目录（相对用户主目录）；null = 沿用 HomeRelativeRoot。
    /// CodeBuddy 的登录态不在 CLI 主目录而在共享数据区，故单列。</summary>
    string? FilesRoot = null)
{
    /// <summary>是否已具备可用的 Vault 配置（凭证文件路径已确认）。</summary>
    public bool IsConfigured => Files.Count > 0;
}

/// <summary>
/// Live 位置注册表（2026-10-15 M0 实测结论，DESIGN §2）：
/// - CodeBuddy CLI（v2.167.0 实测）：登录态在共享数据区
///   %USERPROFILE%\AppData\Local\CodeBuddyExtension\Data\Public\auth\Tencent-Cloud.coding-copilot.info
///   （源码 dist/codebuddy.js：FilePathService.sharedDataPath + "auth/{authentication.id}.info"）。
///   ~/.codebuddy/.credentials.json 不存在、删除它不影响登录态（V-4a 原预期作废）。
///   文件名派生自 product.json 的 authentication.id，CLI 大版本更新若改名只需改这里。
/// - WorkBuddy：同目录下发现 workbuddy-desktop.info（待 WorkBuddy 侧确认后登记）。
/// - Mock：假凭证文件，Live 主目录必须通过 <see cref="SetLiveRootOverride"/> 指定。
/// </summary>
public static class CredentialLocationRegistry
{
    private static readonly Dictionary<AgentKind, CredentialLocationSpec> Specs = new()
    {
        [AgentKind.CodeBuddy] = new CredentialLocationSpec(
            AgentKind.CodeBuddy, "codebuddy", ".codebuddy",
            new[] { new CredentialLiveFile("Tencent-Cloud.coding-copilot.info") },
            new[] { "codebuddy" },
            // 2026-10-15 实测（v2.167.0）：CLI 无 login/auth 子命令，且以 / 开头的启动参数
            // （如 "codebuddy /login"）【不会】被识别为斜杠命令，而是当提示词开 agent 会话
            // （-p /help 也只是让模型回答帮助文本——管道无 TTY 时斜杠命令/登录流程不触发）。
            // 正确姿势：拉起裸 CLI（空参数）。引导登录前编排器已删除 auth\.info 登录态文件，
            // 未登录状态下 TUI 弹登录方式选择/直接开浏览器 OAuth（17:04 日志实测 Start login → auth_success）；
            // 万一只停在空闲输入框，让用户在窗口里手输 /login 即可（交互式 TUI 内有效）。
            "",
            // 凭证文件根：AppData\Local\CodeBuddyExtension\Data\Public\auth（相对用户主目录）
            "AppData/Local/CodeBuddyExtension/Data/Public/auth"),

        [AgentKind.WorkBuddy] = new CredentialLocationSpec(
            AgentKind.WorkBuddy, "workbuddy", ".workbuddy",
            Array.Empty<CredentialLiveFile>(),
            new[] { "workbuddy", "WorkBuddy" },
            null),

        [AgentKind.Mock] = new CredentialLocationSpec(
            AgentKind.Mock, "mock", ".batchagentrunner-mock",
            new[] { new CredentialLiveFile(".credentials.json") },
            Array.Empty<string>(),
            null)
    };

    /// <summary>Live 主目录覆盖（Mock / 测试 / 非默认安装位置）。</summary>
    private static readonly Dictionary<AgentKind, string> LiveRootOverrides = new();

    public static void SetLiveRootOverride(AgentKind kind, string path)
    {
        LiveRootOverrides[kind] = path;
    }

    public static CredentialLocationSpec Resolve(AgentKind kind)
    {
        if (Specs.TryGetValue(kind, out var spec))
        {
            return spec;
        }

        throw new CredentialVaultException($"未注册 {kind} 的凭证位置，请在 CredentialLocationRegistry 中补充");
    }

    /// <summary>Live 主目录（覆盖优先，否则 = 用户主目录 + 注册表相对根）。</summary>
    public static string ResolveLiveRoot(CredentialLocationSpec spec)
    {
        if (LiveRootOverrides.TryGetValue(spec.Kind, out var root))
        {
            return root;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, spec.HomeRelativeRoot);
    }

    /// <summary>Live 侧某个凭证文件的绝对路径。
    /// 有 Root 覆盖且未单列 FilesRoot 时（Mock/测试）沿用覆盖根；否则 = 用户主目录 + (FilesRoot ?? HomeRelativeRoot)。</summary>
    public static string ResolveLiveFile(CredentialLocationSpec spec, string relativePath)
    {
        var root = spec.FilesRoot is null && LiveRootOverrides.TryGetValue(spec.Kind, out var overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                (spec.FilesRoot ?? spec.HomeRelativeRoot).Replace('/', Path.DirectorySeparatorChar));
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
