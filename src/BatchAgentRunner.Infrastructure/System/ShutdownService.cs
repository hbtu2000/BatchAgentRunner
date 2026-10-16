using System.Diagnostics;
using BatchAgentRunner.Core.Abstractions;

namespace BatchAgentRunner.Infrastructure.Platform;

/// <summary>
/// 跨平台自动关机（REQ-015）。
/// 权限不足时不执行关机，降级为「提示手动关机」并记录原因。
/// </summary>
public sealed class ShutdownService : IShutdownService
{
    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public async Task<bool> TryShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("shutdown")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("/s");
                psi.ArgumentList.Add("/t");
                psi.ArgumentList.Add("0");
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi = new ProcessStartInfo("osascript")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("tell app \"System Events\" to shut down");
            }
            else
            {
                return false;
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
