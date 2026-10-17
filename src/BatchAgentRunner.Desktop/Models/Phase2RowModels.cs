using Avalonia;
using Avalonia.Media;
using BatchAgentRunner.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.Models;

/// <summary>
/// 工作流阶段行（二期 §11.2 WorkflowView / GateResultView）：
/// 把 <see cref="StageResult"/> 映射为界面友好的展示属性（状态 / 门禁 / 修订循环 / 遗留问题）。
/// StageResult 本身不可通知，监控页在引擎事件到来时整行重建（与任务行刷新策略一致）。
/// </summary>
public sealed class StageRowViewModel : ObservableObject
{
    private readonly StageResult _stage;

    public StageRowViewModel(StageResult stage)
    {
        _stage = stage;
    }

    public StageResult Stage => _stage;

    public string StageId => _stage.StageId;

    public string DisplayName => string.IsNullOrWhiteSpace(_stage.Name) ? _stage.StageId : _stage.Name!;

    public TaskStatus Status => _stage.Status;

    public string StatusText => _stage.Status switch
    {
        TaskStatus.Pending => "待执行",
        TaskStatus.Running => "执行中",
        TaskStatus.Done => "完成",
        TaskStatus.Failed => "失败",
        TaskStatus.Interrupted => "已中断",
        TaskStatus.NeedsAttention => "待人工介入",
        TaskStatus.Blocked => "已阻塞",
        _ => _stage.Status.ToString()
    };

    public IBrush StatusBrush => _stage.Status switch
    {
        TaskStatus.Pending => new SolidColorBrush(Color.Parse("#8A95A3")),
        TaskStatus.Running => new SolidColorBrush(Color.Parse("#2F6FED")),
        TaskStatus.Done => new SolidColorBrush(Color.Parse("#1F9D55")),
        TaskStatus.Failed => new SolidColorBrush(Color.Parse("#D93025")),
        TaskStatus.Interrupted => new SolidColorBrush(Color.Parse("#B06000")),
        TaskStatus.NeedsAttention => new SolidColorBrush(Color.Parse("#7C3AED")),
        TaskStatus.Blocked => new SolidColorBrush(Color.Parse("#0E7490")),
        _ => new SolidColorBrush(Color.Parse("#8A95A3"))
    };

    /// <summary>门禁结果摘要（GateResultView 数据源）。</summary>
    public string GateText => _stage.Status == TaskStatus.Pending
        ? "门禁未运行"
        : _stage.GatePassed ? "门禁通过" : "门禁未过";

    public bool GateFailed => _stage.Status != TaskStatus.Pending && !_stage.GatePassed;

    public string LoopText => _stage.LoopCount > 0 ? $"修订循环 {_stage.LoopCount} 次" : "无修订循环";

    /// <summary>门禁遗留问题清单（门禁未过时有值）。</summary>
    public string? Leftover => _stage.GateLeftover;

    public bool HasLeftover => !string.IsNullOrWhiteSpace(_stage.GateLeftover);

    public string? OutputSummary => _stage.OutputSummary;

    public string? ExecutionText => string.Join(" · ", new[]
    {
        string.IsNullOrEmpty(_stage.AccountUsed) ? null : _stage.AccountUsed,
        string.IsNullOrEmpty(_stage.ModelUsed) ? null : _stage.ModelUsed
    }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>
/// 依赖看板行（二期 §11.2 DagBoardView）：任务按拓扑层级缩进展示，
/// 悬停/内联展示 depends_on 与 blocked_by。
/// </summary>
public sealed class DagRowViewModel : ObservableObject
{
    public DagRowViewModel(TaskItem item, int level, IReadOnlyList<string> blockedBy)
    {
        Item = item;
        Level = level;
        BlockedBy = blockedBy;
    }

    public TaskItem Item { get; }

    /// <summary>拓扑层级（0 = 无依赖；每多一层依赖 +1），用于看板缩进。</summary>
    public int Level { get; }

    /// <summary>当前仍在阻断本任务的上游依赖 Id（依赖失败/阻塞/悬空）。</summary>
    public IReadOnlyList<string> BlockedBy { get; }

    public string Id => Item.Id;

    public TaskStatus Status => Item.Status;

    public string StatusText => Item.Status switch
    {
        TaskStatus.Pending => Item.DependsOn is { Count: > 0 } ? "待命（等依赖）" : "待处理",
        TaskStatus.Running => "执行中",
        TaskStatus.Done => "完成",
        TaskStatus.Failed => "失败",
        TaskStatus.Interrupted => "已中断",
        TaskStatus.NeedsAttention => "待人工介入",
        TaskStatus.Blocked => "依赖阻塞",
        _ => Item.Status.ToString()
    };

    /// <summary>看板缩进（每层 20px）。</summary>
    public Thickness Indent => new(Level * 20, 0, 0, 0);

    public string DependsText => Item.DependsOn is { Count: > 0 }
        ? "依赖：" + string.Join("、", Item.DependsOn)
        : "无依赖";

    public bool IsBlockedByVisible => BlockedBy.Count > 0;

    public string BlockedByText => "被阻塞：" + string.Join("、", BlockedBy);

    public string WorkflowBadge => Item.IsWorkflow ? "工作流" : string.Empty;
}
