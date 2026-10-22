using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>配置准备度步骤（顶部「配置向导」进度条的一条）。状态：done / current / pending。</summary>
public sealed class ReadinessStep : ObservableObject
{
    /// <summary>创建准备度步骤，指定序号与标题，初始状态为 pending。</summary>
    public ReadinessStep(int number, string title)
    {
        Number = number;
        Title = title;
    }

    /// <summary>步骤序号，从 1 开始，显示在进度条的圆点里。</summary>
    public int Number { get; }

    /// <summary>步骤标题，如「账号」「服务」，显示在序号旁。</summary>
    public string Title { get; }

    private string _state = "pending";

    /// <summary>步骤状态，取 done / current / pending，写入后联动三个布尔属性。</summary>
    public string State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsCurrent));
                OnPropertyChanged(nameof(IsPending));
            }
        }
    }

    /// <summary>状态为 done 时为真，控件据此渲染已完成样式。</summary>
    public bool IsDone => State == "done";

    /// <summary>状态为 current 时为真，标记当前应完成的步骤。</summary>
    public bool IsCurrent => State == "current";

    /// <summary>状态为 pending 时为真，表示该步骤尚未开始。</summary>
    public bool IsPending => State == "pending";
}
