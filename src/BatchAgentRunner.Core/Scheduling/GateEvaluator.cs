using System.Text;
using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Text;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>门禁评估结果。</summary>
public sealed record GateResult(bool Passed, string? Leftover = null)
{
    /// <summary>通过的共享单例。</summary>
    public static readonly GateResult Pass = new(true);

    /// <summary>构造失败结果，可携带遗留输出供修订循环使用。</summary>
    /// <param name="leftover">失败时遗留的输出。</param>
    /// <returns>失败的门禁结果。</returns>
    public static GateResult Fail(string? leftover = null) => new(false, leftover);
}

/// <summary>门禁评估上下文。</summary>
public sealed class GateEvaluationContext
{
    /// <summary>待评估的质量门禁配置。</summary>
    public QualityGate Gate { get; init; } = new();

    /// <summary>上一阶段的输出文本（Command 门禁正则匹配用）。</summary>
    public string StageOutput { get; init; } = string.Empty;

    /// <summary>Command 门禁命令的工作目录。</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>所属任务 ID。</summary>
    public string TaskId { get; init; } = string.Empty;

    /// <summary>所属阶段 ID。</summary>
    public string StageId { get; init; } = string.Empty;

    /// <summary>执行时使用的账号。</summary>
    public string? AccountUsed { get; init; }

    /// <summary>执行时使用的模型。</summary>
    public string? ModelUsed { get; init; }
}

/// <summary>
/// 质量门禁评估器（二期：阶段间卡点）。
/// <list type="bullet">
///   <item><description>Command：在 Workspace 目录运行命令，按退出码（默认 0）+ 可选输出正则判定。</description></item>
///   <item><description>Review：交由注入的 reviewer 委托（引擎用 adapter 跑一次审查阶段）。</description></item>
///   <item><description>Manual：交由注入的 manualApprover 委托（无 UI 确认时默认自动通过）。</description></item>
/// </list>
/// </summary>
public sealed class GateEvaluator
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<GateEvaluationContext, Task<GateResult>>? _reviewer;
    private readonly Func<GateEvaluationContext, Task<bool>>? _manualApprover;

    /// <summary>
    /// 构造门禁评估器。
    /// </summary>
    public GateEvaluator(
        IProcessRunner processRunner,
        Func<GateEvaluationContext, Task<GateResult>>? reviewer = null,
        Func<GateEvaluationContext, Task<bool>>? manualApprover = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _reviewer = reviewer;
        _manualApprover = manualApprover;
    }

    /// <summary>
    /// 按门禁类型评估：Command 在 Workspace 目录跑命令、按退出码与可选输出正则判定；
    /// Review / Manual 交由注入的委托执行（无委托时默认通过）。
    /// </summary>
    /// <returns>门禁结果（通过 / 失败含遗留输出）。</returns>
    public async Task<GateResult> EvaluateAsync(QualityGate gate, GateEvaluationContext ctx, CancellationToken cancellationToken)
    {
        switch (gate.Kind)
        {
            case GateKind.Command:
                return await EvaluateCommandAsync(gate, ctx, cancellationToken).ConfigureAwait(false);
            case GateKind.Review:
                if (_reviewer is null)
                {
                    return GateResult.Pass;
                }

                return await _reviewer(ctx).ConfigureAwait(false);
            case GateKind.Manual:
                var approved = _manualApprover is null || await _manualApprover(ctx).ConfigureAwait(false);
                return approved ? GateResult.Pass : GateResult.Fail("人工卡点未通过");
            default:
                return GateResult.Pass;
        }
    }

    private async Task<GateResult> EvaluateCommandAsync(QualityGate gate, GateEvaluationContext ctx, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gate.Command))
        {
            return GateResult.Pass;
        }

        var (exitCode, stdOut, stdErr) = await RunCommandAsync(gate.Command, ctx.WorkingDirectory, cancellationToken)
            .ConfigureAwait(false);

        // 期望退出码可配置；缺省/解析失败按 0 判定（原行为不变，显式检查 TryParse 返回值以满足 CA1806）
        var expected = int.TryParse(gate.ExpectedExitCode, out var parsedExitCode) ? parsedExitCode : 0;

        if (exitCode != expected)
        {
            return GateResult.Fail(BuildLeftover(gate, $"命令退出码 {exitCode}（期望 {expected}）\n{stdOut}\n{stdErr}"));
        }

        if (!string.IsNullOrWhiteSpace(gate.OutputMatchRegex))
        {
            var matched = Regex.IsMatch(stdOut ?? string.Empty, gate.OutputMatchRegex,
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!matched)
            {
                return GateResult.Fail(BuildLeftover(gate,
                    $"命令输出未匹配正则 {gate.OutputMatchRegex}\n{stdOut}"));
            }
        }

        return GateResult.Pass;
    }

    private static string BuildLeftover(QualityGate gate, string detail)
    {
        if (string.IsNullOrWhiteSpace(gate.FailMessageTemplate))
        {
            return Truncate(detail, 2000);
        }

        return Truncate(gate.FailMessageTemplate.Replace("{stage.output}", detail, StringComparison.Ordinal), 2000);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCommandAsync(
        string command, string workingDirectory, CancellationToken cancellationToken)
    {
        var isWindows = OperatingSystem.IsWindows();
        var fileName = isWindows ? "cmd.exe" : "/bin/sh";
        var arguments = isWindows
            ? $"/c \"{command}\""
            : $"-c \"{command.Replace("\"", "\\\"")}\"";

        return await _processRunner
            .RunAsync(fileName, arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Truncate(string? value, int maxChars) =>
        TextTruncate.Truncate(value, maxChars, "…（已截断）") ?? string.Empty;
}
