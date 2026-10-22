using Avalonia;
using Avalonia.Media;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Helpers;
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

    /// <summary>包装单个阶段结果，供工作流与门禁视图绑定。</summary>
    public StageRowViewModel(StageResult stage)
    {
        _stage = stage;
    }

    /// <summary>被包装的领域阶段结果。</summary>
    public StageResult Stage => _stage;

    /// <summary>阶段标识（如 build / review）。</summary>
    public string StageId => _stage.StageId;

    /// <summary>阶段显示名；未命名时回退为阶段标识。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(_stage.Name) ? _stage.StageId : _stage.Name!;

    /// <summary>阶段当前状态（驱动状态点着色）。</summary>
    public TaskStatus Status => _stage.Status;

    /// <summary>阶段状态的中文文案。</summary>
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

    /// <summary>阶段状态色（P2-10/P2-11：经 AppBrushes 取主题 token，双主题自动切换）。</summary>
    public IBrush StatusBrush => AppBrushes.ForTaskStatus(_stage.Status);

    /// <summary>门禁结果摘要（GateResultView 数据源）。</summary>
    public string GateText => _stage.Status == TaskStatus.Pending
        ? "门禁未运行"
        : _stage.GatePassed ? "门禁通过" : "门禁未过";

    /// <summary>门禁是否已运行且未通过（用于高亮失败行）。</summary>
    public bool GateFailed => _stage.Status != TaskStatus.Pending && !_stage.GatePassed;

    /// <summary>修订循环次数文案；未发生修订时显示「无修订循环」。</summary>
    public string LoopText => _stage.LoopCount > 0 ? $"修订循环 {_stage.LoopCount} 次" : "无修订循环";

    /// <summary>门禁遗留问题清单（门禁未过时有值）。</summary>
    public string? Leftover => _stage.GateLeftover;

    /// <summary>是否存在门禁遗留问题（控制遗留清单可见性）。</summary>
    public bool HasLeftover => !string.IsNullOrWhiteSpace(_stage.GateLeftover);

    /// <summary>阶段输出摘要，可能为 null。</summary>
    public string? OutputSummary => _stage.OutputSummary;

    /// <summary>执行该阶段所用的账号与模型，以「·」连接展示。</summary>
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
    /// <summary>以任务项、拓扑层级与被阻塞依赖创建依赖看板行。</summary>
    public DagRowViewModel(TaskItem item, int level, IReadOnlyList<string> blockedBy)
    {
        Item = item;
        Level = level;
        BlockedBy = blockedBy;
    }

    /// <summary>被展示的领域任务项。</summary>
    public TaskItem Item { get; }

    /// <summary>拓扑层级（0 = 无依赖；每多一层依赖 +1），用于看板缩进。</summary>
    public int Level { get; }

    /// <summary>当前仍在阻断本任务的上游依赖 Id（依赖失败/阻塞/悬空）。</summary>
    public IReadOnlyList<string> BlockedBy { get; }

    /// <summary>任务标识。</summary>
    public string Id => Item.Id;

    /// <summary>任务当前状态。</summary>
    public TaskStatus Status => Item.Status;

    /// <summary>任务状态的中文文案（待命任务标注等依赖）。</summary>
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

    /// <summary>依赖列表文案；无依赖时显示「无依赖」。</summary>
    public string DependsText => Item.DependsOn is { Count: > 0 }
        ? "依赖：" + string.Join("、", Item.DependsOn)
        : "无依赖";

    /// <summary>是否存在上游阻塞（控制阻塞提示可见性）。</summary>
    public bool IsBlockedByVisible => BlockedBy.Count > 0;

    /// <summary>仍在阻断本任务的上游依赖 Id 文案。</summary>
    public string BlockedByText => "被阻塞：" + string.Join("、", BlockedBy);

    /// <summary>工作流徽标文字；非工作流任务为空串。</summary>
    public string WorkflowBadge => Item.IsWorkflow ? "工作流" : string.Empty;
}
