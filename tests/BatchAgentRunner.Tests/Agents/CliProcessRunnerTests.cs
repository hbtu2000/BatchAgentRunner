using System.Diagnostics;
using System.Text;
using System.Threading;
using BatchAgentRunner.Agents.Internal;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
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

    /// <summary>
    /// 2026-10-22 事故回归：指令含换行 → .cmd shim 经 cmd.exe 转交时命令行被截断，
    /// 换行后的参数（--model / --output-format / --port）整体丢失，CLI 退回交互模式、实时输出流空白。
    /// 本用例用「回显参数的 shim」直接验证：压平后尾部参数必须仍然送达子进程。
    /// </summary>
    [Fact]
    public async Task RunAsync_参数含换行_压平后尾部参数不丢失()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // .cmd shim + cmd.exe 截断行为仅 Windows 验证
        }

        var script = Path.Combine(Path.GetTempPath(), $"bar-arg-echo-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, "@echo off\r\necho ARGS %*\r\n", Encoding.Default);

        try
        {
            var request = new AgentRequest(
                "t1", "line1\r\n  line2", new AccountConfig { Id = "acc-1" },
                new ModelCandidate { Name = "m1" }, ExpertRef.Default(),
                Environment.CurrentDirectory, TimeSpan.FromMinutes(5), new AutoAnswerPolicy());
            var arguments = CliCommandBuilder.Build(
                CliCommandBuilder.CodeBuddyTemplate, request, extra: "--flag-tail");

            var runner = CreateRunner(stdinIdleSeconds: 5);
            var spec = new CliLaunchSpec(script, arguments, Path.GetTempPath());

            var result = await runner.RunAsync(spec, null, TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            // 截断时尾部参数与模型参数都会消失，只剩被截断的 -p
            Assert.Contains("--flag-tail", result.Output);
            Assert.Contains("--model m1", result.Output);
            Assert.Contains("--output-format stream-json", result.Output);
            Assert.Contains("line1 line2", result.Output);
        }
        finally
        {
            File.Delete(script);
        }
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

    // ───────────────────────── P1-13：KillTree 重写（整树回收，不再依赖外部 taskkill / 同步阻塞） ─────────────────────────

    /// <summary>
    /// P1-13 不变量：<see cref="CliProcessRunner.KillTree"/> 必须整树回收——父进程拉起的常驻子进程（beacon）
    /// 在调用后必须停止存活，否则会残留僵尸子进程（原 taskkill /T 与重写后的 Kill(entireProcessTree:true) 均须满足）。
    /// 若被回退为「仅杀父进程」，beacon 子进程会继续存活，本用例失败，从而锁死整树语义。
    /// </summary>
    [Fact]
    public void KillTree_父进程拉起常驻子进程_整树回收后子进程不再存活()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 依赖 cmd/start，仅 Windows 验证
        }

        var dir = Path.Combine(Path.GetTempPath(), $"bar-killtree-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var beacon = Path.Combine(dir, "beacon.txt");
            var childCmd = Path.Combine(dir, "child_beacon.cmd");
            var parentCmd = Path.Combine(dir, "parent.cmd");

            // beacon 路径直接烤进脚本，避免多层 cmd 参数引号传递导致的不可靠
            // 子进程：每秒向 beacon 写入时间戳（心跳），用于判定其是否仍存活
            File.WriteAllText(childCmd,
                $"@echo off\r\n:loop\r\necho %TIME% > \"{beacon}\"\r\nping -n 2 127.0.0.1 > nul\r\ngoto loop\r\n",
                Encoding.Default);
            // 父进程：同步拉起 beacon 子进程（作为 Process 对象的直接后代），自身阻塞等待，
            // 使 KillTree(父) 必须整树回收才能同时干掉 beacon 后代
            File.WriteAllText(parentCmd,
                "@echo off\r\ncmd /c child_beacon.cmd\r\n",
                Encoding.Default);

            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{parentCmd}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir
            })!;

            // 先确认 beacon 子进程确实在持续心跳（存活）
            Assert.True(WaitForBeaconHeartbeat(beacon, TimeSpan.FromSeconds(20)),
                "beacon 子进程未如期启动并持续写入，测试前置不成立");

            CliProcessRunner.KillTree(process);

            // 整树回收后 beacon 必须停止更新（子进程已随父进程一并被杀）
            var lastWrite = File.GetLastWriteTimeUtc(beacon);
            Thread.Sleep(3000);
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(beacon));
            Assert.True(process.HasExited);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 进程残留时忽略 */ }
        }
    }

    /// <summary>
    /// P1-13 健壮性：对已经退出的进程调用 KillTree 不得抛异常（回收目的已达成）。
    /// </summary>
    [Fact]
    public void KillTree_进程已退出_不抛异常()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
        Assert.True(process.HasExited);

        var ex = Record.Exception(() => CliProcessRunner.KillTree(process));
        Assert.Null(ex);
    }

    /// <summary>
    /// P1-13 基本语义：KillTree 必须真正终止目标进程（取代原 taskkill 的终止职责）。
    /// </summary>
    [Fact]
    public void KillTree_单进程_被立即终止()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        Assert.False(process.HasExited);

        CliProcessRunner.KillTree(process);

        Assert.True(SpinWait.SpinUntil(() => process.HasExited, TimeSpan.FromSeconds(5)));
    }

    /// <summary>轮询 beacon 文件，直到其 mtime 至少推进一次（证明子进程心跳循环在跑）。</summary>
    private static bool WaitForBeaconHeartbeat(string beaconPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        // 等待首次写入
        while (DateTime.UtcNow < deadline && !File.Exists(beaconPath))
        {
            Thread.Sleep(50);
        }

        if (!File.Exists(beaconPath))
        {
            return false;
        }

        var lastWrite = File.GetLastWriteTimeUtc(beaconPath);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            var current = File.GetLastWriteTimeUtc(beaconPath);
            if (current != lastWrite)
            {
                return true;
            }
        }

        return false;
    }
}
