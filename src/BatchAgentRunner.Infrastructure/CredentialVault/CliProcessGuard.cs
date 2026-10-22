using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>
/// CLI 进程互斥守卫（DESIGN §6 ① / §9）：按注册表中的进程名探测该 CLI 是否有运行中的进程。
/// 探测本身失败（权限等）不视为「在运行」，避免误阻塞；真正的文件级冲突由原子覆盖兜底。
/// </summary>
public sealed class CliProcessGuard : ICliProcessGuard
{
    private readonly ICredentialLocationRegistry _registry;

    /// <summary>以凭证位置注册表构造，用于取该 CLI 的进程名。</summary>
    public CliProcessGuard(ICredentialLocationRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>探测该 CLI 是否有运行中的进程；探测自身失败按未运行处理。</summary>
    /// <param name="cli">目标 CLI 类型。</param>
    public bool IsCliRunning(AgentKind cli)
    {
        var spec = _registry.Resolve(cli);
        foreach (var name in spec.ProcessNames)
        {
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0)
                {
                    return true;
                }
            }
            catch
            {
                // 探测失败不阻塞：降级为「未检测到」，文件级安全由原子覆盖 + 回滚保证
            }
        }

        return false;
    }
}
