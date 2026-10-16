using BatchAgentRunner.Agents.Internal;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Tests.Agents;

/// <summary>
/// CliProcessRunner 零输出行为回归测试（2026-10-16 批量任务误杀事故）：
/// CodeBuddy CLI print 模式（-p）在整个执行期间不向 stdout 输出、仅在结束时打印结果，
/// 「零输出」是常态而非卡死；不得按 stdin 空闲阈值强杀，应交由任务级超时兜底。
/// </summary>
public class CliProcessRunnerTests
{
    private static CliProcessRunner CreateRunner(int stdinIdleSeconds = 1) =>
        new(new AutoAnswerPolicy { StdinIdleSeconds = stdinIdleSeconds });

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
}
