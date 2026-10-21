using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Options;

/// <summary>① 任务文件与执行工具（PRD 第 9 章配置域 1）。</summary>
public sealed partial class RunOptions
{
    [JsonPropertyName("task_file_path")]
    public string TaskFilePath { get; set; } = string.Empty;

    [JsonPropertyName("agent_kind")]
    public AgentKind AgentKind { get; set; } = AgentKind.WorkBuddy;

    /// <summary>CLI 可执行文件路径/命令；为空则使用适配器内置默认。</summary>
    [JsonPropertyName("cli_path")]
    public string? CliPath { get; set; }

    /// <summary>
    /// CLI 参数模板（M0 V-1/V-2 未验证前外置，避免硬编码猜测）。
    /// 占位符：{prompt} {model} {expert} {workspace} {extra}
    /// </summary>
    [JsonPropertyName("cli_arguments_template")]
    public string? CliArgumentsTemplate { get; set; }

    /// <summary>
    /// 模型推理强度（作为 <c>--effort</c> 传给 CLI）：<c>minimal</c> / <c>low</c> / <c>medium</c> / <c>high</c> / <c>xhigh</c> / <c>max</c>。
    /// <para>背景（2026-10-22 实测 CLI 日志）：CLI 默认按 <c>high</c> 推理，单轮首字延迟中位 4.7s、生成约 6.6s，
    /// 一个 200+ 轮的大任务仅模型侧就要 40 分钟以上。故默认取 <c>medium</c> 兼顾速度与质量；
    /// 架构判断类任务可临时调回 <c>high</c>。</para>
    /// <para>别名 <c>middle</c>/<c>mid</c>/<c>中</c>/<c>中等</c> 均按 <c>medium</c> 处理，未知值回落 <c>medium</c>；
    /// 留空 = 不追加该参数（完全走 CLI 默认值）。取值口径见 <see cref="ReasoningEfforts"/>。</para>
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; } = ReasoningEfforts.Default;

    /// <summary>
    /// 执行任务时是否弹出 CLI 原生控制台窗口（默认 <c>false</c> = 后台无窗口运行）。
    /// <para>背景（2026-10-16）：CLI 调用形态为 <c>-p/--print --output-format stream-json</c>，
    /// 后台模式下应用能捕获 stdout 并渲染成实时可读进度（见 <c>StreamJsonRenderer</c>），
    /// 同时自动应答、危险操作拦截、结果回写与鉴权/额度分类全部正常工作。</para>
    /// <para>置 <c>true</c> 仅作排障开关：进程不重定向 stdio、以真实 TTY 运行，窗口里能看到 stream-json 文本（不再是纯黑）；
    /// 但代价是应用侧读不到输出 —— 结果回写/鉴权/额度分类失效、应用内实时区空白，需人工值守窗口。
    /// 不建议在常规批量任务中开启。</para>
    /// </summary>
    [JsonPropertyName("show_cli_window")]
    public bool ShowCliWindow { get; set; }
}
