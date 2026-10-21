using BatchAgentRunner.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>展开预览任务行。</summary>
public sealed class PreviewTaskRowViewModel : ObservableObject
{
    public PreviewTaskRowViewModel(TaskItem task) => Task = task;

    public TaskItem Task { get; }

    public string Id => Task.Id;

    public string Instruction => Task.Instruction;

    public string DependsText => Task.DependsOn is { Count: > 0 }
        ? "依赖：" + string.Join("、", Task.DependsOn)
        : string.Empty;

    public bool HasDepends => Task.DependsOn is { Count: > 0 };

    public string StageText => Task.IsWorkflow ? $"{Task.Workflow!.Stages.Count} 阶段" : "单指令";

    public string WorkspaceText => string.IsNullOrWhiteSpace(Task.Workspace)
        ? "工作空间：（默认）"
        : $"工作空间：{Task.Workspace}";

    public bool HasWorkspace => !string.IsNullOrWhiteSpace(Task.Workspace);
}
