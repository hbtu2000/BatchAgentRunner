using System.Diagnostics;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>
/// 进程启动抽象（P2-21 去 Flaky）：将 <see cref="CliProcessRunner"/> 与真实的
/// <see cref="Process.Start(System.Diagnostics.ProcessStartInfo)"/> 解耦，使进程相关逻辑可在测试中
/// 注入假启动器，避免真实进程带来的环境耦合与不确定性；生产默认走 <see cref="RealProcessLauncher"/>。
/// </summary>
public interface IProcessLauncher
{
    /// <summary>按给定规格启动进程；返回 <c>null</c> 表示启动失败（与 <c>Process.Start</c> 语义一致）。</summary>
    Process? Start(ProcessStartInfo startInfo);
}

/// <summary>默认实现：直接调用 <see cref="Process.Start(System.Diagnostics.ProcessStartInfo)"/>。</summary>
public sealed class RealProcessLauncher : IProcessLauncher
{
    /// <summary>共享单例，生产环境的默认启动器。</summary>
    public static readonly RealProcessLauncher Instance = new();

    /// <summary>直接调用 Process.Start 启动子进程。</summary>
    /// <param name="startInfo">进程启动参数：文件名、命令行与环境设置。</param>
    public Process? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}
