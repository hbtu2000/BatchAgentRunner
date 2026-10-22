using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Models;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「通知推送」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的通知域）：
/// 规则集合与增删命令。写回时逐条调用 <see cref="NotificationRuleEditor.Apply"/> 生成结构化规则。
/// </summary>
public sealed class NotificationSectionViewModel : ViewModelBase
{
    private ObservableCollection<NotificationRuleEditor> _notificationRules = new();

    /// <summary>通知规则集合，保存时逐条生成结构化规则。</summary>
    public ObservableCollection<NotificationRuleEditor> NotificationRules
    {
        get => _notificationRules;
        set => SetProperty(ref _notificationRules, value);
    }

    /// <summary>点击后追加一条空白通知规则。</summary>
    public IRelayCommand AddNotificationRuleCommand { get; }

    /// <summary>点击后移除指定的通知规则。</summary>
    public IRelayCommand<NotificationRuleEditor> RemoveNotificationRuleCommand { get; }

    /// <summary>初始化通知规则的增删命令。</summary>
    public NotificationSectionViewModel()
    {
        AddNotificationRuleCommand = new RelayCommand(() =>
            NotificationRules.Add(new NotificationRuleEditor(new NotificationRule())));
        RemoveNotificationRuleCommand = new RelayCommand<NotificationRuleEditor>(r =>
        {
            if (r is not null)
            {
                NotificationRules.Remove(r);
            }
        });
    }

    /// <summary>从运行配置加载本域状态。</summary>
    public void LoadFrom(RunOptions o)
    {
        NotificationRules = new ObservableCollection<NotificationRuleEditor>(
            o.Notifications.Select(r => new NotificationRuleEditor(r)));
        OnPropertyChanged(nameof(NotificationRules));
    }

    /// <summary>把本域状态写回运行配置。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.Notifications = NotificationRules.Select(r => r.Apply()).ToList();
    }
}
