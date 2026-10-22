using BatchAgentRunner.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>模板清单行。</summary>
public sealed class TemplateRowViewModel : ObservableObject
{
    /// <summary>以模板定义初始化清单行。</summary>
    /// <param name="spec">模板定义，来自模板存储。</param>
    public TemplateRowViewModel(TemplateSpec spec) => Spec = spec;

    /// <summary>模板定义实体。</summary>
    public TemplateSpec Spec { get; }

    /// <summary>模板标识。</summary>
    public string Id => Spec.Id;

    /// <summary>模板显示名。</summary>
    public string Name => Spec.Name;

    /// <summary>变量数量文案，无变量时显示「无变量」。</summary>
    public string VarSummary => Spec.Vars.Count == 0 ? "无变量" : $"{Spec.Vars.Count} 个变量";

    /// <summary>是否为含阶段的工作流模板（供显隐绑定）。</summary>
    public bool IsWorkflow => Spec.Workflow is { Stages.Count: > 0 };

    /// <summary>模板类型文案（工作流 / 单指令）。</summary>
    public string KindText => IsWorkflow ? "工作流" : "单指令";
}
