using System.ComponentModel;
using Avalonia.Media;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.Models;

/// <summary>任务列表行：把领域任务项映射为界面友好的展示属性。</summary>
/// <remarks>
/// P1-7：行 VM 订阅长生命周期 <see cref="TaskItem"/> 的 <see cref="TaskItem.PropertyChanged"/> 以驱动自身刷新，
/// 但 <see cref="TaskItem"/> 由引擎持有、贯穿整轮批处理，若不显式退订会把已被 <c>MonitorViewModel.Tasks</c>
/// 移除的旧行 VM 永久拽在内存里（每次 <c>SyncTasks</c> 重建都会泄漏一批）。故实现 <see cref="IDisposable"/>，
/// 由宿主在行被移出集合时调用 <see cref="Dispose"/> 解除订阅。
/// </remarks>
public sealed class TaskRowViewModel : ObservableObject, IDisposable
{
    private readonly PropertyChangedEventHandler _itemChangedHandler;
    private bool _disposed;

    /// <summary>绑定领域任务项并订阅其属性变更以驱动行刷新。</summary>
    public TaskRowViewModel(TaskItem item)
    {
        Item = item;
        _itemChangedHandler = (_, _) => Refresh();
        item.PropertyChanged += _itemChangedHandler;
    }

    /// <summary>
    /// 解除对本行所绑定 <see cref="TaskItem"/> 的订阅，避免长生命周期领域对象永久持有本行 VM（P1-7）。
    /// 幂等：重复调用安全。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Item.PropertyChanged -= _itemChangedHandler;
    }

    /// <summary>本行对应的领域任务项。</summary>
    public TaskItem Item { get; }

    /// <summary>任务标识。</summary>
    public string Id => Item.Id;

    /// <summary>任务指令（P2-7：详情面板经行 VM 读取，XAML 不得直绑领域对象 <see cref="Item"/>）。</summary>
    public string Instruction => Item.Instruction;

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

    /// <summary>任务状态枚举（供 StatusDotHelper.TaskStatus 附加属性按状态着色，双主题）。</summary>
    public TaskStatus Status => Item.Status;

    /// <summary>任务状态色（P2-10/P2-11：经 AppBrushes 取主题 token，双主题自动切换）。</summary>
    public IBrush StatusBrush => AppBrushes.ForTaskStatus(Item.Status);

    /// <summary>是否工作流任务（详情面板切换阶段树展示）。</summary>
    public bool IsWorkflow => Item.IsWorkflow;

    /// <summary>依赖摘要（DAG 任务列表行内展示）。</summary>
    public string DependsText => Item.DependsOn is { Count: > 0 }
        ? "依赖：" + string.Join("、", Item.DependsOn)
        : string.Empty;

    /// <summary>任务是否声明了上游依赖（控制依赖徽标可见性）。</summary>
    public bool HasDepends => Item.DependsOn is { Count: > 0 };

    /// <summary>工作流阶段数徽标（非工作流任务为空串）。</summary>
    public string StageBadge => Item.IsWorkflow ? $"{Item.Workflow!.Stages.Count} 阶段" : string.Empty;

    /// <summary>执行信息摘要：账号 / 模型 / 兜底标记 / 工作空间。</summary>
    public string ExecutionInfo =>
        string.Join(" · ", new[]
        {
            string.IsNullOrEmpty(Item.AccountUsed) ? null : Item.AccountUsed,
            string.IsNullOrEmpty(Item.ModelUsed) ? null : Item.ModelUsed,
            Item.FallbackUsed ? "兜底" : null,
            string.IsNullOrEmpty(Item.Workspace) ? null : Item.Workspace
        }.Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>所属工作空间（REQ-020 监控：每行展示工作空间，便于校验「同空间未并行」）。</summary>
    public string Workspace => string.IsNullOrEmpty(Item.Workspace) ? "(默认)" : Item.Workspace;

    /// <summary>执行耗时文案；未记录耗时显示「-」。</summary>
    public string DurationText => Item.DurationSeconds > 0 ? $"{Item.DurationSeconds:0.#}s" : "-";

    /// <summary>重试次数文案；未重试时为空串。</summary>
    public string RetryText => Item.RetryCount > 0 ? $"重试 {Item.RetryCount}" : "";

    /// <summary>任务是否记录了错误信息（控制错误提示可见性）。</summary>
    public bool HasError => !string.IsNullOrEmpty(Item.Error);

    /// <summary>通知界面本行全部派生属性已变化（领域对象变更时调用）。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Instruction));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ExecutionInfo));
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(RetryText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsWorkflow));
        OnPropertyChanged(nameof(DependsText));
        OnPropertyChanged(nameof(HasDepends));
        OnPropertyChanged(nameof(StageBadge));
        OnPropertyChanged(nameof(Item));
    }
}

/// <summary>界面提示（切换 / 兜底 / 时段 / 冷却 / 危险操作）。</summary>
public sealed class ToastItem
{
    /// <summary>以级别、正文与时间创建一条界面轻提示。</summary>
    public ToastItem(string level, string message, DateTimeOffset at)
    {
        Level = level;
        Message = message;
        At = at;
    }

    /// <summary>提示级别（danger / warning，其余按 info 着色）。</summary>
    public string Level { get; }

    /// <summary>提示正文。</summary>
    public string Message { get; }

    /// <summary>提示产生的时间。</summary>
    public DateTimeOffset At { get; }

    /// <summary>时间的 HH:mm:ss 文本。</summary>
    public string TimeText => At.ToString("HH:mm:ss");

    /// <summary>按提示级别取主题色（双主题自动切换）。</summary>
    public IBrush LevelBrush => AppBrushes.ForLevel(Level);
}
