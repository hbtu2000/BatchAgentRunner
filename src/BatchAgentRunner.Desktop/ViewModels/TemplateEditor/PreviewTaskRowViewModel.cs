using BatchAgentRunner.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>展开预览任务行。</summary>
public sealed class PreviewTaskRowViewModel : ObservableObject
{
    /// <summary>以展开后的任务项初始化预览行。</summary>
    /// <param name="task">展开产物中的单条任务。</param>
    public PreviewTaskRowViewModel(TaskItem task) => Task = task;

    /// <summary>被预览的任务实体。</summary>
    public TaskItem Task { get; }

    /// <summary>任务标识。</summary>
    public string Id => Task.Id;

    /// <summary>任务的指令正文。</summary>
    public string Instruction => Task.Instruction;

    /// <summary>依赖任务的展示文案，无依赖时为空串。</summary>
    public string DependsText => Task.DependsOn is { Count: > 0 }
        ? "依赖：" + string.Join("、", Task.DependsOn)
        : string.Empty;

    /// <summary>是否存在依赖任务（供显隐绑定）。</summary>
    public bool HasDepends => Task.DependsOn is { Count: > 0 };

    /// <summary>阶段数量文案；非工作流则显示「单指令」。</summary>
    public string StageText => Task.IsWorkflow ? $"{Task.Workflow!.Stages.Count} 阶段" : "单指令";

    /// <summary>工作空间展示文案，未指定时提示默认。</summary>
    public string WorkspaceText => string.IsNullOrWhiteSpace(Task.Workspace)
        ? "工作空间：（默认）"
        : $"工作空间：{Task.Workspace}";

    /// <summary>是否显式指定了工作空间（供显隐绑定）。</summary>
    public bool HasWorkspace => !string.IsNullOrWhiteSpace(Task.Workspace);
}
