using System.ComponentModel;
using Avalonia.Media;
using BatchAgentRunner.Core.Domain;
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

    public TaskItem Item { get; }

    public string Id => Item.Id;

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

    public IBrush StatusBrush => Item.Status switch
    {
        TaskStatus.Pending => new SolidColorBrush(Color.Parse("#8A8F98")),
        TaskStatus.Running => new SolidColorBrush(Color.Parse("#2F6FED")),
        TaskStatus.Done => new SolidColorBrush(Color.Parse("#1F9D55")),
        TaskStatus.Failed => new SolidColorBrush(Color.Parse("#D93025")),
        TaskStatus.Interrupted => new SolidColorBrush(Color.Parse("#B06000")),
        TaskStatus.NeedsAttention => new SolidColorBrush(Color.Parse("#7C3AED")),
        TaskStatus.Blocked => new SolidColorBrush(Color.Parse("#0E7490")),
        _ => new SolidColorBrush(Color.Parse("#8A8F98"))
    };

    /// <summary>是否工作流任务（详情面板切换阶段树展示）。</summary>
    public bool IsWorkflow => Item.IsWorkflow;

    /// <summary>依赖摘要（DAG 任务列表行内展示）。</summary>
    public string DependsText => Item.DependsOn is { Count: > 0 }
        ? "依赖：" + string.Join("、", Item.DependsOn)
        : string.Empty;

    public bool HasDepends => Item.DependsOn is { Count: > 0 };

    /// <summary>工作流阶段数徽标（非工作流任务为空串）。</summary>
    public string StageBadge => Item.IsWorkflow ? $"{Item.Workflow!.Stages.Count} 阶段" : string.Empty;

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

    public string DurationText => Item.DurationSeconds > 0 ? $"{Item.DurationSeconds:0.#}s" : "-";

    public string RetryText => Item.RetryCount > 0 ? $"重试 {Item.RetryCount}" : "";

    public bool HasError => !string.IsNullOrEmpty(Item.Error);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Id));
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
    public ToastItem(string level, string message, DateTimeOffset at)
    {
        Level = level;
        Message = message;
        At = at;
    }

    public string Level { get; }

    public string Message { get; }

    public DateTimeOffset At { get; }

    public string TimeText => At.ToString("HH:mm:ss");

    public IBrush LevelBrush => Level switch
    {
        "danger" => new SolidColorBrush(Color.Parse("#D93025")),
        "warning" => new SolidColorBrush(Color.Parse("#B06000")),
        _ => new SolidColorBrush(Color.Parse("#2F6FED"))
    };
}
