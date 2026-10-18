using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>子进程启动规格。</summary>
public sealed record CliLaunchSpec(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    bool ShowWindow = false);

/// <summary>子进程执行结果。</summary>
public sealed record ProcessRunResult(
    int ExitCode,
    string Output,
    bool TimedOut,
    bool Cancelled)
{
    /// <summary>命中的危险交互点（非 null 表示已拒绝自动批准）。</summary>
    public string? DangerousInteraction { get; init; }

    /// <summary>自动应答过的交互点（REQ-010b 需写入日志）。</summary>
    public IReadOnlyList<string> AutoAnsweredPoints { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Agent CLI 子进程执行器：实时输出转发、stdin 等待自动应答、危险操作拦截、进程树回收。
/// 对应 REQ-002a / REQ-002b / REQ-008「无僵尸进程残留」。
/// </summary>
public sealed class CliProcessRunner
{
    private readonly AutoAnswerPolicy _policy;
    private readonly Action<string>? _log;
    private readonly IProcessLauncher _launcher;

    public CliProcessRunner(AutoAnswerPolicy policy, Action<string>? log = null, IProcessLauncher? launcher = null)
    {
        _policy = policy;
        _log = log;
        _launcher = launcher ?? RealProcessLauncher.Instance;
    }

    public async Task<ProcessRunResult> RunAsync(
        CliLaunchSpec spec,
        Action<string>? onOutput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (spec.ShowWindow)
        {
            return await RunWindowedAsync(spec, timeout, cancellationToken).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
                ? Environment.CurrentDirectory
                : spec.WorkingDirectory
        };

        // Agent CLI（Node.js 系）输出统一为 UTF-8；不显式指定会按控制台代码页（GBK）解码导致乱码
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);

        // 参数需逐段传入以避免转义问题；此处按空白切分保留引号内内容
        foreach (var arg in SplitArguments(spec.Arguments))
        {
            psi.ArgumentList.Add(arg);
        }

        if (spec.EnvironmentVariables is not null)
        {
            foreach (var kv in spec.EnvironmentVariables)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var process = _launcher.Start(psi);
        if (process is null)
        {
            return new ProcessRunResult(-1, $"无法启动进程：{spec.FileName}", false, false);
        }

        var buffer = new StringBuilder();
        var lockObj = new object();
        var lastOutputAt = DateTimeOffset.Now;
        var autoAnswered = new List<string>();

        void OnData(string? data)
        {
            if (data is null)
            {
                return;
            }

            // 剥离 ANSI 转义序列（颜色 / 光标控制等），使应用内实时流为干净文本行，
            // 避免 TUI 残留控制码污染结果与分类（REQ：让输出可观测）。
            var clean = StripAnsi(data);

            lock (lockObj)
            {
                buffer.AppendLine(clean);
                lastOutputAt = DateTimeOffset.Now;
            }

            // 输出回调（如运行日志写入）抛出的异常会经 Process.AsyncStreamReader 在新建线程池线程上
            // re-throw，升级为 AppDomain 未处理异常并终止进程（2026-10-18 实测崩溃根因）。
            // 任何消费方异常都必须在此吞掉，绝不让其越过 AsyncStreamReader。
            try
            {
                onOutput?.Invoke(clean);
            }
            catch
            {
            }
        }

        // P0-2：异步读取回调收到 null 即表示该流已关闭（EOF）。以此为门，进程退出后必须等两个流
        // 都到达 EOF 才能读 buffer —— 否则输出尾部会被丢掉（漏判额度重置、stream-json result 被截断）。
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }

            OnData(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrClosed.TrySetResult();
                return;
            }

            OnData(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var startedAt = DateTimeOffset.Now;
        var idleThreshold = TimeSpan.FromSeconds(Math.Max(1, _policy.StdinIdleSeconds));
        var noOutputLogged = false;

        try
        {
            while (true)
            {
                if (process.HasExited)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    KillTree(process);
                    return new ProcessRunResult(-1, buffer.ToString(), false, true);
                }

                if (DateTimeOffset.Now - startedAt > timeout)
                {
                    KillTree(process);
                    return new ProcessRunResult(-1, buffer.ToString(), true, false);
                }

                // stdin 等待检测：无输出增长超阈值 → 判定交互点
                if (DateTimeOffset.Now - lastOutputAt > idleThreshold)
                {
                    string tail;
                    lock (lockObj)
                    {
                        tail = Tail(buffer.ToString(), 400);
                    }

                    // 缓冲为空 = 长时间零输出。2026-10-16 实测：CodeBuddy CLI 的 print 模式（-p）
                    // 在整个执行期间不向 stdout 输出任何内容、仅在结束时打印最终结果，
                    // 「零输出」是常态而非卡死（此前在此处按「疑似卡死」强杀，导致正常执行中的
                    // 任务于 30 秒被误杀并误报「单任务执行超时」）。故此处不再主动终止，
                    // 交给任务级超时（REQ-013）兜底；交互点判定仅对「已有输出后停滞」的会话生效。
                    if (string.IsNullOrWhiteSpace(tail))
                    {
                        if (!noOutputLogged)
                        {
                            noOutputLogged = true;
                            _log?.Invoke("[自动应答] 进程尚未产生输出（print 模式下属正常现象），继续等待；任务超时兜底仍生效");
                        }

                        lock (lockObj)
                        {
                            lastOutputAt = DateTimeOffset.Now;
                        }

                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var decision = DangerousOperationGuard.Evaluate(tail, _policy, out var matched);
                    if (decision == GuardDecision.Block)
                    {
                        KillTree(process);
                        return new ProcessRunResult(-1, buffer.ToString(), false, false)
                        {
                            DangerousInteraction = matched
                        };
                    }

                    var classification = PromptClassifier.Classify(tail, _policy);
                    _log?.Invoke($"[自动应答] 交互点「{Trim(tail)}」→ {classification.Description}" +
                                 (decision == GuardDecision.AutoAllow ? "（白名单）" : string.Empty));
                    autoAnswered.Add(Trim(tail));

                    try
                    {
                        await process.StandardInput.WriteLineAsync(classification.Answer).ConfigureAwait(false);
                        // CA2016：向前传播取消令牌——取消语义与 WriteLineAsync 一致（取消即中止本轮交互）
                        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 无法注入 → 按超时失败处理，不挂起整批
                        KillTree(process);
                        return new ProcessRunResult(-1, buffer.ToString(), true, false);
                    }

                    lock (lockObj)
                    {
                        lastOutputAt = DateTimeOffset.Now;
                    }
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // P0-2：进程退出 ≠ 输出已排空。子进程把 stdout 句柄交给孙进程时 EOF 可能永不到达，
            // 故设上限（超时只记日志，不阻塞批处理）。
            await DrainOutputAsync(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            return new ProcessRunResult(-1, buffer.ToString(), false, true);
        }

        return new ProcessRunResult(process.ExitCode, buffer.ToString(), false, false)
        {
            AutoAnsweredPoints = autoAnswered
        };
    }

    /// <summary>输出排空等待上限：EOF 迟迟不到（孙进程持有管道句柄）时不能把任务挂死。</summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>等待 stdout / stderr 两个异步读取流都到达 EOF。</summary>
    private async Task DrainOutputAsync(Task stdoutClosed, Task stderrClosed)
    {
        try
        {
            await Task.WhenAll(stdoutClosed, stderrClosed).WaitAsync(OutputDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log?.Invoke($"[输出排空] 进程已退出，但输出流 {OutputDrainTimeout.TotalSeconds:0} 秒内未到达 EOF，" +
                         "按已收到的输出继续处理（输出可能不完整）");
        }
    }

    /// <summary>
    /// 窗口模式：不重定向 stdio、以真实 TTY 运行 CLI，弹出原生控制台窗口。
    /// 优点：CLI 的 TUI / 进度 / 实时信息可在窗口中直接观察（不依赖应用内行缓冲输出流）。
    /// 代价：读不到 stdout/stderr，故自动应答、危险操作拦截、额度/鉴权分类均失效，
    /// 超时与取消仍可用（按进程树回收）。应用内实时输出流在此模式下为空，窗口即实时视图。
    /// 注意：必须 UseShellExecute=false（2026-10-16 定案：ShellExecute 路径下设置环境变量
    /// 会抛「Process 对象必须将 UseShellExecute 属性设置为 False 才能使用环境变量」，
    /// 而 BuildEnvironment 恒返回非空字典 → 每次任务启动必然异常、批处理静默死亡）；
    /// 改用 CreateNoWindow=false 让控制台子进程自建可见窗口，环境变量照常注入。
    /// </summary>
    private async Task<ProcessRunResult> RunWindowedAsync(
        CliLaunchSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
                ? Environment.CurrentDirectory
                : spec.WorkingDirectory,
            // 窗口模式必须用 Arguments 字符串传参；模板中的 prompt 已由 CliCommandBuilder.Quote 完成引号转义。
            // 实测 .cmd shim 在 UseShellExecute=false 下可由 CreateProcess 正常拉起（自动经 %COMSPEC% 转交）。
            Arguments = spec.Arguments
        };

        if (spec.EnvironmentVariables is not null)
        {
            foreach (var kv in spec.EnvironmentVariables)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        _log?.Invoke($"[窗口模式] 弹出 CLI 窗口执行：{spec.FileName} {spec.Arguments}（应用内实时输出流为空，请以 CLI 窗口为准）");

        using var process = _launcher.Start(psi);
        if (process is null)
        {
            return new ProcessRunResult(-1, $"无法启动进程：{spec.FileName}", false, false);
        }

        var startedAt = DateTimeOffset.Now;
        try
        {
            while (true)
            {
                if (process.HasExited)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    KillTree(process);
                    return new ProcessRunResult(-1, string.Empty, false, true);
                }

                if (DateTimeOffset.Now - startedAt > timeout)
                {
                    KillTree(process);
                    return new ProcessRunResult(-1, string.Empty, true, false);
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            return new ProcessRunResult(-1, string.Empty, false, true);
        }

        return new ProcessRunResult(process.ExitCode, string.Empty, false, false);
    }

    /// <summary>进程树回收，避免残留子进程（第 8 章可靠性）。</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var killInfo = new ProcessStartInfo("taskkill")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                killInfo.ArgumentList.Add("/pid");
                killInfo.ArgumentList.Add(process.Id.ToString());
                killInfo.ArgumentList.Add("/T");
                killInfo.ArgumentList.Add("/F");

                var killer = Process.Start(killInfo);
                killer?.WaitForExit(3000);
            }
        }
        catch
        {
            // 忽略：降级到 process.Kill
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程已退出
        }
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[^maxChars..];

    private static string Trim(string text)
    {
        var s = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 120 ? s : s[^120..];
    }

    /// <summary>剥离 ANSI 转义序列（CSI / OSC / 裸 ESC），使捕获到的输出为干净文本。
    /// 不影响应用内实时流对正常文本行的读取，仅去除终端控制码。</summary>
    private static readonly Regex AnsiRegex = new(
        @"\x1B(?:\[[0-9;?]*[ -/]*[@-~]|\][^\x1B\x07]*\x07|[()][AB0-2])",
        RegexOptions.Compiled);

    private static string StripAnsi(string text)
    {
        if (text.IndexOf('\x1B') < 0)
        {
            return text;
        }

        var stripped = AnsiRegex.Replace(text, string.Empty);
        if (stripped.IndexOf('\x1B') < 0)
        {
            return stripped;
        }

        // 兜底：仍有残留裸 ESC 字符则直接剔除
        var sb = new StringBuilder(stripped.Length);
        foreach (var c in stripped)
        {
            if (c != '\x1B')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>按 shell 规则切分参数（支持双引号包裹）。</summary>
    public static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in arguments)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
