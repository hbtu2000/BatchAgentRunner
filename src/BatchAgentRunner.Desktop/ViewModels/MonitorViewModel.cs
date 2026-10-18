using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using BatchAgentRunner.Core.Domain;
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
    private int _needsAttention;
    private int _blocked;
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
        SelectedStages = new ObservableCollection<StageRowViewModel>();
        DagRows = new ObservableCollection<DagRowViewModel>();

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

    /// <summary>选中任务的工作流阶段树（WorkflowView / GateResultView 数据源；非工作流任务为空）。</summary>
    public ObservableCollection<StageRowViewModel> SelectedStages { get; }

    /// <summary>依赖看板行（DagBoardView 数据源；仅当批次含 DAG 依赖时有内容）。</summary>
    public ObservableCollection<DagRowViewModel> DagRows { get; }

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
                RefreshSelectedStages();
            }
        }
    }

    /// <summary>选中任务是否工作流任务（控制阶段树区域可见性）。</summary>
    public bool SelectedIsWorkflow => _selectedTask?.Item.IsWorkflow == true;

    /// <summary>兼容绑定名。</summary>
    public bool HasSelectedWorkflow => SelectedIsWorkflow;

    /// <summary>选中任务的模板来源（模板展开产物溯源展示）。</summary>
    public string SelectedTemplateId => _selectedTask?.Item.TemplateVars?.TemplateId ?? string.Empty;

    public bool HasSelectedTemplate => !string.IsNullOrEmpty(SelectedTemplateId);

    /// <summary>看板是否可见（批次含依赖任务才显示 DAG 区域）。</summary>
    public bool HasDag => Tasks.Any(t => t.Item.DependsOn is { Count: > 0 });

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

    /// <summary>待人工介入任务数（二期：门禁超限等，不计入失败率）。</summary>
    public int NeedsAttention { get => _needsAttention; private set => SetProperty(ref _needsAttention, value); }

    /// <summary>依赖阻塞任务数（二期：DAG 上游失败导致）。</summary>
    public int Blocked { get => _blocked; private set => SetProperty(ref _blocked, value); }

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

        // 二期事件走 Info/Warning 通道，按消息关键词升级告警级别（门禁/依赖/会话/通知/模板）。
        if (level == "info" && IsPhase2Attention(ev.Message))
        {
            level = "warning";
        }

        Toasts.Insert(0, new ToastItem(level, ev.Message, ev.At));
        while (Toasts.Count > 100)
        {
            Toasts.RemoveAt(Toasts.Count - 1);
        }
    }

    /// <summary>二期关注类消息关键词：模板展开 / 门禁修订 / 依赖阻塞 / 会话恢复 / 通知发送失败。</summary>
    private static bool IsPhase2Attention(string? message) => message is not null && (
        message.Contains("门禁", StringComparison.Ordinal) ||
        message.Contains("依赖", StringComparison.Ordinal) ||
        message.Contains("模板", StringComparison.Ordinal) ||
        message.Contains("会话恢复", StringComparison.Ordinal) ||
        message.Contains("通知", StringComparison.Ordinal) ||
        message.Contains("待人工", StringComparison.Ordinal));

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

        RebuildDagRows();
        OnPropertyChanged(nameof(HasDag));
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

        RebuildDagRows();
        OnPropertyChanged(nameof(HasDag));
        RefreshSelectedStages();
    }

    /// <summary>重建选中任务的工作流阶段树（WorkflowView / GateResultView）。</summary>
    private void RefreshSelectedStages()
    {
        SelectedStages.Clear();
        var stages = _selectedTask?.Item.StageResults;
        if (stages is null)
        {
            return;
        }

        foreach (var s in stages)
        {
            SelectedStages.Add(new StageRowViewModel(s));
        }
    }

    /// <summary>
    /// 重建依赖看板（DagBoardView）：按拓扑层级缩进排列，
    /// blocked_by 取自引擎的依赖阻断判定（与报告一致）。
    /// </summary>
    private void RebuildDagRows()
    {
        DagRows.Clear();
        var items = Tasks.Select(t => t.Item).ToList();
        if (!items.Any(t => t.DependsOn is { Count: > 0 }))
        {
            return;
        }

        var levels = ComputeLevels(items);
        foreach (var item in items)
        {
            var blockedBy = ComputeBlockedByView(item, items);
            DagRows.Add(new DagRowViewModel(item, levels.GetValueOrDefault(item.Id, 0), blockedBy));
        }
    }

    /// <summary>拓扑层级：无依赖 = 0，否则 = max(依赖层级) + 1（环/悬空按 0 层保守处理）。</summary>
    private static Dictionary<string, int> ComputeLevels(IReadOnlyList<TaskItem> items)
    {
        var byId = items.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int LevelOf(TaskItem t)
        {
            if (levels.TryGetValue(t.Id, out var cached))
            {
                return cached;
            }

            levels[t.Id] = 0; // 防环：先占位
            var level = 0;
            if (t.DependsOn is { Count: > 0 })
            {
                foreach (var dep in t.DependsOn)
                {
                    if (byId.TryGetValue(dep, out var depTask))
                    {
                        level = Math.Max(level, LevelOf(depTask) + 1);
                    }
                }
            }

            levels[t.Id] = level;
            return level;
        }

        foreach (var t in items)
        {
            LevelOf(t);
        }

        return levels;
    }

    /// <summary>看板视图的 blocked_by：上游失败/阻塞/悬空的依赖 Id（与引擎 ApplyDependencyBlockAsync 判定一致）。</summary>
    private static List<string> ComputeBlockedByView(TaskItem item, IReadOnlyList<TaskItem> items)
    {
        if (item.DependsOn is not { Count: > 0 } ||
            item.Status is not (TaskStatus.Pending or TaskStatus.Blocked))
        {
            return new List<string>();
        }

        var byId = items.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        return item.DependsOn.Where(dep =>
            !byId.TryGetValue(dep, out var upstream) ||
            upstream.Status is TaskStatus.Failed or TaskStatus.Blocked or TaskStatus.NeedsAttention).ToList();
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
        NeedsAttention = tasks.Count(t => t.Status == TaskStatus.NeedsAttention);
        Blocked = tasks.Count(t => t.Status == TaskStatus.Blocked);
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
            SetStatusMessage($"已加载任务文件（{DateTime.Now:HH:mm:ss}）");
        }
        catch (Exception ex)
        {
            SetStatusMessage($"加载失败：{ex.Message}");
        }
    }

    private async Task PreflightAsync()
    {
        try
        {
            var report = await _host.PreflightAsync().ConfigureAwait(false);
            SetStatusMessage(report.CanStart
                ? $"预检通过：{report.LoadedTaskCount} 条任务，待处理 {report.PendingTaskCount} 条"
                : "预检未通过：" + string.Join("；", report.Errors));
        }
        catch (Exception ex)
        {
            SetStatusMessage($"预检异常：{ex.Message}");
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
            FinishWithReport(report);
        }
        catch (Exception ex)
        {
            SetStatusMessage($"执行异常：{ex.Message}");
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
            FinishWithReport(report);
        }
        catch (Exception ex)
        {
            SetStatusMessage($"执行异常：{ex.Message}");
        }
    }

    /// <summary>批处理结束后的界面收尾（P0-6：后台线程 → UI 线程后再写绑定属性）。</summary>
    private void FinishWithReport(BatchReport report) => UiThread.Post(() =>
    {
        StatusMessage = $"批处理结束：{report.BatchStatus}，成功 {report.Done} / 失败 {report.Failed} / 中断 {report.Interrupted}";
        OnPropertyChanged(nameof(LastReportPath));
    });

    /// <summary>状态栏文案写入：调用方可能来自后台线程，统一marshal到 UI 线程。</summary>
    private void SetStatusMessage(string text) => UiThread.Post(() => StatusMessage = text);

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

            SetStatusMessage("预检未通过，已取消启动：" + string.Join("；", report.Errors));
            return false;
        }
        catch (Exception ex)
        {
            SetStatusMessage($"预检异常，已取消启动：{ex.Message}");
            return false;
        }
    }
}
