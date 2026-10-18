using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>门禁评估结果。</summary>
public sealed record GateResult(bool Passed, string? Leftover = null)
{
    public static readonly GateResult Pass = new(true);
    public static GateResult Fail(string? leftover = null) => new(false, leftover);
}

/// <summary>门禁评估上下文。</summary>
public sealed class GateEvaluationContext
{
    public QualityGate Gate { get; init; } = new();
    public string StageOutput { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string StageId { get; init; } = string.Empty;
    public string? AccountUsed { get; init; }
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
    private readonly Func<GateEvaluationContext, Task<GateResult>>? _reviewer;
    private readonly Func<GateEvaluationContext, Task<bool>>? _manualApprover;

    public GateEvaluator(
        Func<GateEvaluationContext, Task<GateResult>>? reviewer = null,
        Func<GateEvaluationContext, Task<bool>>? manualApprover = null)
    {
        _reviewer = reviewer;
        _manualApprover = manualApprover;
    }

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

    private static async Task<GateResult> EvaluateCommandAsync(QualityGate gate, GateEvaluationContext ctx, CancellationToken cancellationToken)
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

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCommandAsync(
        string command, string workingDirectory, CancellationToken cancellationToken)
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? $"/c \"{command}\"" : $"-c \"{command.Replace("\"", "\\\"")}\"",
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
            try { process.Kill(true); } catch { /* 忽略 */ }
            throw;
        }

        return (process.ExitCode, outSb.ToString(), errSb.ToString());
    }

    private static string Truncate(string? value, int maxChars) =>
        value is null || value.Length <= maxChars ? value ?? string.Empty : value[..maxChars] + "…（已截断）";
}
