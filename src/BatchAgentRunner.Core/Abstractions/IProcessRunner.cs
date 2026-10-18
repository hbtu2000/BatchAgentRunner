namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// 进程运行端口（P1-9）：把「启动操作系统进程并收集退出码/标准输出/标准错误」这一基础设施能力
/// 抽象出来，使 <see cref="BatchAgentRunner.Core.Scheduling.GateEvaluator"/> 等 Core 类型不再
/// 直接引用 <see cref="System.Diagnostics.Process"/>，从而守住 §4.3「Core 不得起进程」的架构约束。
/// 真实实现位于 Infrastructure（<c>RealProcessRunner</c>），测试可用内存假实现替代。
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// 运行一个进程并等待其结束。
    /// </summary>
    /// <param name="fileName">可执行文件（如 <c>cmd.exe</c> 或 <c>/bin/sh</c>）。</param>
    /// <param name="arguments">命令行参数（已含 shell 包装，如 <c>/c "echo hi"</c>）。</param>
    /// <param name="workingDirectory">工作目录；为空或不存在时由实现决定行为。</param>
    /// <param name="cancellationToken">取消令牌（超时/用户取消时杀掉进程并抛出）。</param>
    /// <returns>(退出码, 标准输出, 标准错误)。启动失败约定返回 <c>(-1, "", 错误信息)</c>。</returns>
    Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, string arguments, string? workingDirectory, CancellationToken cancellationToken);
}
