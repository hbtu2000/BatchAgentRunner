using System.Text;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>
/// CLI 参数拼装。占位符：<c>{prompt}</c> <c>{model}</c> <c>{expert}</c> <c>{workspace}</c> <c>{extra}</c>。
/// 真实 CLI 的无人值守参数与模型参数待 M0 V-1 / V-2 验证，故默认模板外置可配（RunOptions.CliArgumentsTemplate）。
/// </summary>
public static class CliCommandBuilder
{
    public const string PlaceholderPrompt = "{prompt}";
    public const string PlaceholderModel = "{model}";
    public const string PlaceholderExpert = "{expert}";
    public const string PlaceholderWorkspace = "{workspace}";
    public const string PlaceholderExtra = "{extra}";

    public static string Build(string? template, AgentRequest request, string? extra = null)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var expertArg = request.Expert.IsSpecified
            ? request.Expert.Id
            : string.Empty;

        var built = t
            .Replace(PlaceholderPrompt, Quote(NormalizeToSingleLine(request.Instruction)))
            .Replace(PlaceholderModel, request.Model.Name)
            .Replace(PlaceholderExpert, expertArg ?? string.Empty)
            .Replace(PlaceholderWorkspace, Quote(NormalizeToSingleLine(request.WorkingDirectory)))
            .Replace(PlaceholderExtra, extra ?? string.Empty);

        // 兜底：模板或 extra 里若带入换行，同样会被 cmd.exe 截断（见 NormalizeToSingleLine 说明）
        return built.IndexOfAny(['\r', '\n']) >= 0 ? NormalizeToSingleLine(built) : built;
    }

    /// <summary>
    /// 把任意文本压成单行（换行/制表/其它控制字符折叠为单个空格）。
    /// </summary>
    /// <remarks>
    /// 2026-10-22 事故根因：CLI 可执行文件是 npm/.cmd shim（如 <c>codebuddy.cmd</c>）时，Windows 会经
    /// <c>cmd.exe /c "…"</c> 转交命令行，而 <b>cmd.exe 把命令行里的 CR/LF 当作命令分隔符</b> —— 换行之后的
    /// 参数（<c>--model</c> / <c>--output-format stream-json</c> / <c>--include-partial-messages</c> / <c>--port</c>）
    /// 被整体丢弃，CLI 于是退回交互模式（<c>codebuddy ps</c> 显示 kind=interactive）、完全不写 stream-json stdout，
    /// 表现为「任务在跑、文件在改，但应用内实时输出流一片空白」。
    /// 实测证据：xlsx 单元格内带换行的任务指令，使 node 子进程命令行在换行处被截断（只剩 <c>-p "…严格构建</c>）。
    /// 提示词层面的行内空白折叠对 CLI 语义无影响，故统一在此收口。
    /// </remarks>
    public static string NormalizeToSingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>默认模板（保守取「非交互打印结果」形态，M0 实测后校准）。</summary>
    public static string DefaultTemplate => $"--print {PlaceholderPrompt} --model {PlaceholderModel} {PlaceholderExtra}";

    /// <summary>
    /// WorkBuddy 形态。2026-10-15 实测（codebuddy 2.167.0）：WorkBuddy 品牌无独立 CLI，
    /// 同用 codebuddy 二进制；<c>--expert</c> 为非法参数（error: unknown option），
    /// 专家诉求按 PRD 降级走 prompt 注入。
    /// 同步采用 <c>--output-format stream-json</c>（理由同 <see cref="CodeBuddyTemplate"/>）。
    /// </summary>
    public static string WorkBuddyTemplate =>
        $"--print {PlaceholderPrompt} --model {PlaceholderModel} --output-format stream-json --include-partial-messages {PlaceholderExtra}";

    /// <summary>
    /// CodeBuddy 形态。
    /// 2026-10-16 修正：原 <c>--output-format text</c> 在 <c>-p/--print</c> 下「只打印最终回答并退出」，
    /// 执行全程不写 stdout —— 非窗口模式（应用内实时流）与窗口模式（真实 TTY）都看不到任何过程，
    /// 表现为「CLI 窗口一片黑 / 应用内实时区空白」。
    /// 改为 <c>--output-format stream-json</c>：CLI 实时输出逐行 JSON 事件（assistant / user / result），
    /// 既能在后台（非窗口）被应用捕获并渲染成可读进度，也能在窗口模式里至少显示文本（不再是纯黑）。
    /// 解析见 <see cref="StreamJsonRenderer"/>。
    /// </summary>
    public static string CodeBuddyTemplate =>
        $"-p {PlaceholderPrompt} --model {PlaceholderModel} --output-format stream-json --include-partial-messages {PlaceholderExtra}";

    /// <summary>
    /// 追加 <c>--effort &lt;level&gt;</c>。命令行里已有 <c>--effort</c>（用户在自定义模板里手写）时以用户为准，不重复追加。
    /// 取值口径见 <see cref="ReasoningEfforts"/>。
    /// </summary>
    public static string AppendEffort(string arguments, string? raw)
    {
        var effort = ReasoningEfforts.Normalize(raw);
        if (effort is null)
        {
            return arguments;
        }

        if (arguments.Contains("--effort", StringComparison.Ordinal))
        {
            return arguments;
        }

        return arguments + " --effort " + effort;
    }

    public static string Quote(string value)
    {
        var escaped = value.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
