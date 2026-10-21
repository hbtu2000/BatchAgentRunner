using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Helpers;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 批次运行状态可绑定状态（P-06 分解产物）。
/// 原 <c>MonitorViewModel</c> 的批次状态属性（Status / StatusText / StatusBrush / IsWaiting /
/// DispatchPaused / Can*）由引擎事件一处驱动；分解后由本类型持有（facade 同名属性转发到此）。
/// <see cref="Status"/> 变更时级联通知派生属性，命令可用性由 facade 监听
/// <see cref="Status"/> 变更后统一 <c>NotifyCanExecuteChanged</c>（与原实现等价）。
/// </summary>
internal sealed class BatchStatusState : ViewModelBase
{
    private readonly IEngineHost _host;

    private BatchStatus _status = BatchStatus.Idle;
    private bool _dispatchPaused;

    public BatchStatusState(IEngineHost host)
    {
        _host = host;
    }

    public BatchStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(IsWaiting));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanTerminate));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanCancelWaiting));
            }
        }
    }

    public string StatusText => _status switch
    {
        BatchStatus.Idle => "空闲",
        BatchStatus.Running => "运行中",
        BatchStatus.Waiting => "等待恢复",
        BatchStatus.Blocked => "已阻塞",
        BatchStatus.Terminated => "已终止",
        BatchStatus.Completed => "已完成",
        _ => _status.ToString()
    };

    public Avalonia.Media.IBrush StatusBrush => AppBrushes.ForBatchStatus(_status);

    public bool IsWaiting => _status == BatchStatus.Waiting;

    /// <summary>
    /// 派发暂停提示（REQ-021）：任一在飞槽位冻结后进入全局派发暂停（不再向任何槽位派发新任务），
    /// 直到在飞全部结束后统一切换账号并解除。监控页据此展示「派发已暂停」横幅。
    /// </summary>
    public bool DispatchPaused
    {
        get => _dispatchPaused;
        set => SetProperty(ref _dispatchPaused, value);
    }

    public bool CanStart => !_host.IsRunning && !string.IsNullOrWhiteSpace(_host.Options.TaskFilePath);

    public bool CanTerminate => _host.IsRunning;

    public bool CanResume => !_host.IsRunning && !string.IsNullOrWhiteSpace(_host.Options.TaskFilePath);

    public bool CanCancelWaiting => _status == BatchStatus.Waiting;
}
