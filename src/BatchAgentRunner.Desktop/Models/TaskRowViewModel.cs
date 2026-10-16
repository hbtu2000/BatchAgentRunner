using Avalonia.Media;
using BatchAgentRunner.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.Models;

/// <summary>任务列表行：把领域任务项映射为界面友好的展示属性。</summary>
public sealed class TaskRowViewModel : ObservableObject
{
    public TaskRowViewModel(TaskItem item)
    {
        Item = item;
        item.PropertyChanged += (_, _) => Refresh();
    }

    public TaskItem Item { get; }

    public string Id => Item.Id;

    public string StatusText => Item.Status switch
    {
        TaskStatus.Pending => "待处理",
        TaskStatus.Running => "执行中",
        TaskStatus.Done => "完成",
        TaskStatus.Failed => "失败",
        TaskStatus.Interrupted => "已中断",
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
        _ => new SolidColorBrush(Color.Parse("#8A8F98"))
    };

    public string ExecutionInfo =>
        string.Join(" · ", new[]
        {
            string.IsNullOrEmpty(Item.AccountUsed) ? null : Item.AccountUsed,
            string.IsNullOrEmpty(Item.ModelUsed) ? null : Item.ModelUsed,
            Item.FallbackUsed ? "兜底" : null,
            string.IsNullOrEmpty(Item.Workspace) ? null : Item.Workspace
        }.Where(x => !string.IsNullOrEmpty(x)));

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
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(RetryText));
        OnPropertyChanged(nameof(HasError));
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
