using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>登录进度条目：pending（待办）/ active（进行中）/ done（完成）/ failed（失败）。</summary>
public sealed class LoginStepItem : ObservableObject
{
    private string _state = "pending";

    /// <summary>以步骤标题初始化，初始状态为待办。</summary>
    /// <param name="title">步骤条展示的步骤标题文案。</param>
    public LoginStepItem(string title) => Title = title;

    /// <summary>步骤标题文案（只读，构造后不变）。</summary>
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

    /// <summary>是否处于待办状态（供样式绑定）。</summary>
    public bool IsPending => State == "pending";
    /// <summary>是否处于进行中状态（供样式绑定）。</summary>
    public bool IsActive => State == "active";
    /// <summary>是否已完成（供样式绑定）。</summary>
    public bool IsDone => State == "done";
    /// <summary>是否已失败（供样式绑定）。</summary>
    public bool IsFailed => State == "failed";

    /// <summary>切换步骤状态，并通知四个状态标记刷新。</summary>
    /// <param name="state">目标状态：pending/active/done/failed。</param>
    public void Set(string state) => State = state;
}
