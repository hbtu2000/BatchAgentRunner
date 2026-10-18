using System.Diagnostics;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>
/// 进程启动抽象（P2-21 去 Flaky）：将 <see cref="CliProcessRunner"/> 与真实的
/// <see cref="Process.Start(System.Diagnostics.ProcessStartInfo)"/> 解耦，使进程相关逻辑可在测试中
/// 注入假启动器，避免真实进程带来的环境耦合与不确定性；生产默认走 <see cref="RealProcessLauncher"/>。
/// </summary>
public interface IProcessLauncher
{
    /// <summary>按给定规格启动进程；返回 <c>null</c> 表示启动失败（与 <see cref="Process.Start"/> 语义一致）。</summary>
    Process? Start(ProcessStartInfo startInfo);
}

/// <summary>默认实现：直接调用 <see cref="Process.Start(System.Diagnostics.ProcessStartInfo)"/>。</summary>
public sealed class RealProcessLauncher : IProcessLauncher
{
    public static readonly RealProcessLauncher Instance = new();

    public Process? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}
