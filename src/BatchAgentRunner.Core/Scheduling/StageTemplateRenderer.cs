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

    public void SetStageOutput(string stageId, string output) =>
        _outputs[stageId] = output;

    public void SetVar(string name, string value) => _vars[name] = value;

    public void SetTaskField(string name, string value) => _taskFields[name] = value;

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
