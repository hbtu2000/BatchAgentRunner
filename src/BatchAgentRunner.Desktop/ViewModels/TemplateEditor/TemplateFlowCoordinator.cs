using System.Collections.Generic;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// P-05：模板库页「流程示意」构建逻辑收口（从 <see cref="TemplateEditorViewModel"/> 抽离的 Presenter）。
/// 输入 <see cref="TemplateSpec"/>，输出自上而下的执行阶段节点与一句话摘要；纯逻辑、无 UI 状态依赖，
/// 由 VM 仅负责把结果灌入 <see cref="TemplateEditorViewModel.FlowNodes"/> 与 <see cref="TemplateEditorViewModel.FlowSummary"/>。
/// </summary>
public sealed class TemplateFlowCoordinator
{
    /// <summary>构建流程示意：按阶段依赖做拓扑排序，生成自上而下的执行顺序；单指令模板退化为单节点。</summary>
    public static FlowBuildResult BuildFlow(TemplateSpec? spec)
    {
        if (spec is null)
        {
            return new FlowBuildResult(Array.Empty<TemplateFlowNodeViewModel>(), string.Empty);
        }

        if (spec.Workflow is not { Stages.Count: > 0 } wf)
        {
            var single = new TemplateFlowNodeViewModel(
                index: 1,
                stage: null,
                spec: spec,
                consumedBy: Array.Empty<string>(),
                isLast: true);
            var singleSummary = spec.FileGlob is { Count: > 0 }
                ? "单指令模板：每命中一个文件生成一条任务，独立执行、无阶段编排"
                : "单指令模板：一条指令即一个任务，无阶段编排";
            return new FlowBuildResult(new[] { single }, singleSummary);
        }

        var ordered = TopoSortStages(wf.Stages);
        var consumers = BuildOutputConsumers(ordered);

        var nodes = new List<TemplateFlowNodeViewModel>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            nodes.Add(new TemplateFlowNodeViewModel(
                index: i + 1,
                stage: ordered[i],
                spec: spec,
                consumedBy: consumers.TryGetValue(ordered[i].Id, out var by) ? by : Array.Empty<string>(),
                isLast: i == ordered.Count - 1));
        }

        var summary =
            $"{wf.Name}：{ordered.Count} 个阶段顺序执行（{DescribeOnFailure(wf.OnFailure)}，门禁默认最多修订 {wf.DefaultMaxLoops} 轮{(wf.FreshSessionPerStage ? "，每阶段独立会话" : string.Empty)}）";
        return new FlowBuildResult(nodes, summary);
    }

    /// <summary>按 depends_on_stage 做稳定拓扑排序；存在环或悬空依赖时退化为原始顺序。</summary>
    private static List<WorkflowStage> TopoSortStages(List<WorkflowStage> stages)
    {
        var result = new List<WorkflowStage>(stages.Count);
        var known = new HashSet<string>(stages.Select(s => s.Id), StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);

        var remaining = stages.ToList();
        var guard = stages.Count + 1;
        while (remaining.Count > 0 && guard-- > 0)
        {
            var progressed = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var dep = remaining[i].DependsOnStage;
                var ready = string.IsNullOrWhiteSpace(dep)
                    || !known.Contains(dep)
                    || placed.Contains(dep);
                if (!ready)
                {
                    continue;
                }

                result.Add(remaining[i]);
                placed.Add(remaining[i].Id);
                remaining.RemoveAt(i);
                progressed = true;
                i--;
            }

            if (!progressed)
            {
                // 有环：按原顺序补齐，保证界面仍能展示完整流程
                result.AddRange(remaining);
                break;
            }
        }

        return result;
    }

    /// <summary>统计每个阶段的产出被哪些后续阶段以 {output:stageId} 引用。</summary>
    private static Dictionary<string, List<string>> BuildOutputConsumers(List<WorkflowStage> stages)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var producer in stages)
        {
            foreach (var consumer in stages)
            {
                if (string.Equals(producer.Id, consumer.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!consumer.InstructionTemplate.Contains($"{{output:{producer.Id}}}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!map.TryGetValue(producer.Id, out var list))
                {
                    list = new List<string>();
                    map[producer.Id] = list;
                }

                list.Add(consumer.Id);
            }
        }

        return map;
    }

    private static string DescribeOnFailure(WorkflowOnFailure onFailure) => onFailure switch
    {
        WorkflowOnFailure.MarkNeedsAttention => "阶段失败即停并标记待人工接管",
        WorkflowOnFailure.Fail => "阶段失败即停并标记失败",
        WorkflowOnFailure.ContinueNextStage => "阶段失败也继续下一阶段",
        _ => onFailure.ToString()
    };
}

/// <summary><see cref="TemplateFlowCoordinator.BuildFlow"/> 的产物：流程节点列表 + 一句话摘要。</summary>
public sealed record FlowBuildResult(IReadOnlyList<TemplateFlowNodeViewModel> Nodes, string Summary);
