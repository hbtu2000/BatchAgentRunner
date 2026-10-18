using System.Diagnostics;
using System.Text;
using BatchAgentRunner.Agents.Internal;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Tests.Agents;

/// <summary>
/// CliProcessRunner 零输出行为回归测试（2026-10-16 批量任务误杀事故）：
/// CodeBuddy CLI print 模式（-p）在整个执行期间不向 stdout 输出、仅在结束时打印结果，
/// 「零输出」是常态而非卡死；不得按 stdin 空闲阈值强杀，应交由任务级超时兜底。
/// </summary>
[Trait("Category", "Process")]
[Trait("Category", "Invariant")]
public class CliProcessRunnerTests
{
    private static CliProcessRunner CreateRunner(int stdinIdleSeconds = 1) =>
        new(new AutoAnswerPolicy { StdinIdleSeconds = stdinIdleSeconds });

    /// <summary>假启动器：不启动任何进程，直接返回 null（模拟启动失败）。</summary>
    private sealed class NullProcessLauncher : IProcessLauncher
    {
        public Process? Start(ProcessStartInfo startInfo) => null;
    }

    [Fact]
    public async Task RunAsync_启动器返回null_返回无法启动进程结果且不进入主循环()
    {
        // P2-21：IProcessLauncher 注入缝——无需真实进程即可验证「启动失败」分支
        var runner = new CliProcessRunner(new AutoAnswerPolicy(), null, new NullProcessLauncher());
        var spec = new CliLaunchSpec("必然不存在的二进制", "args", Environment.CurrentDirectory);

        var result = await runner.RunAsync(spec, null, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("无法启动进程", result.Output);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task RunAsync_静默进程超出自空闲阈值_不再误杀_等待自然退出()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 静默进程用 cmd 构造，仅 Windows 验证
        }

        // ping -n 3 约 2 秒且输出重定向到 nul：全程零输出，超出 1 秒空闲阈值
        var runner = CreateRunner(stdinIdleSeconds: 1);
        var spec = new CliLaunchSpec("cmd", "/c ping -n 3 127.0.0.1 > nul",
            Environment.CurrentDirectory);

        var result = await runner.RunAsync(spec, null, TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_静默进程真超时_仍按超时回收()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 约 60 秒的静默进程，2 秒即超时：任务级超时兜底必须仍然生效
        var runner = CreateRunner(stdinIdleSeconds: 1);
        var spec = new CliLaunchSpec("cmd", "/c ping -n 60 127.0.0.1 > nul",
            Environment.CurrentDirectory);

        var result = await runner.RunAsync(spec, null, TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_窗口模式注入环境变量_不再抛ShellExecute异常_正常执行()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 2026-10-16 回归：窗口模式原用 UseShellExecute=true 且注入环境变量，
        // Process.Start 必抛「Process 对象必须将 UseShellExecute 属性设置为 False 才能使用环境变量」，
        // 导致每次任务启动即异常、批处理静默死亡（无 task_failed 事件、无报告、日志戛然而止）。
        // BuildEnvironment 恒返回非空字典，故任何窗口模式执行都必须能带环境变量启动。
        var runner = CreateRunner(stdinIdleSeconds: 1);
        var env = new Dictionary<string, string> { ["BATCH_AGENT_ACCOUNT"] = "profile:default" };
        var spec = new CliLaunchSpec(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            "/c exit 0", Environment.CurrentDirectory, env)
        {
            ShowWindow = true
        };

        var result = await runner.RunAsync(spec, null, TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_选择型交互_自动选择第一个选项并继续执行()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // cmd/set/p 仅 Windows 验证
        }

        var script = Path.Combine(Path.GetTempPath(), $"bar-choice-test-{Guid.NewGuid()}.cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("echo select one:");
        sb.AppendLine("echo 1. option A");
        sb.AppendLine("echo 2. option B");
        sb.AppendLine("set /p answer=");
        sb.AppendLine("echo CHOSEN=%answer%");
        File.WriteAllText(script, sb.ToString(), Encoding.Default);

        try
        {
            var policy = AutoAnswerPolicy.CreateDefault();
            policy.StdinIdleSeconds = 1;
            var runner = new CliProcessRunner(policy);
            var spec = new CliLaunchSpec("cmd", $"/c \"{script}\"", Environment.CurrentDirectory);

            var result = await runner.RunAsync(spec, null, TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("CHOSEN=1", result.Output);
            Assert.Single(result.AutoAnsweredPoints);
            Assert.Contains("select", result.AutoAnsweredPoints[0]);
        }
        finally
        {
            File.Delete(script);
        }
    }

    /// <summary>
    /// P0-2 不变量：进程退出后必须排空异步输出流再读缓冲，输出尾部不得丢失
    /// （尾部丢失会漏判额度重置、截断 stream-json 的 result 事件）。
    /// </summary>
    [Fact]
    public async Task RunAsync_大量输出_进程退出后输出尾部不丢失()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 用 cmd 构造大输出，仅 Windows 验证
        }

        // 约 20 万字符输出后打印哨兵行：不排空异步读取流时，尾部几乎必然被丢掉
        var script = Path.Combine(Path.GetTempPath(), $"bar-tail-test-{Guid.NewGuid()}.cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("for /L %%i in (1,1,2000) do echo line-%%i-0123456789012345678901234567890123456789012345678901234567890123456789");
        sb.AppendLine("echo TAIL-SENTINEL");
        File.WriteAllText(script, sb.ToString(), Encoding.Default);

        try
        {
            var runner = CreateRunner(stdinIdleSeconds: 1);
            var spec = new CliLaunchSpec("cmd", $"/c \"{script}\"", Environment.CurrentDirectory);

            var result = await runner.RunAsync(spec, null, TimeSpan.FromSeconds(60),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("TAIL-SENTINEL", result.Output);
            Assert.Contains("line-1-", result.Output);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task RunAsync_确认型交互_仍注入默认应答()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // cmd/set/p 仅 Windows 验证
        }

        var script = Path.Combine(Path.GetTempPath(), $"bar-confirm-test-{Guid.NewGuid()}.cmd");
        File.WriteAllText(script,
            "@echo off\necho 是否继续 (y/n)?\nset /p answer=\necho ANSWER=%answer%\n",
            Encoding.Default);

        try
        {
            var policy = AutoAnswerPolicy.CreateDefault();
            policy.StdinIdleSeconds = 1;
            var runner = new CliProcessRunner(policy);
            var spec = new CliLaunchSpec("cmd", $"/c \"{script}\"", Environment.CurrentDirectory);

            var result = await runner.RunAsync(spec, null, TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("ANSWER=y", result.Output);
        }
        finally
        {
            File.Delete(script);
        }
    }
}
