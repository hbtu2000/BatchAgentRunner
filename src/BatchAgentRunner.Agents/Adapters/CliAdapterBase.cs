using System.Text.RegularExpressions;
using BatchAgentRunner.Agents.Internal;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Core.Text;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Agents.Adapters;

/// <summary>适配器通用设置（CLI 路径与参数模板外置，避免 M0 前硬编码猜测）。</summary>
public sealed class CliAdapterSettings
{
    public string? ExecutablePath { get; set; }

    public string? ArgumentsTemplate { get; set; }

    /// <summary>附加无人值守参数（如 --yes / --dangerously-skip-permissions）。</summary>
    public string? ExtraArguments { get; set; }

    /// <summary>账号凭据注入的环境变量名（仅存引用名，不明文保存，PRD 第 8 章）。</summary>
    public string CredentialEnvironmentVariable { get; set; } = "BATCH_AGENT_ACCOUNT";

    /// <summary>
    /// API Key 注入的环境变量名。凭据引用形如 env:XXX 时，会把环境变量 XXX 的值
    /// 以此变量名注入 CLI 子进程（codebuddy 2.167.0 读取 CODEBUDDY_API_KEY），
    /// 从而支持「API Key 型多账号」。
    /// </summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "CODEBUDDY_API_KEY";

    /// <summary>执行任务时是否弹出 CLI 原生控制台窗口（不重定向 stdio，以真实 TTY 运行，便于实时观察）。</summary>
    public bool ShowWindow { get; set; }

    public Action<string>? Log { get; set; }

    /// <summary>运行日志（用于任务输出逐行实时落盘，REQ：实时可观测）。执行路径由引擎注入，
    /// 预检/列模型等非执行路径为 null，此时 WriteTaskOutput 安全空操作、不落盘。</summary>
    public IRunLogger? Logger { get; set; }
}

/// <summary>
/// 真实 CLI 适配器基类：子进程执行 + 输出分类 + 危险操作拦截。
/// 各 CLI 的差异（参数形态 / 错误特征）由子类覆写。
/// </summary>
public abstract class CliAdapterBase : IAgentCliAdapter
{
    // CA1051（P0-12）：protected 只读字段改为 protected 自动属性——对子类源码兼容，
    // 但不以「可见实例字段」形态暴露（子类均在构造期赋值、运行期只读）。
    protected CliAdapterSettings Settings { get; }
    protected AutoAnswerPolicy Policy { get; }
    protected QuotaErrorClassifier Classifier { get; }
    protected CliProcessRunner Runner { get; }
    /// <summary>凭证位置注册表（P1-16：显式注入，替代原静态全局态）。</summary>
    protected ICredentialLocationRegistry Locations { get; }

    protected CliAdapterBase(CliAdapterSettings settings, AutoAnswerPolicy? policy = null,
        QuotaErrorClassifier? classifier = null, ICredentialLocationRegistry? registry = null)
    {
        Settings = settings;
        Policy = policy ?? AutoAnswerPolicy.CreateDefault();
        Classifier = classifier ?? new QuotaErrorClassifier();
        Runner = new CliProcessRunner(Policy, settings.Log);
        Locations = registry ?? new CredentialLocationRegistry();
    }

    public abstract AgentKind Kind { get; }

    /// <summary>默认可执行文件名（用于 PATH 查找）。</summary>
    protected abstract string DefaultExecutable { get; }

    protected abstract string DefaultArgumentsTemplate { get; }

    public virtual async Task<PreflightResult> ValidateEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        var exe = ResolveExecutable();
        if (exe is null)
        {
            return PreflightResult.Fail($"未找到 {Kind} CLI（{DefaultExecutable}）：请安装或在配置中指定 CLI 路径");
        }

        return await Task.FromResult(PreflightResult.Pass($"{Kind} CLI 可用：{exe}")).ConfigureAwait(false);
    }

    public virtual async Task<PreflightResult> ValidateAccountAsync(AccountConfig account,
        CancellationToken cancellationToken = default)
    {
        var exe = ResolveExecutable();
        if (exe is null)
        {
            return PreflightResult.Fail("CLI 不可用，无法校验账号", account.Id);
        }

        // 极简调用校验（REQ-010）：真实命令待 M0 V-4 验证，此处用 --version 形态探测
        var spec = new CliLaunchSpec(exe, "--version", Environment.CurrentDirectory,
            BuildEnvironment(account));
        var result = await Runner.RunAsync(spec, null, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0
            ? PreflightResult.Pass($"账号 {account.Label} 可用", account.Id)
            : PreflightResult.Fail($"账号 {account.Label} 预检失败（退出码 {result.ExitCode}）", account.Id);
    }

    public virtual async Task<AgentExecutionResult> ExecuteAsync(AgentRequest request,
        CancellationToken cancellationToken = default)
    {
        var exe = ResolveExecutable();
        if (exe is null)
        {
            return AgentExecutionResult.Fail(request,
                new AgentFailure(AgentFailureKind.TaskError, $"未找到 {Kind} CLI，无法执行"));
        }

        // 批量执行前确保「全程零确认」：把 CLI 权限模式持久化为 bypassPermissions，
        // 避免 agent 在工具调用审批框卡死（非 TTY 下审批框不以纯文本行出现、也无法被 stdin 自动应答捕获）。
        // Mock 不拉起真实 CLI，跳过；异常不阻断执行，仅告警。
        if (Kind != AgentKind.Mock)
        {
            try
            {
                await CliPermissionSettings.EnsureBypassPermissionsAsync(
                    Locations, Locations.Resolve(Kind), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Settings.Log?.Invoke($"WARN 设置零确认权限失败（{ex.Message}），agent 可能卡在审批框");
            }
        }

        var template = Settings.ArgumentsTemplate ?? DefaultArgumentsTemplate;
        // 2026-10-22 事故：任务指令自带换行 → 经 cmd.exe 转交时命令行被截断，后续参数（--model /
        // --output-format / --port）全部丢失，CLI 退回交互模式、stdout 无 stream-json，实时输出流一片空白。
        // Build 内已折叠为单行；此处留一条痕迹，避免同类问题再靠现场猜测。
        if (request.Instruction.IndexOfAny(['\r', '\n']) >= 0)
        {
            Settings.Log?.Invoke("[CLI 参数] 任务指令含换行，已折叠为单行后传给 CLI（换行会被 cmd.exe 当命令分隔符，截断后续参数）");
        }

        var arguments = CliCommandBuilder.Build(template, request, Settings.ExtraArguments);

        // codebuddy / WorkBuddy 启动时占用固定本地端口（默认 52607）作为内嵌服务端口。
        // 若该端口已被本机其它 codebuddy/WorkBuddy 进程（如桌面端）占用，CLI 会启动失败或长时间无输出，
        // 表现为「实时输出流」空白、任务卡死（2026-10-17 实测：端口冲突导致子进程整轮零输出）。
        // 为每个子进程分配独立空闲端口，从根本上规避冲突；实测显式 --port 后 stream-json 正常流式输出。
        if (Kind is AgentKind.CodeBuddy or AgentKind.WorkBuddy)
        {
            try
            {
                arguments += " --port " + FreePortFinder.GetFreeTcpPort();
            }
            catch
            {
                // 端口探测失败时退化为不附加 --port，行为同旧版（不阻断执行）
            }
        }

        // 现场可诊断性：把「CLI 实际收到的参数」落一条 INFO（截断 + 经运行日志统一脱敏）。
        // 历史版本运行日志里完全没有参数可查，导致「实时输出流空白」只能靠外部猜（本次排障成本极高）。
        Settings.Log?.Invoke($"[CLI] {exe} {TextTruncate.Truncate(arguments, 300, "…")}");

        // stream-json 渲染：把 CLI 的逐行 JSON 事件翻译成可读行喂给应用内实时流；
        // 同时记录最终回答（result 事件）供结果回写。原始 stdout/stderr 仍由 CliProcessRunner
        // 累积进 run.Output，鉴权/额度分类照常基于原始文本工作。
        var renderer = new StreamJsonRenderer();
        Action<string>? onOut = request.OnOutputChunk is null
            ? null
            : raw =>
            {
                foreach (var line in renderer.Feed(raw))
                {
                    request.OnOutputChunk(line);
                    Settings.Logger?.WriteTaskOutput(line);
                }
            };

        var spec = new CliLaunchSpec(exe, arguments, request.WorkingDirectory, BuildEnvironment(request.Account))
        {
            ShowWindow = Settings.ShowWindow
        };
        var run = await Runner.RunAsync(spec, onOut, request.Timeout, cancellationToken)
            .ConfigureAwait(false);

        // 刷出 stream-json 增量缓冲中的残留正文（若进程在最后一个 delta 后未再产生完整事件）
        if (request.OnOutputChunk is not null)
        {
            foreach (var line in renderer.Flush())
            {
                request.OnOutputChunk(line);
                Settings.Logger?.WriteTaskOutput(line);
            }
        }

        if (run.DangerousInteraction is { } matched)
        {
            return AgentExecutionResult.Fail(request, new AgentFailure(
                AgentFailureKind.DangerousOperation,
                $"检测到危险操作确认（{matched}），已拒绝自动批准并中断该任务"), run.Output);
        }

        if (run.Cancelled)
        {
            return AgentExecutionResult.Fail(request,
                new AgentFailure(AgentFailureKind.TaskError, "执行被取消"), run.Output);
        }

        if (run.TimedOut)
        {
            return AgentExecutionResult.Fail(request,
                new AgentFailure(AgentFailureKind.Timeout, $"执行超过 {request.Timeout.TotalMinutes:0} 分钟"), run.Output);
        }

        if (run.ExitCode != 0)
        {
            var failure = Classifier.Classify(run.Output, run.ExitCode);
            return AgentExecutionResult.Fail(request, failure, run.Output);
        }

        // 2026-10-15 实测（codebuddy 2.167.0）：认证失败等错误以「exit 0 + stderr 输出」返回，
        // 仅凭退出码会把失败当成功回写任务；按凭据失效特征兜底分类。
        // 2026-10-16 修复：429 限流同样以 exit 0 返回（stream-json result 事件 is_error=true），
        // 此前只拦 AuthExpired 导致 RateLimited/QuotaExhausted 被当成功 → 引擎不退避、不记冷却、不切换账号。
        return ResolveExitZeroOutcome(request, run.Output, renderer.FinalResult, renderer.FinalIsError);
    }

    /// <summary>
    /// exit 0 时的成败兜底判定（protected 以便子类/回归测试复用）：
    /// ① result 事件 is_error=true → CLI 自报失败，按输出分类（限流/额度/凭据/普通失败）；
    /// ② 无 result 事件（finalResult 为空）但输出命中凭据失效/限流/额度特征 → 按分类器判失败；
    /// ③ 有正常 result 事件（is_error=false）→ 信任之，即使回答文本中碰巧含有 429/额度 等词也不误杀。
    /// </summary>
    protected AgentExecutionResult ResolveExitZeroOutcome(
        AgentRequest request, string output, string? finalResult, bool finalIsError)
    {
        if (finalIsError)
        {
            var failure = Classifier.Classify(output, exitCode: 1);
            return AgentExecutionResult.Fail(request, failure, output);
        }

        if (finalResult is null)
        {
            var silent = Classifier.Classify(output, exitCode: 0);
            if (silent.Kind is AgentFailureKind.AuthExpired
                or AgentFailureKind.RateLimited
                or AgentFailureKind.QuotaExhausted)
            {
                return AgentExecutionResult.Fail(request, silent, output);
            }
        }

        var result = ExtractResult(output, finalResult);
        return AgentExecutionResult.Ok(request, result, ExtractUsage(output));
    }

    /// <summary>
    /// 实时读取该账号可用的模型列表：带上账号凭据环境运行 CLI 的模型列举命令，
    /// 解析其输出中的「当前支持模型」清单。失败或解析不到时返回空列表（降级为手填）。
    /// </summary>
    public virtual async Task<IReadOnlyList<string>> ListModelsAsync(AccountConfig account,
        CancellationToken cancellationToken = default)
    {
        var exe = ResolveExecutable();
        if (exe is null)
        {
            return Array.Empty<string>();
        }

        var spec = new CliLaunchSpec(exe, ModelListArguments, Environment.CurrentDirectory, BuildEnvironment(account));
        var result = await Runner.RunAsync(spec, null, TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);

        if (result.TimedOut || result.Cancelled || result.ExitCode != 0)
        {
            return Array.Empty<string>();
        }

        return ParseSupportedModels(result.Output);
    }

    /// <summary>
    /// 模型目录 = CLI 拉到的模型名 ∪ 官方产品配置缓存目录；倍率取官方 credits（如 0.79 → 显示 0.79x）。
    /// 官方目录里 CLI 没列出的模型也会并入尾部，保证与官方选择器口径一致。
    /// </summary>
    public virtual async Task<IReadOnlyList<ModelCatalogEntry>> ListModelCatalogAsync(AccountConfig account,
        CancellationToken cancellationToken = default)
    {
        var names = await ListModelsAsync(account, cancellationToken).ConfigureAwait(false);

        CodeBuddyModelCatalogReader.TryReadCredits(out var credits);

        var list = new List<ModelCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var key = name.Trim();
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            credits.TryGetValue(key, out var multiplier);
            list.Add(new ModelCatalogEntry(name, multiplier));
        }

        foreach (var (id, multiplier) in credits)
        {
            if (seen.Add(id))
            {
                list.Add(new ModelCatalogEntry(id, multiplier));
            }
        }

        return list;
    }

    /// <summary>用于列举模型的 CLI 参数（子类可覆写为更精确的命令，如 /models）。</summary>
    protected virtual string ModelListArguments => "--help";

    /// <summary>
    /// 从 CLI 输出解析可用模型名。默认匹配 codebuddy/WorkBuddy 的
    /// 「Currently supported: (a, b, c)」形态；不同 CLI 可覆写。
    /// </summary>
    protected virtual IReadOnlyList<string> ParseSupportedModels(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<string>();
        }

        var match = Regex.Match(output, @"Currently supported:\s*\(([^)]*)\)");
        if (!match.Success)
        {
            return Array.Empty<string>();
        }

        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 从 CLI 输出中提取结果正文。stream-json 模式下原始输出是逐行 JSON（不可直接当答案），
    /// 故优先取渲染器抽取的最终回答；解析失败或非 JSON 输出再退化为原始文本。各 CLI 可在 adapter 层覆写。
    /// </summary>
    protected virtual string ExtractResult(string rawOutput, string? finalResult) =>
        !string.IsNullOrEmpty(finalResult)
            ? finalResult!
            : (rawOutput?.Trim() ?? string.Empty);

    /// <summary>
    /// 提取用量/费用（REQ-004b / M0 V-7）。当前不可得时返回 null，
    /// 引擎会降级为「调用次数 × 预估单价」估算口径并在报告中标注。
    /// </summary>
    protected virtual AgentUsage? ExtractUsage(string rawOutput) => null;

    protected string? ResolveExecutable()
    {
        if (!string.IsNullOrWhiteSpace(Settings.ExecutablePath))
        {
            return File.Exists(Settings.ExecutablePath) ? Settings.ExecutablePath : null;
        }

        var found = FindOnPath(DefaultExecutable);
        return found;
    }

    protected static string? FindOnPath(string name)
    {
        // Windows 上 npm 全局包会同时生成无扩展名 sh 脚本与 .cmd shim；
        // 无扩展名脚本 Process.Start 无法执行（"not a valid application for this OS platform"），
        // 必须优先命中 .exe/.cmd/.bat，无扩展名仅作 Unix 兜底。
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in new[] { ".exe", ".cmd", ".bat", string.Empty })
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    protected virtual Dictionary<string, string> BuildEnvironment(AccountConfig account)
    {
        var env = new Dictionary<string, string>
        {
            [Settings.CredentialEnvironmentVariable] = account.CredentialReference
        };

        // 2026-10-15：打通 env: 凭据引用。形如 "env:XXX" 的账号会把环境变量 XXX 的值
        // 以 API Key 变量名注入 CLI 子进程，使 API Key 型多账号真正生效。
        // 只注入变量名与是否存在，变量值一律不落日志（PRD 第 8 章）。
        if (account.CredentialReference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var variableName = account.CredentialReference[4..].Trim();
            var value = variableName.Length == 0 ? null : Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrEmpty(value))
            {
                env[Settings.ApiKeyEnvironmentVariable] = value;
            }
            else
            {
                Settings.Log?.Invoke(
                    $"WARN 账号 {account.Id} 的凭据引用 {account.CredentialReference} 对应的环境变量不存在或为空，已跳过 API Key 注入");
            }
        }

        return env;
    }
}
