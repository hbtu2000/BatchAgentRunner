using System.Collections.Generic;
using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Text;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 流程示意节点（二期 UI）：一个节点 = 一个执行阶段（单指令模板则为一个虚拟节点）。
/// 节点之间的连线由视图根据 <see cref="IsLast"/> 绘制成箭头。
/// </summary>
public sealed class TemplateFlowNodeViewModel
{
    private static readonly Regex PlaceholderRx = new(
        @"\{(output:[^}]+|var:[^}]+|task\.[^}]+)\}",
        RegexOptions.Compiled);

    /// <summary>以阶段、模板与引用关系构建一个流程示意节点。</summary>
    public TemplateFlowNodeViewModel(
        int index,
        WorkflowStage? stage,
        TemplateSpec spec,
        IReadOnlyList<string> consumedBy,
        bool isLast)
    {
        Index = index;
        Stage = stage;
        IsLast = isLast;
        IsSingleStep = stage is null;

        var template = stage?.InstructionTemplate ?? spec.InstructionTemplate;
        Title = stage is null
            ? spec.Name
            : string.IsNullOrWhiteSpace(stage.Name) ? stage.Id : stage.Name;
        Template = string.IsNullOrWhiteSpace(template) ? "（未填写指令模板）" : template;
        Placeholders = PlaceholderRx.Matches(Template).Select(m => m.Value).Distinct().ToArray();
        ExecutorText = BuildExecutorText(stage);
        GateText = BuildGateText(stage);
        LoopText = stage is null
            ? string.Empty
            : $"最多 {stage.MaxLoops ?? (spec.Workflow?.DefaultMaxLoops ?? 3)} 轮修订（{DescribeStageGateFail(stage.OnGateFail)}）";
        DependsText = string.IsNullOrWhiteSpace(stage?.DependsOnStage)
            ? string.Empty
            : $"依赖阶段：{stage!.DependsOnStage}";
        ConsumedByText = consumedBy.Count == 0
            ? string.Empty
            : $"产出被引用：{string.Join("、", consumedBy)}";
    }

    /// <summary>节点在流程中的序号（从 1 开始）。</summary>
    public int Index { get; }

    /// <summary>对应的执行阶段；单指令模板的虚拟节点为 null。</summary>
    public WorkflowStage? Stage { get; }

    /// <summary>是否为最后一个节点（视图据此不绘箭头）。</summary>
    public bool IsLast { get; }

    /// <summary>单指令模板的虚拟节点（无门禁/执行器概念）。</summary>
    public bool IsSingleStep { get; }

    /// <summary>序号的文本形式，供界面绑定。</summary>
    public string IndexText => Index.ToString();

    /// <summary>节点标题：阶段名，单指令模板则用模板名。</summary>
    public string Title { get; }

    /// <summary>节点展示的指令模板正文（缺失时给占位文案）。</summary>
    public string Template { get; }

    /// <summary>模板中出现的占位符列表（已去重）。</summary>
    public IReadOnlyList<string> Placeholders { get; }

    /// <summary>执行器摘要文案，未配置时为空串。</summary>
    public string ExecutorText { get; }

    /// <summary>门禁摘要文案，未配置门禁时为空串。</summary>
    public string GateText { get; }

    /// <summary>修订轮数摘要文案，单指令节点为空串。</summary>
    public string LoopText { get; }

    /// <summary>依赖阶段文案，无依赖时为空串。</summary>
    public string DependsText { get; }

    /// <summary>产出被引用文案，无人引用时为空串。</summary>
    public string ConsumedByText { get; }

    /// <summary>模板是否含占位符（供显隐绑定）。</summary>
    public bool HasPlaceholders => Placeholders.Count > 0;

    /// <summary>是否存在依赖阶段（供显隐绑定）。</summary>
    public bool HasDepends => DependsText.Length > 0;

    /// <summary>产出是否被后续阶段引用（供显隐绑定）。</summary>
    public bool HasConsumedBy => ConsumedByText.Length > 0;

    /// <summary>是否展示修订轮数（供显隐绑定）。</summary>
    public bool HasLoop => LoopText.Length > 0;

    /// <summary>是否需要绘制与下一节点的连线。</summary>
    public bool HasConnector => !IsLast;

    /// <summary>是否有执行器信息可展示。</summary>
    public bool HasExecutor => ExecutorText.Length > 0;

    /// <summary>是否有门禁信息可展示。</summary>
    public bool HasGate => GateText.Length > 0;

    private static string BuildExecutorText(WorkflowStage? stage)
    {
        var ex = stage?.Executor;
        if (ex is null)
        {
            return stage is null ? string.Empty : "执行器：继承全局配置";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ex.AccountId))
        {
            parts.Add($"账号 {ex.AccountId}");
        }

        if (!string.IsNullOrWhiteSpace(ex.Model))
        {
            parts.Add($"模型 {ex.Model}");
        }

        if (!string.IsNullOrWhiteSpace(ex.ExpertId))
        {
            parts.Add($"专家 {ex.ExpertId}");
        }

        if (!string.IsNullOrWhiteSpace(ex.Workspace))
        {
            parts.Add($"工作空间 {ex.Workspace}");
        }

        return parts.Count == 0 ? string.Empty : "执行器：" + string.Join(" · ", parts);
    }

    private static string BuildGateText(WorkflowStage? stage)
    {
        if (stage?.Gate is not { } gate)
        {
            return stage is null ? string.Empty : "门禁：无";
        }

        var kind = gate.Kind switch
        {
            GateKind.Command => "命令",
            GateKind.Review => "审查",
            GateKind.Manual => "人工",
            _ => gate.Kind.ToString()
        };

        var detail = gate.Kind switch
        {
            GateKind.Command when !string.IsNullOrWhiteSpace(gate.Command) =>
                $"{gate.Command}（期望退出码 {gate.ExpectedExitCode ?? "0"}）",
            GateKind.Review when !string.IsNullOrWhiteSpace(gate.ReviewChecklist) =>
                Shorten(gate.ReviewChecklist, 60),
            GateKind.Manual => gate.ManualApproval ? "需人工确认" : "自动通过",
            _ => string.Empty
        };

        return string.IsNullOrEmpty(detail) ? $"门禁：{kind}" : $"门禁：{kind} · {detail}";
    }

    private static string DescribeStageGateFail(StageOnGateFail onGateFail) => onGateFail switch
    {
        StageOnGateFail.FixLoop => "不通过则修订重跑",
        StageOnGateFail.StopStage => "不通过则停止本阶段",
        StageOnGateFail.MarkNeedsAttention => "不通过则标记待人工接管",
        _ => onGateFail.ToString()
    };

    private static string Shorten(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return TextTruncate.Truncate(flat, max, "…")!;
    }
}
