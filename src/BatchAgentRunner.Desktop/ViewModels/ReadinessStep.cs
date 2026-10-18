using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>配置准备度步骤（顶部「配置向导」进度条的一条）。状态：done / current / pending。</summary>
public sealed class ReadinessStep : ObservableObject
{
    public ReadinessStep(int number, string title)
    {
        Number = number;
        Title = title;
    }

    public int Number { get; }

    public string Title { get; }

    private string _state = "pending";

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

    public bool IsDone => State == "done";

    public bool IsCurrent => State == "current";

    public bool IsPending => State == "pending";
}
