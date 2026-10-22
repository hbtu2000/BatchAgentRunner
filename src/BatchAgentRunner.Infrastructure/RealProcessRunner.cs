using System.Diagnostics;
using System.Text;
using BatchAgentRunner.Core.Abstractions;

namespace BatchAgentRunner.Infrastructure;

/// <summary>
/// 真实进程运行器（P1-9）：唯一被允许在基础设施层直接 <c>new Process()</c> 的类型。
/// 仅经由 <see cref="IProcessRunner"/> 端口被 Core 消费，从而把进程 IO 赶出 Core。
/// </summary>
public sealed class RealProcessRunner : IProcessRunner
{
    /// <summary>启动子进程并收集退出码与标准输出/错误，支持取消。</summary>
    public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, string arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                outSb.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                errSb.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            return (-1, string.Empty, "无法启动命令进程");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* 忽略杀进程失败 */ }
            throw;
        }

        return (process.ExitCode, outSb.ToString(), errSb.ToString());
    }
}
