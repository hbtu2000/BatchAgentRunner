using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Desktop.Models;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 执行监控页（主页面）：总进度 + 状态统计卡 + 任务列表 + 实时输出流 + 提示区。
/// 引擎事件驱动刷新（≤2 秒），非轮询。
/// </summary>
public sealed class MonitorViewModel : ViewModelBase
{
    private readonly EngineHost _host;
    private readonly DispatcherTimer _timer;

    private TaskRowViewModel? _selectedTask;
    private BatchStatus _status = BatchStatus.Idle;
    private int _total;
    private int _done;
    private int _failed;
    private int _running;
    private int _interrupted;
    private int _pending;
    private int _skipped;
    private string _currentTaskText = "无";
    private string _currentComboText = "-";
    private string _currentWorkspaceText = "-";
    private DateTimeOffset? _resumeAt;
    private string _waitingText = string.Empty;
    private bool _forceRerun;
    private bool _waitIfUnavailable = true;
    private string _statusMessage = string.Empty;

    public MonitorViewModel(EngineHost host)
    {
        _host = host;
        Tasks = new ObservableCollection<TaskRowViewModel>();
        Toasts = new ObservableCollection<ToastItem>();
        OutputLines = new ObservableCollection<string>();

        _host.EventRaised += OnEngineEvent;
        _host.TasksChanged += (_, _) => Dispatcher.UIThread.Post(SyncTasks);

        StartCommand = new AsyncRelayCommand(StartAsync, () => !_host.IsRunning && !string.IsNullOrWhiteSpace(_host.Options.TaskFilePath));
        TerminateCommand = new AsyncRelayCommand(() => _host.TerminateAsync(false), () => _host.IsRunning);
        TerminateAfterCurrentCommand = new AsyncRelayCommand(() => _host.TerminateAsync(true), () => _host.IsRunning);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => !_host.IsRunning);
        CancelWaitingCommand = new RelayCommand(() => _host.CancelWaiting(), () => _status == BatchStatus.Waiting);
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        PreflightCommand = new AsyncRelayCommand(PreflightAsync);
        ResetCooldownCommand = new RelayCommand(() => _host.ResetAllCooldowns());

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => UpdateWaitingText());
        _timer.Start();

        _forceRerun = host.Options.ForceRerun;
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; }

    public ObservableCollection<ToastItem> Toasts { get; }

    public ObservableCollection<string> OutputLines { get; }

    public TaskRowViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                OnPropertyChanged(nameof(SelectedInstruction));
                OnPropertyChanged(nameof(SelectedResult));
                OnPropertyChanged(nameof(SelectedError));
            }
        }
    }

    public string SelectedInstruction => _selectedTask?.Item.Instruction ?? string.Empty;

    public string SelectedResult => _selectedTask?.Item.Result ?? string.Empty;

    public string SelectedError => _selectedTask?.Item.Error ?? string.Empty;

    // ---------------- 统计 ----------------

    public int Total { get => _total; private set => SetProperty(ref _total, value); }

    public int Done { get => _done; private set => SetProperty(ref _done, value); }

    public int Failed { get => _failed; private set => SetProperty(ref _failed, value); }

    public int Running { get => _running; private set => SetProperty(ref _running, value); }

    public int Interrupted { get => _interrupted; private set => SetProperty(ref _interrupted, value); }

    public int Pending { get => _pending; private set => SetProperty(ref _pending, value); }

    public int Skipped { get => _skipped; private set => SetProperty(ref _skipped, value); }

    public double ProgressPercent => Total == 0 ? 0 : (Done + Failed + Interrupted) * 100.0 / Total;

    public string ProgressText => $"{Done + Failed + Interrupted}/{Total}";

    public BatchStatus Status
    {
        get => _status;
        private set
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
                CancelWaitingCommand.NotifyCanExecuteChanged();
                StartCommand.NotifyCanExecuteChanged();
                TerminateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusText => Status switch
    {
        BatchStatus.Idle => "空闲",
        BatchStatus.Running => "运行中",
        BatchStatus.Waiting => "等待恢复",
        BatchStatus.Blocked => "已阻塞",
        BatchStatus.Terminated => "已终止",
        BatchStatus.Completed => "已完成",
        _ => Status.ToString()
    };

    public IBrush StatusBrush => Status switch
    {
        BatchStatus.Running => new SolidColorBrush(Color.Parse("#2F6FED")),
        BatchStatus.Waiting => new SolidColorBrush(Color.Parse("#B06000")),
        BatchStatus.Blocked => new SolidColorBrush(Color.Parse("#D93025")),
        BatchStatus.Completed => new SolidColorBrush(Color.Parse("#1F9D55")),
        BatchStatus.Terminated => new SolidColorBrush(Color.Parse("#8A8F98")),
        _ => new SolidColorBrush(Color.Parse("#8A8F98"))
    };

    public bool IsWaiting => Status == BatchStatus.Waiting;

    public bool CanStart => !_host.IsRunning && !string.IsNullOrWhiteSpace(_host.Options.TaskFilePath);

    public bool CanTerminate => _host.IsRunning;

    public bool CanResume => !_host.IsRunning && !string.IsNullOrWhiteSpace(_host.Options.TaskFilePath);

    public bool CanCancelWaiting => Status == BatchStatus.Waiting;

    public string CurrentTaskText
    {
        get => _currentTaskText;
        private set => SetProperty(ref _currentTaskText, value);
    }

    public string CurrentComboText
    {
        get => _currentComboText;
        private set => SetProperty(ref _currentComboText, value);
    }

    public string CurrentWorkspaceText
    {
        get => _currentWorkspaceText;
        private set => SetProperty(ref _currentWorkspaceText, value);
    }

    public string WaitingText
    {
        get => _waitingText;
        private set => SetProperty(ref _waitingText, value);
    }

    public bool ForceRerun
    {
        get => _forceRerun;
        set
        {
            if (SetProperty(ref _forceRerun, value))
            {
                _host.Options.ForceRerun = value;
            }
        }
    }

    public bool WaitIfUnavailable
    {
        get => _waitIfUnavailable;
        set => SetProperty(ref _waitIfUnavailable, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string TaskFilePath => _host.Options.TaskFilePath;

    public string LastReportPath => _host.LastReportPath ?? "（尚未生成）";

    // ---------------- 命令 ----------------

    public IAsyncRelayCommand StartCommand { get; }

    public IAsyncRelayCommand TerminateCommand { get; }

    public IAsyncRelayCommand TerminateAfterCurrentCommand { get; }

    public IAsyncRelayCommand ResumeCommand { get; }

    public IRelayCommand CancelWaitingCommand { get; }

    public IAsyncRelayCommand LoadCommand { get; }

    public IAsyncRelayCommand PreflightCommand { get; }

    public IRelayCommand ResetCooldownCommand { get; }

    // ---------------- 引擎事件 ----------------

    private void OnEngineEvent(object? sender, BatchEvent ev)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (ev.Kind)
            {
                case BatchEventKind.StatusChanged:
                    if (ev.BatchStatus.HasValue)
                    {
                        Status = ev.BatchStatus.Value;
                    }

                    break;
                case BatchEventKind.TaskOutput:
                    OutputLines.Add(ev.Message);
                    if (OutputLines.Count > 500)
                    {
                        OutputLines.RemoveAt(0);
                    }

                    break;
                case BatchEventKind.BatchWaiting:
                    _resumeAt = ev.ResumeAt;
                    UpdateWaitingText();
                    break;
                case BatchEventKind.BatchResumed:
                    _resumeAt = null;
                    WaitingText = string.Empty;
                    break;
            }

            if (!string.IsNullOrEmpty(ev.Message) && ev.Kind is not (BatchEventKind.TaskOutput or BatchEventKind.StatusChanged))
            {
                AddToast(ev);
            }

            if (ev.Task is not null)
            {
                SyncTaskRow(ev.Task);
            }

            Recalculate();
        });
    }

    private void AddToast(BatchEvent ev)
    {
        var level = ev.Kind switch
        {
            BatchEventKind.DangerousOperationBlocked => "danger",
            BatchEventKind.BatchBlocked or BatchEventKind.ModelSwitched or BatchEventKind.AccountSwitched
                or BatchEventKind.FallbackUsed or BatchEventKind.BatchWaiting or BatchEventKind.QuotaResetRecorded
                or BatchEventKind.Warning or BatchEventKind.BatchTerminated => "warning",
            _ => "info"
        };

        Toasts.Insert(0, new ToastItem(level, ev.Message, ev.At));
        while (Toasts.Count > 100)
        {
            Toasts.RemoveAt(Toasts.Count - 1);
        }
    }

    private void UpdateWaitingText()
    {
        if (_resumeAt is null)
        {
            return;
        }

        var remaining = _resumeAt.Value - DateTimeOffset.Now;
        WaitingText = remaining <= TimeSpan.Zero
            ? "恢复时间点已到，正在继续…"
            : $"等待至 {_resumeAt.Value:HH:mm:ss} 自动继续（剩余 {remaining:hh\\:mm\\:ss}，可取消）";
    }

    private void SyncTasks()
    {
        var tasks = _host.Engine?.Tasks;
        if (tasks is null)
        {
            return;
        }

        Tasks.Clear();
        foreach (var t in tasks)
        {
            Tasks.Add(new TaskRowViewModel(t));
        }

        Recalculate();
    }

    private void SyncTaskRow(TaskItem item)
    {
        var row = Tasks.FirstOrDefault(r => r.Item.Id == item.Id);
        if (row is null)
        {
            Tasks.Add(new TaskRowViewModel(item));
        }
        else
        {
            row.Refresh();
        }
    }

    private void Recalculate()
    {
        var tasks = Tasks.Select(t => t.Item).ToList();
        Total = tasks.Count;
        Done = tasks.Count(t => t.Status == TaskStatus.Done);
        Failed = tasks.Count(t => t.Status == TaskStatus.Failed);
        Running = tasks.Count(t => t.Status == TaskStatus.Running);
        Interrupted = tasks.Count(t => t.Status == TaskStatus.Interrupted);
        Pending = tasks.Count(t => t.Status == TaskStatus.Pending);
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));

        var current = tasks.FirstOrDefault(t => t.Status == TaskStatus.Running);
        CurrentTaskText = current?.Id ?? "无";
        CurrentComboText = current is null
            ? "-"
            : string.Join(" + ", new[] { current.AccountUsed, current.ModelUsed }.Where(x => !string.IsNullOrEmpty(x)));
        CurrentWorkspaceText = current?.Workspace ?? _host.Options.DefaultWorkspace ?? "-";
    }

    // ---------------- 操作 ----------------

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_host.Options.TaskFilePath))
        {
            StatusMessage = "请先在配置页选择任务文件";
            return;
        }

        try
        {
            await _host.LoadTasksAsync().ConfigureAwait(false);
            StatusMessage = $"已加载任务文件（{DateTime.Now:HH:mm:ss}）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    private async Task PreflightAsync()
    {
        try
        {
            var report = await _host.PreflightAsync().ConfigureAwait(false);
            StatusMessage = report.CanStart
                ? $"预检通过：{report.LoadedTaskCount} 条任务，待处理 {report.PendingTaskCount} 条"
                : "预检未通过：" + string.Join("；", report.Errors);
        }
        catch (Exception ex)
        {
            StatusMessage = $"预检异常：{ex.Message}";
        }
    }

    private async Task StartAsync()
    {
        if (!await EnsurePreflightPassedAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var report = await _host.StartAsync(_waitIfUnavailable).ConfigureAwait(false);
            StatusMessage = $"批处理结束：{report.BatchStatus}，成功 {report.Done} / 失败 {report.Failed} / 中断 {report.Interrupted}";
            OnPropertyChanged(nameof(LastReportPath));
        }
        catch (Exception ex)
        {
            StatusMessage = $"执行异常：{ex.Message}";
        }
    }

    private async Task ResumeAsync()
    {
        if (!await EnsurePreflightPassedAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var report = await _host.StartAsync(_waitIfUnavailable).ConfigureAwait(false);
            StatusMessage = $"批处理结束：{report.BatchStatus}，成功 {report.Done} / 失败 {report.Failed} / 中断 {report.Interrupted}";
            OnPropertyChanged(nameof(LastReportPath));
        }
        catch (Exception ex)
        {
            StatusMessage = $"执行异常：{ex.Message}";
        }
    }

    /// <summary>
    /// 启动前预检闸门（REQ-010）：CLI 缺失 / 任务文件与工作空间问题 / 账号池为空等
    /// 环境错误在进入执行循环前拦截，避免每条任务白跑重试后判失败。
    /// </summary>
    private async Task<bool> EnsurePreflightPassedAsync()
    {
        try
        {
            var report = await _host.PreflightAsync().ConfigureAwait(false);
            if (report.CanStart)
            {
                return true;
            }

            StatusMessage = "预检未通过，已取消启动：" + string.Join("；", report.Errors);
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"预检异常，已取消启动：{ex.Message}";
            return false;
        }
    }
}
