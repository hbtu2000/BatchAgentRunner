using System.Diagnostics.CodeAnalysis;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;
namespace BatchAgentRunner.Core.Engine;

/// <summary>单任务执行循环的结果。</summary>
public enum TaskOutcome
{
    /// <summary>任务到达终态（done / failed），继续下一条。</summary>
    Finished,
    /// <summary>批处理进入 blocked，停止派发。</summary>
    Blocked,
    /// <summary>被用户终止。</summary>
    Terminated,
    /// <summary>失败即停（REQ-013）触发暂停。</summary>
    StopBatch
}
/// <summary>
/// 批处理调度引擎（PRD 第 10 章架构方法 + 附录 B 降级状态机）。串行 / 受控并发（REQ-012）共用同一套可用性视图与单写者回写。
/// </summary>
/// <remarks>
/// CA1001（P0-12）：持有 <see cref="SemaphoreSlim"/> 门与 <see cref="CancellationTokenSource"/>，实现 <see cref="IDisposable"/> 由宿主释放。
/// P-02 收口（ARCH §3.3 + A-7）：编排已下沉到 <see cref="WaitCoordinator"/> / <see cref="BatchPersistence"/> /
/// <see cref="AccountSwitchCoordinator"/> / <see cref="BatchLifecycle"/> 等协作者，facade 退化为「按序编排 + 维护批次级状态」。
/// </remarks>
public sealed class BatchEngine : IBatchEngine
{
    private readonly RunOptions _options;
    private readonly IAgentCliAdapter _adapter;
    private readonly ITaskFileStore _taskStore;
    private readonly ICooldownStore _cooldownStore;
    private readonly IRunLogger _logger;
    private readonly IClock _clock;
    private readonly QuotaCooldownRegistry _cooldowns;
    private readonly AvailabilityEvaluator _evaluator;
    private readonly QuotaErrorClassifier _classifier;
    private readonly RetryPolicy _retryPolicy;
    /// <summary>凭证 Vault（DESIGN-账号凭证切换-v1.0）。null = 不启用凭证切换（保持旧行为）。</summary>
    private readonly ICredentialVault? _vault;
    private readonly List<TaskItem> _tasks = new();
    /// <summary>降级矩阵（P-02 仍为引擎持有的共享可变状态；重建经 <see cref="RebuildMatrix"/>，供各协作者经 MatrixProvider 读取）。</summary>
    private DegradationMatrix _matrix;
    private readonly List<NotificationRule> _notifications;
    private readonly INotificationDispatcher? _notificationDispatcher;
    private readonly GateEvaluator _gateEvaluator;
    /// <summary>文件系统探测端口（P1-10）：引擎零直接 IO。</summary>
    private readonly IFileSystem _fileSystem;
    private readonly SemaphoreSlim _statsLock = new(1, 1);
    /// <summary>单任务级暂停等待累计（DESIGN-额度耗尽暂停续跑：从任务时长中剔除）。key 不区分大小写。</summary>
    private readonly Dictionary<string, double> _taskWaitSeconds = new(StringComparer.OrdinalIgnoreCase);

    // 既有协作者（P1-1 拆分产物）
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能", Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假计费验证不变量），拒绝向具体类型回退；逃逸阀台账 #8 / 到期日 2027-01-20 / 责任人 工程")]
    private readonly IFallbackBilling _fallback;
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能", Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假报告构建器验证聚合），拒绝向具体类型回退；逃逸阀台账 #8 / 到期日 2027-01-20 / 责任人 工程")]
    private readonly IBatchReportBuilder _report;
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能", Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假 Git 工作流验证尽力而为语义），拒绝向具体类型回退；逃逸阀台账 #8 / 到期日 2027-01-20 / 责任人 工程")]
    private readonly IGitWorkflow _gitWorkflow;
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能", Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假通知器验证分发时机），拒绝向具体类型回退；逃逸阀台账 #8 / 到期日 2027-01-20 / 责任人 工程")]
    private readonly IBatchNotifier _notifier;
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能", Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假预检验证报告契约），拒绝向具体类型回退；逃逸阀台账 #8 / 到期日 2027-01-20 / 责任人 工程")]
    private readonly IPreflight _preflight;
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能", Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假单任务执行器验证状态机不变量），拒绝向具体类型回退；逃逸阀台账 #8 / 到期日 2027-01-20 / 责任人 工程")]
    private readonly ITaskRunner _runner;
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能", Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假派发器验证并发/DAG 不变量），拒绝向具体类型回退；逃逸阀台账 #8 / 到期日 2027-01-20 / 责任人 工程")]
    private readonly ITaskDispatcher _dispatcher;
    /// <summary>并行调度协调器（REQ-020 / REQ-021）：引擎持有唯一实例，分别注入派发器与单任务执行器。</summary>
    private readonly DispatchCoordinator _coordinator;

    // P-01 / P-02 新增协作者：等待协调、持久化、凭证切换、批次生命周期编排
    private readonly WaitCoordinator _waitCoordinator;
    private readonly BatchPersistence _persistence;
    private readonly AccountSwitchCoordinator _accountSwitch;
    private readonly BatchLifecycle _lifecycle;
    private readonly BatchLifecycle.BatchRunState _runState;
    /// <summary>
    /// 本批关联标识（ARCH P-10 / A-8）：与运行日志共用同一实例（<see cref="IRunLogger.RunId"/>），
    /// 使日志、事件、指标三者可按同一次运行聚合；日志未提供时（空实现）引擎自生成。
    /// </summary>
    private readonly string _runId;
    /// <summary>运行级指标采集器（ARCH P-10 / A-8）。</summary>
    private readonly RunMetrics _metrics;
    public BatchEngine(
        RunOptions options,
        IAgentCliAdapter adapter,
        ITaskFileStore taskStore,
        ICooldownStore cooldownStore,
        IRunLogger logger,
        BatchEngineDependencies? dependencies = null)
    {
        var deps = dependencies ?? new BatchEngineDependencies();
        _options = options;
        _adapter = adapter;
        _taskStore = taskStore;
        _cooldownStore = cooldownStore;
        _logger = logger;
        _runId = string.IsNullOrWhiteSpace(logger.RunId) ? RunLogEntry.NewRunId() : logger.RunId;
        _metrics = deps.Metrics ?? new RunMetrics(_runId);
        _vault = deps.CredentialVault;
        _notifications = deps.Notifications?.ToList() ?? options.Notifications?.ToList() ?? new List<NotificationRule>();
        _notificationDispatcher = deps.NotificationDispatcher;
        _gateEvaluator = deps.GateEvaluator ?? new GateEvaluator(deps.ProcessRunner ?? new NullProcessRunner());
        _clock = deps.Clock ?? new SystemClock();
        _classifier = deps.Classifier ?? new QuotaErrorClassifier();
        _cooldowns = new QuotaCooldownRegistry(_clock, options.DefaultCooldown);
        _evaluator = new AvailabilityEvaluator(_cooldowns);
        _retryPolicy = new RetryPolicy
        {
            MaxRetries = options.MaxRetriesPerTask,
            RateLimitBackoffRetries = options.RateLimitBackoffRetries,
            InitialBackoff = TimeSpan.FromSeconds(Math.Max(1, options.RateLimitInitialBackoffSeconds))
        };
        _matrix = new DegradationMatrix(options.Accounts, options.ModelCandidates, options.DegradationStrategy);
        _fileSystem = deps.FileSystem ?? new SystemFileSystem();
        _fallback = new FallbackBilling(_options, () => _matrix, _clock, Raise);
        _gitWorkflow = new GitWorkflow(_options, deps.DiffCollector, deps.Git, deps.MarkdownWriter, _logger);
        _report = new BatchReportBuilder(_clock, () => _options, () => _tasks, () => Status,
            () => _gitWorkflow.Diffs, BuildFallbackSummary);
        _notifier = new BatchNotifier(_options, _notifications, _notificationDispatcher, () => Status,
            () => _tasks, _clock, _logger);
        _preflight = new StartupPreflight(_options, _taskStore, _fileSystem, _adapter, _clock, _cooldowns,
            Raise, ResolveWorkspacePath, _vault);
        // P-02 协作者：持久化 / 凭证切换 / 生命周期编排。构造顺序须满足 runner/dispatcher→lifecycle→runner 的依赖
        // （runner/dispatcher 上下文注入依赖 persistence/accountSwitch/lifecycle；lifecycle 又依赖 dispatcher/waitCoordinator）。
        _runState = new BatchLifecycle.BatchRunState();
        _coordinator = new DispatchCoordinator();
        _persistence = new BatchPersistence(_taskStore, _cooldownStore, _logger, _cooldowns, _clock, _options,
            _tasks, () => TasksChanged?.Invoke(this, EventArgs.Empty), Raise);
        _accountSwitch = new AccountSwitchCoordinator(new AccountSwitchContext
        {
            Options = _options,
            Vault = _vault,
            Report = _report,
            Logger = _logger,
            Clock = _clock,
            Evaluator = _evaluator,
            MatrixProvider = () => _matrix,
            RebuildMatrix = RebuildMatrix,
            Raise = Raise
        });
        _dispatcher = new TaskDispatcher(BuildTaskDispatcherContext());
        _waitCoordinator = new WaitCoordinator(BuildWaitCoordinatorContext());
        _lifecycle = new BatchLifecycle(new BatchLifecycleContext
        {
            Options = _options,
            Adapter = _adapter,
            Clock = _clock,
            Evaluator = _evaluator,
            Cooldowns = _cooldowns,
            Classifier = _classifier,
            GateEvaluator = _gateEvaluator,
            Tasks = _tasks,
            MatrixProvider = () => _matrix,
            Report = _report,
            Notifier = _notifier,
            Logger = _logger,
            Dispatcher = _dispatcher,
            WaitCoordinator = _waitCoordinator,
            AccountSwitch = _accountSwitch,
            Persistence = _persistence,
            RunState = _runState,
            Vault = _vault,
            WaitForRestore = WaitForRestoreAsync,
            Raise = Raise,
            SetStatus = SetStatus,
            GetStatus = () => Status,
            SetReport = r => Report = r,
            RaiseTaskChanged = RaiseTaskChanged,
            ResolveWorkspace = ResolveWorkspacePath,
            RaiseBlocked = RaiseBlocked,
            Metrics = _metrics
        });
        _runner = new TaskRunner(BuildTaskRunnerContext());
    }
    /// <summary>构造等待协调上下文（把引擎的等待能力注入 <see cref="WaitCoordinator"/>，避免反向耦合）。</summary>
    private WaitCoordinatorContext BuildWaitCoordinatorContext() => new()
    {
        Clock = _clock,
        Evaluator = _evaluator,
        MatrixProvider = () => _matrix,
        Report = _report,
        Logger = _logger,
        TaskWaitSeconds = _taskWaitSeconds,
        StatsLock = _statsLock,
        Raise = Raise,
        SetStatus = SetStatus,
        CurrentTaskIdProvider = () => _runState.CurrentTaskId
    };
    /// <summary>构造派发上下文（把引擎的批处理级协调能力注入 <see cref="ITaskDispatcher"/>，避免反向耦合）。</summary>
    private TaskDispatcherContext BuildTaskDispatcherContext() => new()
    {
        Options = _options,
        Tasks = _tasks,
        Clock = _clock,
        Logger = _logger,
        RunTask = (task, ct) => _runner.RunSafeAsync(task, ct),
        EnterWriteGate = _persistence.EnterWriteGateAsync,
        Persist = _persistence.PersistAsync,
        StopAfterCurrentTask = () => _runState.StopAfterCurrentTask,
        Raise = Raise,
        SetStatus = SetStatus,
        RaiseBlocked = RaiseBlocked,
        RaiseTaskChanged = RaiseTaskChanged,
        Coordinator = _coordinator,
        PerformUnifiedSwitch = _accountSwitch.SwitchAccountAsync
    };
    /// <summary>构造单任务执行上下文（把引擎的批处理级协调能力注入 <see cref="ITaskRunner"/>，避免反向耦合）。</summary>
    private TaskRunnerContext BuildTaskRunnerContext() => new()
    {
        Options = _options,
        Adapter = _adapter,
        MatrixProvider = () => _matrix,
        Evaluator = _evaluator,
        Cooldowns = _cooldowns,
        RetryPolicy = _retryPolicy,
        Clock = _clock,
        Logger = _logger,
        Vault = _vault,
        Report = _report,
        Notifier = _notifier,
        Fallback = _fallback,
        GitWorkflow = _gitWorkflow,
        FileSystem = _fileSystem,
        TaskWaitSeconds = _taskWaitSeconds,
        StatsLock = _statsLock,
        ResolveWorkspace = ResolveWorkspacePath,
        SaveCooldowns = _persistence.SaveCooldownsAsync,
        Persist = _persistence.PersistAsync,
        EnterWriteGate = _persistence.EnterWriteGateAsync,
        Raise = Raise,
        SetStatus = SetStatus,
        RaiseBlocked = RaiseBlocked,
        RaiseTaskChanged = RaiseTaskChanged,
        SetCurrentTaskId = id => _runState.CurrentTaskId = id,
        WaitForRestore = WaitForRestoreAsync,
        ExecuteWorkflow = _lifecycle.ExecuteWorkflowAsync,
        GetPendingCount = () => _tasks.Count(t => !t.IsTerminal),
        Coordinator = _coordinator,
        Metrics = _metrics,
        DeferredAccountSwitchEnabled = _options.Concurrency > 1 && _vault is not null
    };

    // ---------------- 公开状态 ----------------
    public RunOptions Options => _options;
    public IReadOnlyList<TaskItem> Tasks => _tasks;
    public DegradationMatrix Matrix => _matrix;
    public QuotaCooldownRegistry Cooldowns => _cooldowns;
    public BatchStatus Status { get; private set; } = BatchStatus.Idle;
    /// <summary>本批跳过的已完成任务数（ForceRerun 时为 0；P1-1 拆分后由报告协作者持有）。</summary>
    public int SkippedDoneCount => _report.SkippedDoneCount;
    public BatchReport? Report { get; private set; }
    /// <inheritdoc />
    public string RunId => _runId;
    /// <inheritdoc />
    public RunMetrics Metrics => _metrics;
    public string? CurrentTaskId => _runState.CurrentTaskId;
    /// <summary>当前是否存在在飞任务（REQ-021 手动切换禁用闸）。</summary>
    public bool HasInFlight => _coordinator?.HasInFlight ?? false;
    /// <summary>全局派发是否处于暂停态（REQ-021：任一在飞槽位冻结后进入，统一切换完成前持续）。</summary>
    public bool IsDispatchHalted => _coordinator?.DispatchHalted ?? false;
    /// <summary>是否允许手动「切换到此账号」（REQ-021：并发且有在飞时禁用；串行态或 in-flight=0 时允许）。</summary>
    public bool CanManualSwitchAccount => _options.Concurrency <= 1 || !HasInFlight;
    public event EventHandler<BatchEvent>? EventRaised;
    public event EventHandler? TasksChanged;
    // ---------------- 生命周期 ----------------
    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await _persistence.LoadCooldownsAsync(cancellationToken).ConfigureAwait(false);
    /// <summary>加载任务文件（REQ-001 / REQ-001a）。</summary>
    public async Task<IReadOnlyList<TaskItem>> LoadTasksAsync(CancellationToken cancellationToken = default) =>
        await _persistence.LoadTasksAsync(cancellationToken).ConfigureAwait(false);
    /// <summary>启动前预检（REQ-010）。委托 <see cref="IPreflight"/> 协作者（P1-1 拆分产物 ⑥）。</summary>
    public async Task<PreflightReport> PreflightAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await _preflight.RunAsync(cancellationToken).ConfigureAwait(false);
        _matrix = outcome.Matrix; // 反映预检期停用的账号
        if (outcome.Loaded)
        {
            using (await _persistence.EnterWriteGateAsync().ConfigureAwait(false))
            {
                _tasks.Clear();
                _tasks.AddRange(outcome.Tasks);
            }
        }
        return outcome.Report;
    }
    /// <summary>启动或续跑（REQ-008）。委托 <see cref="BatchLifecycle"/>（P-02 拆分产物）。</summary>
    public Task<BatchReport> StartAsync(bool waitIfUnavailable = false, CancellationToken cancellationToken = default) =>
        _lifecycle.RunAsync(waitIfUnavailable, cancellationToken);
    /// <summary>终止（REQ-008）。默认立即安全中断；<paramref name="waitCurrentTask"/> 时等待当前任务完成。</summary>
    public Task TerminateAsync(bool waitCurrentTask = false)
    {
        if (Status is not (BatchStatus.Running or BatchStatus.Waiting or BatchStatus.Blocked))
        {
            return Task.CompletedTask;
        }
        _runState.TerminateRequested = true;
        _waitCoordinator.CancelWaiting();
        if (waitCurrentTask && _runState.CurrentTaskId is not null)
        {
            _runState.StopAfterCurrentTask = true;
            Raise(new BatchEvent(BatchEventKind.Info, _clock.Now, "已请求终止：等待当前任务完成后停止"));
        }
        else
        {
            _runState.RunCts?.Cancel();
        }

        return Task.CompletedTask;
    }
    /// <summary>取消 waiting（REQ-003b：取消后转入 blocked）。</summary>
    public void CancelWaiting()
    {
        if (Status != BatchStatus.Waiting)
        {
            return;
        }

        _waitCoordinator.CancelWaiting();
    }

    /// <summary>配置变更通知：waiting 期间补充额度/新增账号模型/修改时段/手动重置冷却后，立即重评可用性，已可用则提前退出 waiting（REQ-003b / REQ-004c）。</summary>
    public void NotifyConfigurationChanged()
    {
        RebuildMatrix();
        _waitCoordinator.SignalWake();
    }

    /// <summary>手动重置某组合的冷却标记（REQ-004c）。</summary>
    public void ResetCooldown(string accountId, string modelName)
    {
        _cooldowns.Clear(accountId, modelName);
        NotifyConfigurationChanged();
    }

    public void ResetAllCooldowns()
    {
        _cooldowns.ClearAll();
        NotifyConfigurationChanged();
    }

    // ---------------- 辅助 ----------------

    /// <summary>三级覆盖：单任务 &gt; 本批默认 &gt; 全局默认（REQ-003a）。</summary>
    public string? ResolveWorkspacePath(string? workspace) => WorkspaceResolver.Resolve(_options, workspace);
    private void RebuildMatrix() =>
        _matrix = new DegradationMatrix(_options.Accounts, _options.ModelCandidates, _options.DegradationStrategy);

    /// <summary>等待可用性恢复：委托 <see cref="WaitCoordinator"/>（P-01 抽取），公开签名不变，供生命周期与单任务/工作流执行器复用同一等待语义。</summary>
    private Task<bool> WaitForRestoreAsync(RestoreCandidate candidate, string? taskId, CancellationToken ct) =>
        _waitCoordinator.WaitForRestoreAsync(candidate, taskId, ct);

    private void RaiseBlocked(string reason, string? taskId = null)
    {
        _report.AddTimeline("阻塞", reason, taskId, null, null);
        Raise(new BatchEvent(BatchEventKind.BatchBlocked, _clock.Now, reason, taskId)
        {
            BatchStatus = BatchStatus.Blocked,
            Detail = reason
        });
    }

    private void SetStatus(BatchStatus status)
    {
        Status = status;
        Raise(new BatchEvent(BatchEventKind.StatusChanged, _clock.Now, $"批处理状态：{status}",
            null)
        { BatchStatus = status });
    }

    private void RaiseTaskChanged(TaskItem task) =>
        Raise(new BatchEvent(BatchEventKind.Info, _clock.Now, string.Empty, task.Id) { Task = task });

    private void Raise(BatchEvent ev)
    {
        // P-10：关联标识在此统一填充——所有事件（含 UI 订阅到的事件流）天然带 RunId，
        // 任一写入点都不必、也无法"忘记"带关联字段。
        var tagged = string.IsNullOrEmpty(ev.RunId) ? ev with { RunId = _runId } : ev;
        EventRaised?.Invoke(this, tagged);
        _logger.Write(tagged);
    }

    /// <summary>兜底用量汇总（P1-1 拆分后由 <see cref="IFallbackBilling"/> 记账，此处供报告协作者读取）。</summary>
    private FallbackSummary BuildFallbackSummary() => _fallback.BuildSummary();

    /// <summary>释放持久化门、等待门与运行级 <see cref="CancellationTokenSource"/>（CA1001）；仅在所有任务已终态、批处理已停止后调用。</summary>
    public void Dispose()
    {
        _persistence.Dispose();
        _waitCoordinator.Dispose();
        _runState.RunCts?.Dispose();
        _runState.RunCts = null;
        GC.SuppressFinalize(this);
    }
}
