using BatchAgentRunner.Core.Abstractions;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 进程运行器的惰性默认实现（仅用于「不实际使用命令门禁」的测试/旧调用点）。
/// Core 不得引用 Infrastructure，故无法在此处构造真实的 <c>RealProcessRunner</c>；
/// 生产路径由 <c>BatchAgentRunner.Desktop.Composition.EngineFactory</c> 显式注入真实实现。
/// 默认让命令门禁直接通过（返回退出码 0），避免 Mock 驱动的测试因缺runner而误失败。
/// </summary>
public sealed class NullProcessRunner : IProcessRunner
{
    /// <summary>不实际执行命令，直接返回退出码 0 与空输出（惰性默认实现）。</summary>
    /// <returns>恒为 (0, 空, 空)。</returns>
    public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, string arguments, string? workingDirectory, CancellationToken cancellationToken) =>
        Task.FromResult<(int, string, string)>((0, string.Empty, string.Empty));
}
