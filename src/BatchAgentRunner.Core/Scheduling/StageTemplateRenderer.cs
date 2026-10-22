using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 阶段指令模板渲染器（纯函数，无副作用）。
/// 支持占位符：{output:stageId}（前序阶段产出）、{var:name}（模板变量）、{task.instruction} / {task.field}（任务字段）。
/// 分隔符兼容冒号与点号（{task:instruction} 与 {task.instruction} 等价）。
/// 未解析占位符保留原样并计入 Unresolved，不阻断执行。
/// </summary>
public sealed class StageTemplateRenderer
{
    private static readonly Regex Placeholder =
        new(@"\{(output|var|task)[:.]([a-zA-Z0-9_\.]+)\}", RegexOptions.Compiled);

    /// <summary>渲染结果。</summary>
    public sealed record RenderResult(string Text, IReadOnlyList<string> Unresolved);

    /// <summary>
    /// 渲染模板：将 <c>{output:…}</c> / <c>{var:…}</c> / <c>{task.…}</c> 占位符替换为上下文取值；
    /// 未解析的占位符原样保留并计入 <see cref="RenderResult.Unresolved"/>，不阻断执行。
    /// </summary>
    public static RenderResult Render(string template, WorkflowRenderContext context)
    {
        if (string.IsNullOrEmpty(template))
        {
            return new RenderResult(string.Empty, Array.Empty<string>());
        }

        var unresolved = new List<string>();
        var text = Placeholder.Replace(template, m =>
        {
            var kind = m.Groups[1].Value;
            var key = m.Groups[2].Value;
            if (context.TryResolve(kind, key, out var value))
            {
                return value;
            }

            unresolved.Add(m.Value);
            return m.Value;
        });

        return new RenderResult(text, unresolved);
    }
}

/// <summary>渲染上下文：前序阶段产出 + 模板变量 + 任务字段。</summary>
public sealed class WorkflowRenderContext
{
    private readonly Dictionary<string, string> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _taskFields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登记前序阶段产出（<c>{output:stageId}</c> 占位符取值，键大小写不敏感）。</summary>
    /// <param name="stageId">阶段 id（占位符 <c>{output:stageId}</c> 的键）。</param>
    /// <param name="output">该阶段产出摘要，同名重复写入按后者覆盖。</param>
    public void SetStageOutput(string stageId, string output) =>
        _outputs[stageId] = output;

    /// <summary>设置模板变量（<c>{var:name}</c> 占位符取值）。</summary>
    /// <param name="name">变量名（大小写不敏感）。</param>
    /// <param name="value">变量替换值。</param>
    public void SetVar(string name, string value) => _vars[name] = value;

    /// <summary>设置任务字段（<c>{task.field}</c> 占位符取值，如 instruction）。</summary>
    /// <param name="name">字段名（大小写不敏感，如 instruction）。</param>
    /// <param name="value">字段值。</param>
    public void SetTaskField(string name, string value) => _taskFields[name] = value;

    /// <summary>
    /// 按占位符类型与键解析取值：kind 为 <c>output</c> / <c>var</c> / <c>task</c> 之一；
    /// 找不到时返回 <c>false</c> 且 <paramref name="value"/> 为空串。
    /// </summary>
    public bool TryResolve(string kind, string key, out string value)
    {
        value = string.Empty;
        return kind switch
        {
            "output" => _outputs.TryGetValue(key, out value!),
            "var" => _vars.TryGetValue(key, out value!),
            "task" => _taskFields.TryGetValue(key, out value!),
            _ => false
        };
    }
}
