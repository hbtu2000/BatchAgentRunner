using BatchAgentRunner.Core.Abstractions;

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

        return t
            .Replace(PlaceholderPrompt, Quote(request.Instruction))
            .Replace(PlaceholderModel, request.Model.Name)
            .Replace(PlaceholderExpert, expertArg ?? string.Empty)
            .Replace(PlaceholderWorkspace, Quote(request.WorkingDirectory))
            .Replace(PlaceholderExtra, extra ?? string.Empty);
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
        $"--print {PlaceholderPrompt} --model {PlaceholderModel} --output-format stream-json {PlaceholderExtra}";

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
        $"-p {PlaceholderPrompt} --model {PlaceholderModel} --output-format stream-json {PlaceholderExtra}";

    public static string Quote(string value)
    {
        var escaped = value.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
