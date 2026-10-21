using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>登录进度条目：pending（待办）/ active（进行中）/ done（完成）/ failed（失败）。</summary>
public sealed class LoginStepItem : ObservableObject
{
    private string _state = "pending";

    public LoginStepItem(string title) => Title = title;

    public string Title { get; }

    private string State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    public bool IsPending => State == "pending";
    public bool IsActive => State == "active";
    public bool IsDone => State == "done";
    public bool IsFailed => State == "failed";

    public void Set(string state) => State = state;
}
