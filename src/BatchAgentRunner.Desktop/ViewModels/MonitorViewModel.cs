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

    /// <summary>创建监控页 facade，装配各协作者、命令与定时器并订阅引擎事件。</summary>
    /// <param name="host">引擎宿主，提供引擎实例、配置与事件源。</param>
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

    /// <summary>任务行集合，随引擎任务事件增量更新或在全量同步时重建。</summary>
    public ObservableCollection<TaskRowViewModel> Tasks => _taskList.Tasks;

    /// <summary>事件提示板条目，非输出类引擎事件到达时追加。</summary>
    public ObservableCollection<ToastItem> Toasts => _toasts.Items;

    /// <summary>选中任务的全部输出行，尚未按显示规则过滤。</summary>
    public ObservableCollection<OutputLineItem> OutputLines => _output.OutputLines;

    /// <summary>按当前跟随与过滤规则筛选后实际渲染的输出行。</summary>
    public ObservableCollection<OutputLineItem> DisplayedOutputLines => _output.DisplayedOutputLines;

    /// <summary>选中任务的阶段行，切换任务时重建。</summary>
    public ObservableCollection<StageRowViewModel> SelectedStages => _taskList.SelectedStages;

    /// <summary>依赖关系看板的行数据，任务带 depends_on 时才有内容。</summary>
    public ObservableCollection<DagRowViewModel> DagRows => _taskList.DagRows;

    // ---------------- 输出流（转发 OutputStreamCoordinator） ----------------

    /// <summary>是否显示全部任务的输出，关闭时只看选中任务。</summary>
    public bool ShowAllOutput
    {
        get => _output.ShowAllOutput;
        set => _output.ShowAllOutput = value;
    }

    /// <summary>输出跟随开关的按钮文案，随跟随模式刷新。</summary>
    public string OutputFollowTitle => _output.OutputFollowTitle;

    /// <summary>是否有可显示的输出行，为假时展示空态提示。</summary>
    public bool HasDisplayedOutput => _output.HasDisplayedOutput;

    /// <summary>输出区空态提示语，说明当前没有内容的原因。</summary>
    public string OutputEmptyHint => _output.OutputEmptyHint;

    // ---------------- 统计（转发 BatchStatsState） ----------------

    /// <summary>任务总数。</summary>
    public int Total => _statsState.Total;
    /// <summary>已成功完成的任务数。</summary>
    public int Done => _statsState.Done;
    /// <summary>执行失败的任务数。</summary>
    public int Failed => _statsState.Failed;
    /// <summary>当前正在执行的任务数。</summary>
    public int Running => _statsState.Running;
    /// <summary>被中断的任务数。</summary>
    public int Interrupted => _statsState.Interrupted;
    /// <summary>尚未开始的任务数。</summary>
    public int Pending => _statsState.Pending;
    /// <summary>因续跑时已完成而跳过的任务数。</summary>
    public int Skipped => _statsState.Skipped;
    /// <summary>需要人工介入的任务数。</summary>
    public int NeedsAttention => _statsState.NeedsAttention;
    /// <summary>因依赖阻塞无法执行的任务数。</summary>
    public int Blocked => _statsState.Blocked;
    /// <summary>预计剩余时间文案，按已完成进度外推。</summary>
    public string EtaText => _statsState.EtaText;
    /// <summary>当前执行任务的标识文案。</summary>
    public string CurrentTaskText => _statsState.CurrentTaskText;
    /// <summary>当前任务使用的账号与模型组合文案。</summary>
    public string CurrentComboText => _statsState.CurrentComboText;
    /// <summary>当前任务工作空间路径的展示文案。</summary>
    public string CurrentWorkspaceText => _statsState.CurrentWorkspaceText;

    /// <summary>完成百分比（完成 + 失败 + 中断 / 总数），供进度条绑定。</summary>
    public double ProgressPercent => _statsState.Total == 0 ? 0 : (_statsState.Done + _statsState.Failed + _statsState.Interrupted) * 100.0 / _statsState.Total;

    /// <summary>进度分数文案，形如「3 / 10」。</summary>
    public string ProgressText => $"{_statsState.Done + _statsState.Failed + _statsState.Interrupted} / {_statsState.Total}";

    // ---------------- 批次状态（转发 BatchStatusState） ----------------

    /// <summary>批次整体状态，引擎状态事件到达时写入。</summary>
    public BatchStatus Status
    {
        get => _statusState.Status;
        private set => _statusState.Status = value;
    }

    /// <summary>批次状态的中文文案，用于顶部状态标签。</summary>
    public string StatusText => _statusState.StatusText;
    /// <summary>按批次状态取色的画刷，随浅色 / 深色主题切换。</summary>
    public Avalonia.Media.IBrush StatusBrush => _statusState.StatusBrush;
    /// <summary>批次是否处于等待到点自动恢复的状态。</summary>
    public bool IsWaiting => _statusState.IsWaiting;
    /// <summary>调度是否因账号额度冻结而暂停派发。</summary>
    public bool DispatchPaused
    {
        get => _statusState.DispatchPaused;
        private set => _statusState.DispatchPaused = value;
    }

    /// <summary>当前是否允许开始，未运行且已配置任务文件时为真。</summary>
    public bool CanStart => _statusState.CanStart;
    /// <summary>当前是否允许终止，仅在批处理运行中为真。</summary>
    public bool CanTerminate => _statusState.CanTerminate;
    /// <summary>当前是否允许续跑，批处理未运行时为真。</summary>
    public bool CanResume => _statusState.CanResume;
    /// <summary>当前是否可取消等待，仅在等待状态为真。</summary>
    public bool CanCancelWaiting => _statusState.CanCancelWaiting;

    // ---------------- 选中任务（派生自 _selectedTask） ----------------

    /// <summary>列表中选中的任务行，切换后刷新阶段树并切换输出过滤目标。</summary>
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

    /// <summary>选中任务是否为工作流任务。</summary>
    public bool SelectedIsWorkflow => _selectedTask?.Item.IsWorkflow == true;
    /// <summary>选中任务是否带工作流，为真时显示阶段区。</summary>
    public bool HasSelectedWorkflow => SelectedIsWorkflow;
    /// <summary>选中任务由模板展开时的模板 ID，非模板任务为空串。</summary>
    public string SelectedTemplateId => _selectedTask?.Item.TemplateVars?.TemplateId ?? string.Empty;
    /// <summary>选中任务是否来自模板展开。</summary>
    public bool HasSelectedTemplate => !string.IsNullOrEmpty(SelectedTemplateId);
    /// <summary>是否有选中任务，为假时详情区显示未选中提示。</summary>
    public bool HasSelectedTask => _selectedTask is not null;
    /// <summary>选中任务是否带有错误信息。</summary>
    public bool SelectedHasError => _selectedTask?.HasError == true;
    /// <summary>任务列表中是否存在依赖关系，为真时显示 DAG 看板。</summary>
    public bool HasDag => _taskList.Tasks.Any(t => t.Item.DependsOn is { Count: > 0 });
    /// <summary>选中任务的指令正文。</summary>
    public string SelectedInstruction => _selectedTask?.Item.Instruction ?? string.Empty;
    /// <summary>选中任务的执行结果文本。</summary>
    public string SelectedResult => _selectedTask?.Item.Result ?? string.Empty;
    /// <summary>选中任务的错误信息文本。</summary>
    public string SelectedError => _selectedTask?.Item.Error ?? string.Empty;

    // ---------------- 运行参数 / 状态栏 ----------------

    /// <summary>是否忽略续跑台账强制重跑，勾选即写入配置项。</summary>
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

    /// <summary>账号或额度暂不可用时是否等待，而不是直接判定失败。</summary>
    public bool WaitIfUnavailable
    {
        get => _waitIfUnavailable;
        set => SetProperty(ref _waitIfUnavailable, value);
    }

    /// <summary>状态栏操作提示，加载 / 预检 / 启动结束后写入。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>等待恢复的倒计时文案，由每秒定时器刷新。</summary>
    public string WaitingText
    {
        get => _waitingText;
        private set => SetProperty(ref _waitingText, value);
    }

    /// <summary>当前任务文件路径，取自配置，供状态栏显示。</summary>
    public string TaskFilePath => _host.Options.TaskFilePath;
    /// <summary>最近一次报告文件路径，尚未生成时显示占位文案。</summary>
    public string LastReportPath => _host.LastReportPath ?? "（尚未生成）";

    // ---------------- 筛选 ----------------

    /// <summary>任务列表当前筛选键（全部 / 执行中 / 待处理 / 完成 / 失败）。</summary>
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

    /// <summary>按当前筛选键过滤后的任务行，供列表绑定。</summary>
    public IEnumerable<TaskRowViewModel> VisibleTasks => _activeFilter switch
    {
        "执行中" => _taskList.Tasks.Where(t => t.Status == TaskStatus.Running),
        "待处理" => _taskList.Tasks.Where(t => t.Status == TaskStatus.Pending),
        "完成" => _taskList.Tasks.Where(t => t.Status == TaskStatus.Done),
        "失败" => _taskList.Tasks.Where(t => t.Status == TaskStatus.Failed),
        _ => _taskList.Tasks
    };

    // ---------------- 命令 ----------------

    /// <summary>开始命令，点击后按当前等待策略启动批处理。</summary>
    public IAsyncRelayCommand StartCommand { get; }
    /// <summary>终止命令，点击后立即停止整批任务。</summary>
    public IAsyncRelayCommand TerminateCommand { get; }
    /// <summary>温和终止命令，当前任务跑完后不再派发新任务。</summary>
    public IAsyncRelayCommand TerminateAfterCurrentCommand { get; }
    /// <summary>续跑命令，跳过已完成任务继续执行剩余部分。</summary>
    public IAsyncRelayCommand ResumeCommand { get; }
    /// <summary>取消等待命令，点击后跳过恢复时间点立即继续。</summary>
    public IRelayCommand CancelWaitingCommand { get; }
    /// <summary>加载命令，点击后从任务文件读取任务清单。</summary>
    public IAsyncRelayCommand LoadCommand { get; }
    /// <summary>预检命令，检查 CLI 登录、账号与工作空间是否就绪。</summary>
    public IAsyncRelayCommand PreflightCommand { get; }
    /// <summary>重置冷却命令，清空所有账号的额度冷却计时。</summary>
    public IRelayCommand ResetCooldownCommand { get; }
    /// <summary>筛选命令，参数为筛选键，点击后切换任务列表过滤。</summary>
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
