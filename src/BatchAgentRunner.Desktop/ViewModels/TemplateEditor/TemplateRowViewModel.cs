using BatchAgentRunner.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>模板清单行。</summary>
public sealed class TemplateRowViewModel : ObservableObject
{
    public TemplateRowViewModel(TemplateSpec spec) => Spec = spec;

    public TemplateSpec Spec { get; }

    public string Id => Spec.Id;

    public string Name => Spec.Name;

    public string VarSummary => Spec.Vars.Count == 0 ? "无变量" : $"{Spec.Vars.Count} 个变量";

    public bool IsWorkflow => Spec.Workflow is { Stages.Count: > 0 };

    public string KindText => IsWorkflow ? "工作流" : "单指令";
}
