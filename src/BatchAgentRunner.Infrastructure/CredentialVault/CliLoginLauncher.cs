using System.Diagnostics;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>拉起 CLI 交互式登录（浏览器 OAuth）的端口；立即返回，不等待登录完成。</summary>
public interface ICliLoginLauncher
{
    Task LaunchAsync(AgentKind cli, CancellationToken cancellationToken = default);
}

/// <summary>真实实现：以注册表中的 LoginArguments 拉起 CLI 登录（空参数 = 拉起裸 CLI，未登录时 TUI 自动弹登录方式选择）。</summary>
public sealed class CliLoginLauncher : ICliLoginLauncher
{
    private readonly ICredentialLocationRegistry _registry;

    public CliLoginLauncher(ICredentialLocationRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task LaunchAsync(AgentKind cli, CancellationToken cancellationToken = default)
    {
        var spec = _registry.Resolve(cli);
        if (spec.LoginArguments is null)
        {
            throw new CredentialVaultException(
                $"{spec.CliId} 的交互式登录命令尚未确认（DESIGN-账号凭证切换 V-4b/V-4e 待实测），" +
                "请在 CredentialLocationRegistry 登记后使用");
        }

        var exe = FindOnPath(spec.CliId);
        if (exe is null)
        {
            throw new CredentialVaultException(
                $"未找到 {spec.CliId} CLI，无法拉起登录；请先安装或在配置中指定 CLI 路径");
        }

        // 全程零确认：拉起前把权限模式持久化为 bypassPermissions（否则登录会话内工具调用会卡在审批框）
        CliPermissionSettings.EnsureBypassPermissions(_registry, spec);

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = spec.LoginArguments,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    private static string? FindOnPath(string name)
    {
        // Windows 上 npm 全局包会同时生成无扩展名 sh 脚本与 .cmd shim；
        // 无扩展名脚本 Process.Start 无法执行（"not a valid application for this OS platform"），
        // 必须优先命中 .exe/.cmd/.bat，无扩展名仅作 Unix 兜底。
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in new[] { ".exe", ".cmd", ".bat", string.Empty })
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
