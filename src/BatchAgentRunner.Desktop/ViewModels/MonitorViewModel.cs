using System.Collections.ObjectModel;
using Avalonia.Threading;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Desktop.Models;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 执行监控页 façade（P-06 分解收口产物）。
/// <para>
/// 原 <c>MonitorViewModel</c> 是 983 行的上帝类，承载「状态统计 + 批次状态 + 实时输出流 + 事件提示板 +
/// 任务列表同步 + DAG 看板 + 选中任务阶段树 + 操作编排」。P-06 按「状态持有 + 绑定转发 + 事件编排」
/// 将其下沉为若干协作者（<see cref="Monitor"/> 命名空间下）：<see cref="OutputStreamCoordinator"/>（输出流）、
/// <see cref="BatchStatsState"/>（统计快照状态）、<see cref="BatchStatusState"/>（批次状态）、
/// <see cref="EventToastBoard"/>（提示板）、<see cref="TaskListCoordinator"/>（任务列表 / DAG / 阶段树）、
/// <see cref="BatchStatistics"/>（纯统计计算）、<see cref="OutputTagClassifier"/>（输出分类）、
/// <see cref="MonitorOperationsCoordinator"/>（加载 / 预检 / 开始 / 续跑操作编排）。
/// 本类退化为 façade：持有协作者、转发可绑定属性、订阅其 <c>PropertyChanged</c> 透传到本 façade 以保住
/// Avalonia 反射绑定契约，并把引擎事件路由到对应协作者。对外公开 API（属性名 / 命令名 / 语义）逐字节等价于
/// 分解前，故既有 headless 契约测试无需改动。
/// </para>
/// </summary>
public sealed class MonitorViewModel : ViewModelBase
{
    private readonly IEngineHost _host;
    private readonly DispatcherTimer _timer;

    private readonly BatchStatusState _statusState;
    private readonly BatchStatsState _statsState;
    private readonly TaskListCoordinator _taskList;
    private readonly OutputStreamCoordinator _output;
    private readonly EventToastBoard _toasts;
    private readonly MonitorOperationsCoordinator _operations;
    private readonly Func<IReadOnlyList<TaskItem>> _taskItems;

    private TaskRowViewModel? _selectedTask;
    private DateTimeOffset? _resumeAt;
    private bool _forceRerun;
    private bool _waitIfUnavailable = true;
    private string _statusMessage = string.Empty;
    private string _waitingText = string.Empty;
    private string _activeFilter = "全部";

    public MonitorViewModel(IEngineHost host)
    {
        _host = host;
        _statusState = new BatchStatusState(host);
        _statsState = new BatchStatsState();
        _taskList = new TaskListCoordinator();
        _taskItems = () => _taskList.Tasks.Select(t => t.Item).ToList();
        _output = new OutputStreamCoordinator(_taskItems);
        _toasts = new EventToastBoard();
        _operations = new MonitorOperationsCoordinator(new MonitorOperationsContext
        {
            Host = host,
            Toasts = _toasts,
            WriteStatusMessage = text => StatusMessage = text,
            NotifyLastReportPathChanged = () => OnPropertyChanged(nameof(LastReportPath))
        });

        _host.EventRaised += OnEngineEvent;
        _host.TasksChanged += (_, _) => Dispatcher.UIThread.Post(SyncTasks);

        StartCommand = new AsyncRelayCommand(() => _operations.StartAsync(_waitIfUnavailable), () => !_host.IsRunning && !string.IsNullOrWhiteSpace(_host.Options.TaskFilePath));
        TerminateCommand = new AsyncRelayCommand(() => _host.TerminateAsync(false), () => _host.IsRunning);
        TerminateAfterCurrentCommand = new AsyncRelayCommand(() => _host.TerminateAsync(true), () => _host.IsRunning);
        ResumeCommand = new AsyncRelayCommand(() => _operations.ResumeAsync(_waitIfUnavailable), () => !_host.IsRunning);
        CancelWaitingCommand = new RelayCommand(() => _host.CancelWaiting(), () => _statusState.Status == BatchStatus.Waiting);
        LoadCommand = new AsyncRelayCommand(() => _operations.LoadAsync());
        PreflightCommand = new AsyncRelayCommand(() => _operations.PreflightAsync());
        ResetCooldownCommand = new RelayCommand(() => _host.ResetAllCooldowns());
        SetFilterCommand = new RelayCommand<string>(filter => ActiveFilter = filter ?? "全部");

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => UpdateWaitingText());
        _timer.Start();

        _forceRerun = host.Options.ForceRerun;

        _statusState.PropertyChanged += _ForwardStatus;
        _statsState.PropertyChanged += _Forward;
        _output.PropertyChanged += _Forward;

        _output.RefreshFollowTitle();
    }

    // ---------------- 集合（直接透传协作者的同一实例，集合自身通知） ----------------

    public ObservableCollection<TaskRowViewModel> Tasks => _taskList.Tasks;

    public ObservableCollection<ToastItem> Toasts => _toasts.Items;

    public ObservableCollection<OutputLineItem> OutputLines => _output.OutputLines;

    public ObservableCollection<OutputLineItem> DisplayedOutputLines => _output.DisplayedOutputLines;

    public ObservableCollection<StageRowViewModel> SelectedStages => _taskList.SelectedStages;

    public ObservableCollection<DagRowViewModel> DagRows => _taskList.DagRows;

    // ---------------- 输出流（转发 OutputStreamCoordinator） ----------------

    public bool ShowAllOutput
    {
        get => _output.ShowAllOutput;
        set => _output.ShowAllOutput = value;
    }

    public string OutputFollowTitle => _output.OutputFollowTitle;

    public bool HasDisplayedOutput => _output.HasDisplayedOutput;

    public string OutputEmptyHint => _output.OutputEmptyHint;

    // ---------------- 统计（转发 BatchStatsState） ----------------

    public int Total => _statsState.Total;
    public int Done => _statsState.Done;
    public int Failed => _statsState.Failed;
    public int Running => _statsState.Running;
    public int Interrupted => _statsState.Interrupted;
    public int Pending => _statsState.Pending;
    public int Skipped => _statsState.Skipped;
    public int NeedsAttention => _statsState.NeedsAttention;
    public int Blocked => _statsState.Blocked;
    public string EtaText => _statsState.EtaText;
    public string CurrentTaskText => _statsState.CurrentTaskText;
    public string CurrentComboText => _statsState.CurrentComboText;
    public string CurrentWorkspaceText => _statsState.CurrentWorkspaceText;

    public double ProgressPercent => _statsState.Total == 0 ? 0 : (_statsState.Done + _statsState.Failed + _statsState.Interrupted) * 100.0 / _statsState.Total;

    public string ProgressText => $"{_statsState.Done + _statsState.Failed + _statsState.Interrupted} / {_statsState.Total}";

    // ---------------- 批次状态（转发 BatchStatusState） ----------------

    public BatchStatus Status
    {
        get => _statusState.Status;
        private set => _statusState.Status = value;
    }

    public string StatusText => _statusState.StatusText;
    public Avalonia.Media.IBrush StatusBrush => _statusState.StatusBrush;
    public bool IsWaiting => _statusState.IsWaiting;
    public bool DispatchPaused
    {
        get => _statusState.DispatchPaused;
        private set => _statusState.DispatchPaused = value;
    }

    public bool CanStart => _statusState.CanStart;
    public bool CanTerminate => _statusState.CanTerminate;
    public bool CanResume => _statusState.CanResume;
    public bool CanCancelWaiting => _statusState.CanCancelWaiting;

    // ---------------- 选中任务（派生自 _selectedTask） ----------------

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
                OnPropertyChanged(nameof(HasSelectedWorkflow));
                OnPropertyChanged(nameof(SelectedIsWorkflow));
                OnPropertyChanged(nameof(SelectedTemplateId));
                OnPropertyChanged(nameof(HasSelectedTemplate));
                OnPropertyChanged(nameof(HasSelectedTask));
                OnPropertyChanged(nameof(SelectedHasError));
                _taskList.RefreshStages(_selectedTask?.Item);
                _output.SetSelectedTask(_selectedTask?.Id);
            }
        }
    }

    public bool SelectedIsWorkflow => _selectedTask?.Item.IsWorkflow == true;
    public bool HasSelectedWorkflow => SelectedIsWorkflow;
    public string SelectedTemplateId => _selectedTask?.Item.TemplateVars?.TemplateId ?? string.Empty;
    public bool HasSelectedTemplate => !string.IsNullOrEmpty(SelectedTemplateId);
    public bool HasSelectedTask => _selectedTask is not null;
    public bool SelectedHasError => _selectedTask?.HasError == true;
    public bool HasDag => _taskList.Tasks.Any(t => t.Item.DependsOn is { Count: > 0 });
    public string SelectedInstruction => _selectedTask?.Item.Instruction ?? string.Empty;
    public string SelectedResult => _selectedTask?.Item.Result ?? string.Empty;
    public string SelectedError => _selectedTask?.Item.Error ?? string.Empty;

    // ---------------- 运行参数 / 状态栏 ----------------

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

    public string WaitingText
    {
        get => _waitingText;
        private set => SetProperty(ref _waitingText, value);
    }

    public string TaskFilePath => _host.Options.TaskFilePath;
    public string LastReportPath => _host.LastReportPath ?? "（尚未生成）";

    // ---------------- 筛选 ----------------

    public string ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (SetProperty(ref _activeFilter, value))
            {
                OnPropertyChanged(nameof(VisibleTasks));
            }
        }
    }

    public IEnumerable<TaskRowViewModel> VisibleTasks => _activeFilter switch
    {
        "执行中" => _taskList.Tasks.Where(t => t.Status == TaskStatus.Running),
        "待处理" => _taskList.Tasks.Where(t => t.Status == TaskStatus.Pending),
        "完成" => _taskList.Tasks.Where(t => t.Status == TaskStatus.Done),
        "失败" => _taskList.Tasks.Where(t => t.Status == TaskStatus.Failed),
        _ => _taskList.Tasks
    };

    // ---------------- 命令 ----------------

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand TerminateCommand { get; }
    public IAsyncRelayCommand TerminateAfterCurrentCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IRelayCommand CancelWaitingCommand { get; }
    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand PreflightCommand { get; }
    public IRelayCommand ResetCooldownCommand { get; }
    public IRelayCommand SetFilterCommand { get; }

    // ---------------- 引擎事件编排 ----------------

    private void OnEngineEvent(object? sender, BatchEvent ev)
    {
        switch (ev.Kind)
        {
            case BatchEventKind.StatusChanged:
                if (ev.BatchStatus.HasValue)
                {
                    Status = ev.BatchStatus.Value;
                    if (ev.BatchStatus.Value is BatchStatus.Completed or BatchStatus.Terminated or BatchStatus.Blocked)
                    {
                        DispatchPaused = false;
                    }
                }

                break;
            case BatchEventKind.SlotFrozen:
                DispatchPaused = true;
                break;
            case BatchEventKind.AccountSwitched:
                if (ev.DeferredSwitch)
                {
                    DispatchPaused = false;
                }

                break;
            case BatchEventKind.TaskOutput:
                _output.AppendChunk(ev.At, ev.Message, ev.TaskId);
                break;
            case BatchEventKind.TaskStarted:
                _output.OnTaskStarted(ev.TaskId);
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
            _toasts.Add(ev);
            _output.AppendEvent(ev, ev.TaskId ?? ev.Task?.Id);
        }

        if (ev.Task is not null)
        {
            SyncTaskRow(ev.Task);
        }

        Recalculate();
    }

    private void SyncTaskRow(TaskItem item)
    {
        _taskList.Upsert(item);
        _taskList.RefreshStages(_selectedTask?.Item);
        OnPropertyChanged(nameof(HasDag));
        OnPropertyChanged(nameof(VisibleTasks));
    }

    private void SyncTasks()
    {
        var tasks = _host.Engine?.Tasks;
        var prevId = _selectedTask?.Id;
        _selectedTask = _taskList.RebuildAll(tasks, prevId);
        OnPropertyChanged(nameof(SelectedTask));
        OnPropertyChanged(nameof(HasDag));
        OnPropertyChanged(nameof(VisibleTasks));
        Recalculate();
    }

    private void Recalculate()
    {
        _statsState.Apply(BatchStatistics.Compute(_taskItems(), _host.Options.DefaultWorkspace));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(VisibleTasks));
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

    // ---------------- 协作者属性变更透传（保住 Avalonia 反射绑定契约） ----------------

    private void _Forward(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    private void _ForwardStatus(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }

        // 原 Status setter 在状态变更时联动刷新命令可用性（Start/Terminate/CancelWaiting 的
        // can-execute 谓词读宿主 IsRunning / 状态，与原行为一致）。
        if (e.PropertyName == nameof(BatchStatusState.Status))
        {
            CancelWaitingCommand.NotifyCanExecuteChanged();
            StartCommand.NotifyCanExecuteChanged();
            TerminateCommand.NotifyCanExecuteChanged();
        }
    }
}
