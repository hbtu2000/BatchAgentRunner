using System.Diagnostics.CodeAnalysis;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
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
/// 批处理调度引擎（PRD 第 10 章架构方法 + 附录 B 降级状态机）。
/// 串行 / 受控并发（REQ-012）共用同一套可用性视图与单写者回写。
/// </summary>
/// <remarks>
/// CA1001（P0-12）：类持有 <see cref="SemaphoreSlim"/> 门与 <see cref="CancellationTokenSource"/>，
/// 实现 <see cref="IDisposable"/> 由宿主在用毕（替换/退出）时释放。引擎为「每次运行一个实例」的
/// 短生命周期对象（EngineHost 重建引擎时先释放旧实例）。
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
    /// <summary>
    /// 任务状态写入区（P0-4「读改写同锁」）。用 <see cref="SemaphoreSlim"/> 而非 <c>lock</c>：
    /// 落盘序列化必须在持锁期间 <c>await</c>（<c>lock</c> 不允许），且 <see cref="TaskItem"/> 可变，
    /// 浅拷贝快照挡不住并发写入——只有让「状态写入」与「序列化读取」互斥才能落盘一致状态。
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _statsLock = new();
    /// <summary>持久化单写者门（P0-4）：同一时刻只允许一个写者落盘任务文件。</summary>
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly SemaphoreSlim _waitGate = new(1, 1);

    private DegradationMatrix _matrix;
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource? _wake;
    private bool _waitingCancelled;
    private bool _terminateRequested;
    private bool _stopAfterCurrentTask;
    private string? _currentTaskId;
    /// <summary>
    /// 兜底模型选择与计费协作者（P1-1 拆分产物 ③，§3.4 <c>IFallbackBilling</c>）：
    /// 独占兜底成本/用量状态，承载 P0-1（单任务成本截断）与 P0-5（跨任务累计同锁）不变量。
    /// </summary>
    /// <remarks>
    /// CA1859（§4.7 逃逸阀）：字段刻意保持接口类型——P1-1 拆分后该协作者须可被测试替身替换，
    /// 拒绝为微性能向具体类型回退（同 EngineHost CA1859 豁免的架构方向理由）。
    /// </remarks>
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能",
        Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假计费验证不变量），拒绝向具体类型回退")]
    private readonly IFallbackBilling _fallback;
    /// <summary>
    /// 报告聚合协作者（P1-1 拆分产物 ⑤，§3.4 <c>IReportBuilder</c>）：
    /// 独占用量统计 / 时间线 / 等待累计，负责组装 <see cref="BatchReport"/>。
    /// </summary>
    /// <remarks>
    /// CA1859（§4.7 逃逸阀）：同 <see cref="_fallback"/>——接口字段是拆分后的测试缝。
    /// </remarks>
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能",
        Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假报告构建器验证聚合），拒绝向具体类型回退")]
    private readonly IBatchReportBuilder _report;
    /// <summary>单任务级暂停等待累计（DESIGN-额度耗尽暂停续跑：从任务时长中剔除）。key 不区分大小写。</summary>
    private readonly Dictionary<string, double> _taskWaitSeconds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _idSnapshot;

    // ---------------- 二期扩展（全部可选，向后兼容） ----------------
    private readonly List<NotificationRule> _notifications;
    private readonly SessionResumePolicy _sessionPolicy;
    private readonly INotificationDispatcher? _notificationDispatcher;
    private readonly GateEvaluator _gateEvaluator;
    /// <summary>文件系统探测端口（P1-10）：引擎零直接 IO，任务文件/工作空间存在性均经此抽象。</summary>
    private readonly IFileSystem _fileSystem;
    /// <summary>
    /// Git 工作流协作者（P1-1 拆分产物 ④，§3.4 <c>IGitWorkflow</c>）：
    /// 项目级任务建分支/开 PR、diff 采集、Markdown 原地回写（均尽力而为）。
    /// </summary>
    /// <remarks>
    /// CA1859（§4.7 逃逸阀）：同 <see cref="_fallback"/>——接口字段是拆分后的测试缝。
    /// </remarks>
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能",
        Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假 Git 工作流验证尽力而为语义），拒绝向具体类型回退")]
    private readonly IGitWorkflow _gitWorkflow;
    /// <summary>
    /// 批处理通知协作者（P1-1 拆分产物 ⑤）：按触发时机组装通知上下文并分发。
    /// </summary>
    /// <remarks>
    /// CA1859（§4.7 逃逸阀）：同 <see cref="_fallback"/>——接口字段是拆分后的测试缝。
    /// </remarks>
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能",
        Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假通知器验证分发时机），拒绝向具体类型回退")]
    private readonly IBatchNotifier _notifier;
    /// <summary>
    /// 启动前预检协作者（P1-1 拆分产物 ⑥，§3.4 <c>IPreflight</c>）：承接「启动前预检（REQ-010）」全部逻辑。
    /// </summary>
    /// <remarks>
    /// CA1859（§4.7 逃逸阀）：同 <see cref="_fallback"/>——接口字段是拆分后的测试缝。
    /// </remarks>
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能",
        Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假预检验证报告契约），拒绝向具体类型回退")]
    private readonly IPreflight _preflight;
    /// <summary>
    /// 单任务执行协作者（P1-1 拆分产物 ②，§3.4 <c>ITaskRunner</c>）：承载「单任务状态机
    /// （重试 / 退避 / 降级 / 超时）+ 终态写入 / 兜底计费 / 凭证切换 / 组合切换」全部逻辑（P1-2 状态机外置）。
    /// 引擎保留批处理级协调（落盘单写者、waiting 门、状态广播），经 <see cref="TaskRunnerContext"/> 注入。
    /// </summary>
    /// <remarks>
    /// CA1859（§4.7 逃逸阀）：同 <see cref="_fallback"/>——接口字段是拆分后的测试缝。
    /// </remarks>
    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能",
        Justification = "接口字段是 P1-1 拆分后的测试缝（可注入假单任务执行器验证状态机不变量），拒绝向具体类型回退")]
    private readonly ITaskRunner _runner;

    public BatchEngine(
        RunOptions options,
        IAgentCliAdapter adapter,
        ITaskFileStore taskStore,
        ICooldownStore cooldownStore,
        IRunLogger logger,
        IClock? clock = null,
        QuotaErrorClassifier? classifier = null,
        ICredentialVault? credentialVault = null,
        IEnumerable<NotificationRule>? notifications = null,
        IDiffCollector? diffCollector = null,
        IGitIntegration? git = null,
        INotificationDispatcher? notificationDispatcher = null,
        IMarkdownInPlaceWriter? markdownWriter = null,
        GateEvaluator? gateEvaluator = null,
        IProcessRunner? processRunner = null,
        IFileSystem? fileSystem = null)
    {
        _options = options;
        _adapter = adapter;
        _taskStore = taskStore;
        _cooldownStore = cooldownStore;
        _logger = logger;
        _vault = credentialVault;
        _notifications = notifications?.ToList() ?? options.Notifications?.ToList() ?? new List<NotificationRule>();
        _notificationDispatcher = notificationDispatcher;
        _gateEvaluator = gateEvaluator ?? new GateEvaluator(processRunner ?? new NullProcessRunner());
        _clock = clock ?? new SystemClock();
        _classifier = classifier ?? new QuotaErrorClassifier();
        _cooldowns = new QuotaCooldownRegistry(_clock, options.DefaultCooldown);
        _evaluator = new AvailabilityEvaluator(_cooldowns);
        _retryPolicy = new RetryPolicy
        {
            MaxRetries = options.MaxRetriesPerTask,
            RateLimitBackoffRetries = options.RateLimitBackoffRetries,
            InitialBackoff = TimeSpan.FromSeconds(Math.Max(1, options.RateLimitInitialBackoffSeconds))
        };
        _matrix = new DegradationMatrix(options.Accounts, options.ModelCandidates, options.DegradationStrategy);
        _sessionPolicy = new SessionResumePolicy(options.EnableSessionResume);
        _fileSystem = fileSystem ?? new SystemFileSystem();
        _fallback = new FallbackBilling(_options, () => _matrix, _clock, Raise);
        _gitWorkflow = new GitWorkflow(_options, diffCollector, git, markdownWriter, _logger);
        _report = new BatchReportBuilder(_clock, () => _options, () => _tasks, () => Status,
            () => _gitWorkflow.Diffs, BuildFallbackSummary);
        _notifier = new BatchNotifier(_options, _notifications, _notificationDispatcher, () => Status,
            () => _tasks, _clock, _logger);
        _preflight = new StartupPreflight(_options, _taskStore, _fileSystem, _adapter, _clock, _cooldowns,
            Raise, ResolveWorkspacePath, _vault);
        _runner = new TaskRunner(BuildTaskRunnerContext());
    }

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
        SaveCooldowns = SaveCooldownsAsync,
        Persist = PersistAsync,
        EnterWriteGate = EnterWriteGateAsync,
        Raise = Raise,
        SetStatus = SetStatus,
        RaiseBlocked = RaiseBlocked,
        RaiseTaskChanged = RaiseTaskChanged,
        SetCurrentTaskId = id => _currentTaskId = id,
        WaitForRestore = WaitForRestoreAsync,
        ExecuteWorkflow = ExecuteWorkflowAsync,
        GetPendingCount = () => _tasks.Count(t => !t.IsTerminal)
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

    public string? CurrentTaskId => _currentTaskId;

    /// <summary>引擎事件（界面与日志订阅）。</summary>
    public event EventHandler<BatchEvent>? EventRaised;

    /// <summary>任务集合/任务项发生变化（界面整体刷新）。</summary>
    public event EventHandler? TasksChanged;

    // ---------------- 生命周期 ----------------

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var records = await _cooldownStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var r in records)
        {
            _cooldowns.Record(r.AccountId, r.ModelName, r.ResetAt, _clock.Now, r.RawValue);
        }

        if (records.Count > 0)
        {
            Raise(new BatchEvent(BatchEventKind.Info, _clock.Now,
                $"已加载 {records.Count} 条额度冷却记录（重启后仍然有效）"));
        }
    }

    /// <summary>加载任务文件（REQ-001 / REQ-001a）。</summary>
    public async Task<IReadOnlyList<TaskItem>> LoadTasksAsync(CancellationToken cancellationToken = default)
    {
        var result = await _taskStore.LoadAsync(_options.TaskFilePath, cancellationToken).ConfigureAwait(false);

        using (await EnterWriteGateAsync().ConfigureAwait(false))
        {
            _tasks.Clear();
            _tasks.AddRange(result.Tasks);
        }

        foreach (var issue in result.Issues)
        {
            Raise(new BatchEvent(
                string.Equals(issue.Level, "error", StringComparison.OrdinalIgnoreCase)
                    ? BatchEventKind.Warning
                    : BatchEventKind.Info,
                _clock.Now, issue.Message, issue.TaskId));
        }

        TasksChanged?.Invoke(this, EventArgs.Empty);
        return _tasks;
    }

    /// <summary>启动前预检（REQ-010）。委托 <see cref="IPreflight"/> 协作者（P1-1 拆分产物 ⑥）。</summary>
    public async Task<PreflightReport> PreflightAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await _preflight.RunAsync(cancellationToken).ConfigureAwait(false);

        // 同步预检重建后的降级矩阵（原 PreflightAsync 内 RebuildMatrix 的语义：反映预检期停用的账号）
        _matrix = outcome.Matrix;

        // 任务文件成功加载才覆盖既有 _tasks（与原行为一致：加载失败不破坏已加载的任务集合）
        if (outcome.Loaded)
        {
            using (await EnterWriteGateAsync().ConfigureAwait(false))
            {
                _tasks.Clear();
                _tasks.AddRange(outcome.Tasks);
            }
        }

        return outcome.Report;
    }

    /// <summary>
    /// 启动或续跑（REQ-008）。<paramref name="waitIfUnavailable"/> 为 true 时，
    /// 启动前全部组合不可用会进入 waiting（REQ-010 用户选择「等待自动启动」），否则直接 blocked 退出。
    /// </summary>
    public async Task<BatchReport> StartAsync(bool waitIfUnavailable = false, CancellationToken cancellationToken = default)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _runCts.Token;
        var startedAt = _clock.Now;
        _terminateRequested = false;
        _stopAfterCurrentTask = false;
        _waitingCancelled = false;

        await LoadTasksAsync(token).ConfigureAwait(false);
        await _taskStore.BackupAsync(_options.TaskFilePath, Math.Max(0, _options.BackupRetention), token)
            .ConfigureAwait(false);

        // ID 快照校验（REQ-001a）
        CheckIdSnapshot();

        var queue = _tasks
            .Where(t => _options.ForceRerun
                ? true
                : t.Status is TaskStatus.Pending or TaskStatus.Interrupted)
            .ToList();

        // 二期 DAG：存在任务依赖时按拓扑序派发，保证依赖先于其下游执行；环在加载期快速失败
        if (queue.Any(t => t.DependsOn is { Count: > 0 }))
        {
            try
            {
                queue = DagScheduler.TopoSort(queue).ToList();
            }
            catch (DagCycleException ex)
            {
                SetStatus(BatchStatus.Blocked);
                RaiseBlocked($"任务依赖存在环：{ex.Message}");
                await PersistAsync(token).ConfigureAwait(false);
                Report = _report.Build(startedAt);
                return Report;
            }
        }

        _report.SetSkippedDoneCount(_options.ForceRerun
            ? 0
            : _tasks.Count(t => t.Status is TaskStatus.Done or TaskStatus.Failed));
        var SkippedDoneCount = _report.SkippedDoneCount;

        if (SkippedDoneCount > 0)
        {
            Raise(new BatchEvent(BatchEventKind.Info, _clock.Now, $"已跳过 {SkippedDoneCount} 条已完成任务"));
        }

        SetStatus(BatchStatus.Running);

        // 启动前预检闸门（REQ-010）：CLI 缺失属环境错误，快速失败、不进入执行循环，
        // 任务保持 pending（不重试、不标 failed）。无恢复时间点语义，故不适用「等待自动启动」。
        var env = await _adapter.ValidateEnvironmentAsync(token).ConfigureAwait(false);
        if (!env.Ok)
        {
            SetStatus(BatchStatus.Blocked);
            RaiseBlocked($"启动前预检未通过：{env.Message}");
            await PersistAsync(token).ConfigureAwait(false);
            Report = _report.Build(startedAt);
            return Report;
        }

        // 启动前不可用处理
        if (!_matrix.IsEmpty && _matrix.AllUnavailable(_clock.Now, _evaluator))
        {
            var earliest = _matrix.EarliestRestore(_clock.Now, _evaluator);
            // 额度耗尽场景即便未勾选「等待自动启动」也自动挂起等待（必然恢复，DESIGN-额度耗尽暂停续跑）
            if (earliest is not null && (waitIfUnavailable || IsQuotaUnboundedWait(earliest, _clock.Now)))
            {
                var ok = await WaitForRestoreAsync(earliest, null, token).ConfigureAwait(false);
                if (!ok)
                {
                    SetStatus(BatchStatus.Blocked);
                    RaiseBlocked("启动前等待被取消");
                }
            }
            else
            {
                SetStatus(BatchStatus.Blocked);
                RaiseBlocked(earliest is null
                    ? "所有候选组合当前不可用"
                    : $"所有候选组合当前不可用，最早恢复时间 {earliest.RestoreAt:HH:mm}");
            }
        }

        if (Status == BatchStatus.Running)
        {
            await DispatchAsync(queue, token).ConfigureAwait(false);
        }

        // 终态判定
        if (_terminateRequested)
        {
            SetStatus(BatchStatus.Terminated);
        }
        else if (Status is BatchStatus.Running or BatchStatus.Waiting)
        {
            var allTerminal = _tasks.All(t => t.IsTerminal);
            if (allTerminal)
            {
                SetStatus(BatchStatus.Completed);
                Raise(new BatchEvent(BatchEventKind.BatchCompleted, _clock.Now,
                    $"批处理完成：成功 {_tasks.Count(t => t.Status == TaskStatus.Done)}，失败 {_tasks.Count(t => t.Status == TaskStatus.Failed)}，中断 {_tasks.Count(t => t.Status == TaskStatus.Interrupted)}，待人工 {_tasks.Count(t => t.Status == TaskStatus.NeedsAttention)}，阻塞 {_tasks.Count(t => t.Status == TaskStatus.Blocked)}")
                {
                    BatchStatus = BatchStatus.Completed
                });
            }
            else
            {
                SetStatus(BatchStatus.Blocked);
            }
        }

        await PersistAsync(token).ConfigureAwait(false);
        Report = _report.Build(startedAt);

        // 二期：批处理终态通知推送（REQ-018；按规则触发，失败不阻断）
        await _notifier.DispatchAsync(
            Status switch
            {
                BatchStatus.Completed => NotifyTrigger.BatchCompleted,
                BatchStatus.Blocked => NotifyTrigger.BatchBlocked,
                BatchStatus.Terminated => NotifyTrigger.BatchTerminated,
                _ => (NotifyTrigger?)null
            },
            startedAt).ConfigureAwait(false);

        return Report;
    }

    /// <summary>终止（REQ-008）。默认立即安全中断；<paramref name="waitCurrentTask"/> 时等待当前任务完成。</summary>
    public Task TerminateAsync(bool waitCurrentTask = false)
    {
        if (Status is not (BatchStatus.Running or BatchStatus.Waiting or BatchStatus.Blocked))
        {
            return Task.CompletedTask;
        }

        _terminateRequested = true;
        _waitingCancelled = true;
        _wake?.TrySetResult();

        if (waitCurrentTask && _currentTaskId is not null)
        {
            _stopAfterCurrentTask = true;
            Raise(new BatchEvent(BatchEventKind.Info, _clock.Now, "已请求终止：等待当前任务完成后停止"));
        }
        else
        {
            _runCts?.Cancel();
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

        _waitingCancelled = true;
        _wake?.TrySetResult();
    }

    /// <summary>
    /// 配置变更通知：waiting 期间补充额度/新增账号模型/修改时段/手动重置冷却后，
    /// 立即重新评估可用性，已可用则提前退出 waiting（REQ-003b / REQ-004c）。
    /// </summary>
    public void NotifyConfigurationChanged()
    {
        RebuildMatrix();
        _wake?.TrySetResult();
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

    // ---------------- 调度主循环 ----------------

    private async Task DispatchAsync(List<TaskItem> queue, CancellationToken ct)
    {
        // 二期：含任务依赖的批次强制串行派发——并发下依赖终态不可预测，串行才能保证 DAG 拓扑序语义
        var hasDependencies = queue.Any(t => t.DependsOn is { Count: > 0 });
        if (hasDependencies && _options.Concurrency > 1)
        {
            Raise(new BatchEvent(BatchEventKind.Info, _clock.Now,
                "批次含任务依赖（DAG），已强制串行派发以保证依赖顺序"));
        }

        if (_options.Concurrency <= 1 || hasDependencies)
        {
            foreach (var task in queue)
            {
                if (ct.IsCancellationRequested || _stopAfterCurrentTask)
                {
                    break;
                }

                // 二期 DAG：依赖未满足时不进入执行，按依赖失败策略处理
                var verdict = EvaluateDependency(task, out var blockingDep);
                if (verdict != DependencyVerdict.Satisfied)
                {
                    if (!await ApplyDependencyBlockAsync(task, blockingDep, ct).ConfigureAwait(false))
                    {
                        break; // 策略=终止整批
                    }

                    continue; // 已标记 Blocked；其下游轮到时因本任务 Blocked 而逐层阻断
                }

                var outcome = await _runner.RunSafeAsync(task, ct).ConfigureAwait(false);
                if (outcome is TaskOutcome.Blocked or TaskOutcome.Terminated or TaskOutcome.StopBatch)
                {
                    break;
                }
            }
        }
        else
        {
            // 受控并发（REQ-012）：无依赖批次；停止派发 → 逐一回收 → 全部落盘
            using var semaphore = new SemaphoreSlim(_options.Concurrency);
            var stopDispatch = false;

            var workers = queue.Select(async task =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (stopDispatch || ct.IsCancellationRequested || _stopAfterCurrentTask)
                    {
                        return;
                    }

                    var outcome = await _runner.RunSafeAsync(task, ct).ConfigureAwait(false);
                    if (outcome is TaskOutcome.Blocked or TaskOutcome.Terminated or TaskOutcome.StopBatch)
                    {
                        stopDispatch = true;
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }

    // ---------------- 二期：工作流执行 ----------------

    /// <summary>
    /// 二期：工作流任务执行（多阶段顺序驱动 + 质量门禁 + 修订循环）。
    /// 委托给自包含的 <see cref="WorkflowExecutor"/>（其内部复用一期降级矩阵/免费时段/额度冷却/兜底语义），
    /// 引擎只负责落盘与终态映射，不改动一期单指令执行路径。
    /// </summary>
    private async Task<TaskOutcome> ExecuteWorkflowAsync(TaskItem task, CancellationToken ct)
    {
        var outcome = await new WorkflowExecutor(BuildWorkflowContext()).RunAsync(task, ct).ConfigureAwait(false);

        using (await EnterWriteGateAsync().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
        {
            task.Status = outcome.FinalStatus;
            task.Result = Truncate(outcome.Result, _options.ResultMaxKB * 1024);
            task.Error = outcome.Error;
            task.StageResults = outcome.StageResults;
            task.ModelUsed = outcome.ModelUsed;
            task.AccountUsed = outcome.AccountUsed;
            task.ExpertUsed = outcome.ExpertUsed;
            task.FallbackUsed = outcome.FallbackUsed;
            task.FallbackCost = outcome.FallbackCost;
            task.Finish(outcome.FinalStatus, _clock.Now);
        }

        RaiseTaskChanged(task);
        _logger.WriteTaskFinished(task.Id, outcome.FinalStatus, task.DurationSeconds, task.RetryCount);
        Raise(new BatchEvent(BatchEventKind.TaskFinished, _clock.Now,
            $"工作流任务 {task.Id} 结束：{outcome.FinalStatus}", task.Id)
        { Task = task });

        await PersistAsync(CancellationToken.None).ConfigureAwait(false);

        if (outcome.FinalStatus == TaskStatus.NeedsAttention)
        {
            await _notifier.DispatchAsync(NotifyTrigger.NeedsAttention, null, outcome.Error).ConfigureAwait(false);
        }

        return outcome.FinalStatus switch
        {
            TaskStatus.Interrupted => TaskOutcome.Terminated,
            TaskStatus.Failed => _options.StopOnFailure ? TaskOutcome.StopBatch : TaskOutcome.Finished,
            _ => TaskOutcome.Finished // Done / NeedsAttention / Blocked 均不影响后续任务派发
        };
    }

    /// <summary>构造工作流执行上下文（把引擎的一期内核能力注入二期执行器）。</summary>
    private WorkflowEngineContext BuildWorkflowContext() => new()
    {
        Options = _options,
        Adapter = _adapter,
        Matrix = _matrix,
        Evaluator = _evaluator,
        Cooldowns = _cooldowns,
        Classifier = _classifier,
        Clock = _clock,
        Logger = _logger,
        GateEvaluator = _gateEvaluator,
        OnEvent = Raise,
        OnTaskChanged = RaiseTaskChanged,
        Persist = () => PersistAsync(CancellationToken.None),
        TrackUsage = (account, model, usage) =>
        {
            _report.TrackUsage(account, model, usage);
            return Task.CompletedTask;
        },
        EnsureCredential = _vault is not null ? EnsureLiveCredentialAsync : null,
        SessionPolicy = _sessionPolicy,
        DefaultMaxGateLoops = _options.DefaultMaxGateLoops
    };

    // ---------------- 二期：DAG 依赖 ----------------

    /// <summary>依赖判定结果。</summary>
    private enum DependencyVerdict
    {
        /// <summary>依赖已满足，可以执行。</summary>
        Satisfied,
        /// <summary>依赖未成功（失败/阻塞/悬空），本任务应被阻断或终止整批。</summary>
        Blocked,
        /// <summary>依赖尚未到达终态（串行拓扑序下不出现，保守按阻断处理）。</summary>
        Wait
    }

    /// <summary>
    /// 判定任务依赖是否满足（二期 DAG）。策略=忽略依赖失败时恒返回 <see cref="DependencyVerdict.Satisfied"/>。
    /// </summary>
    private DependencyVerdict EvaluateDependency(TaskItem task, out string? blockingDep)
    {
        blockingDep = null;
        if (task.DependsOn is not { Count: > 0 })
        {
            return DependencyVerdict.Satisfied;
        }

        if (_options.DependencyFailurePolicy == DependencyFailurePolicy.SkipDependents)
        {
            return DependencyVerdict.Satisfied; // 忽略依赖失败，下游照常执行
        }

        var statusById = new Dictionary<string, TaskStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _tasks)
        {
            statusById[t.Id] = t.Status;
        }

        var blockOnNeedsAttention = _options.DependencyFailurePolicy == DependencyFailurePolicy.BlockDependents;
        var wait = false;
        foreach (var dep in task.DependsOn)
        {
            if (!statusById.TryGetValue(dep, out var st))
            {
                blockingDep = dep; // 悬空依赖：保守阻断
                return DependencyVerdict.Blocked;
            }

            if (st is TaskStatus.Failed or TaskStatus.Blocked)
            {
                blockingDep = dep;
                return DependencyVerdict.Blocked;
            }

            if (st == TaskStatus.NeedsAttention && blockOnNeedsAttention)
            {
                blockingDep = dep;
                return DependencyVerdict.Blocked;
            }

            if (st is not (TaskStatus.Done or TaskStatus.Failed or TaskStatus.Interrupted
                or TaskStatus.NeedsAttention or TaskStatus.Blocked))
            {
                wait = true; // 依赖尚未到达终态
            }
        }

        return wait ? DependencyVerdict.Wait : DependencyVerdict.Satisfied;
    }

    /// <summary>
    /// 依赖未满足时按策略处理本任务。返回 <c>false</c> 表示应终止整批派发。
    /// 默认策略（阻断下游）：把本任务标记 Blocked，其下游轮到时因本任务 Blocked 而逐层阻断。
    /// </summary>
    private async Task<bool> ApplyDependencyBlockAsync(TaskItem task, string? blockingDep, CancellationToken ct)
    {
        var reason = $"依赖任务 {blockingDep ?? "（未知）"} 未成功，本任务被阻断";

        if (_options.DependencyFailurePolicy == DependencyFailurePolicy.AbortBatch)
        {
            using (await EnterWriteGateAsync().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
            {
                task.Status = TaskStatus.Blocked;
                task.Error = reason;
                task.Finish(TaskStatus.Blocked, _clock.Now);
            }

            RaiseTaskChanged(task);
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            SetStatus(BatchStatus.Blocked);
            RaiseBlocked($"任务 {task.Id} {reason}，按依赖失败策略终止整批", task.Id);
            return false;
        }

        using (await EnterWriteGateAsync().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
        {
            task.Status = TaskStatus.Blocked;
            task.Error = reason;
            task.Finish(TaskStatus.Blocked, _clock.Now);
        }

        RaiseTaskChanged(task);
        _logger.WriteTaskFinished(task.Id, TaskStatus.Blocked, task.DurationSeconds, task.RetryCount);
        Raise(new BatchEvent(BatchEventKind.TaskFinished, _clock.Now,
            $"任务 {task.Id} {reason}", task.Id)
        { Task = task });
        await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    // ---------------- 二期：通知推送 ----------------

    // ---------------- waiting（REQ-003b / REQ-004c） ----------------

    private async Task<bool> WaitForRestoreAsync(RestoreCandidate candidate, string? taskId, CancellationToken ct)
    {
        await _waitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 先清取消标记再置 Waiting 状态：避免取消请求落在「已置状态、未清标记」的窗口内被误清
            // （界面轮询到 Waiting 后立刻点取消，若此时标记尚未清零，取消会丢失导致批处理跑完而非转阻塞）
            _waitingCancelled = false;
            SetStatus(BatchStatus.Waiting);
            var waitStart = _clock.Now;

            Raise(new BatchEvent(BatchEventKind.BatchWaiting, _clock.Now,
                DescribeWaiting(candidate), CurrentTaskId)
            {
                BatchStatus = BatchStatus.Waiting,
                ResumeAt = candidate.RestoreAt,
                WaitKind = candidate.WaitKind
            });
            _logger.WriteBatchWaiting(candidate.RestoreAt, candidate.Combo.Model.Name, candidate.WaitKind);

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    return false;
                }

                if (_waitingCancelled)
                {
                    return false;
                }

                var now = _clock.Now;
                if (now >= candidate.RestoreAt)
                {
                    break;
                }

                // 配置变更 / 补充额度后即时重评，已可用则提前退出
                if (_matrix.SelectFirst(now, _evaluator).Found)
                {
                    Raise(new BatchEvent(BatchEventKind.BatchResumed, now, "检测到可用组合，提前结束等待继续执行"));
                    _logger.WriteBatchResumed("配置变更或额度补充，提前退出等待");
                    break;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _wake = tcs;
                var remaining = candidate.RestoreAt - now;
                var slice = remaining > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : remaining;
                var delay = _clock.DelayAsync(slice, ct);
                await Task.WhenAny(delay, tcs.Task).ConfigureAwait(false);
            }

            var elapsed = (_clock.Now - waitStart).TotalSeconds;
            _report.AddWaitSeconds(candidate.WaitKind, elapsed);

            // 单任务级暂停累计（用于从任务时长中剔除，DESIGN-额度耗尽暂停续跑）
            if (!string.IsNullOrEmpty(taskId))
            {
                lock (_statsLock)
                {
                    _taskWaitSeconds.TryGetValue(taskId!, out var prior);
                    _taskWaitSeconds[taskId!] = prior + elapsed;
                }
            }

            SetStatus(BatchStatus.Running);
            Raise(new BatchEvent(BatchEventKind.BatchResumed, _clock.Now,
                $"已恢复执行（暂停 {elapsed / 60:0.0} 分钟，不计入任务时长）")
            { BatchStatus = BatchStatus.Running });
            _logger.WriteBatchResumed("到达恢复时间点");
            return true;
        }
        finally
        {
            _waitGate.Release();
        }
    }

    /// <summary>是否对「额度耗尽」场景放开等待上限（无限等待直到重置）。</summary>
    private bool IsQuotaUnboundedWait(RestoreCandidate? candidate, DateTimeOffset now) =>
        _options.WaitForQuotaReset
        && candidate is { WaitKind: WaitKind.QuotaReset }
        && _matrix.AllUnavailablePurelyByQuota(now, _evaluator);

    private static string DescribeWaiting(RestoreCandidate candidate) => candidate.WaitKind switch
    {
        WaitKind.FreeWindow =>
            $"当前无可用模型，将于 {candidate.RestoreAt:HH:mm} 进入下一免费时段（{candidate.Combo.Model.Name}），等待中，可取消",
        WaitKind.QuotaReset =>
            $"当前所有组合额度均已耗尽，将于 {candidate.RestoreAt:HH:mm} 重置恢复（{candidate.Combo.Model.Name}）；已暂停等待重置（暂停不计入时长），可取消",
        WaitKind.Mixed =>
            $"当前无可用组合（时段外 + 冷却中），将于 {candidate.RestoreAt:HH:mm} 恢复（{candidate.Combo.Model.Name}），等待中，可取消",
        _ => $"等待至 {candidate.RestoreAt:HH:mm} 后继续"
    };

    // ---------------- 兜底（REQ-004b） ----------------

    // ---------------- 任务状态写入 ----------------

    // ---------------- 辅助 ----------------

    /// <summary>三级覆盖：单任务 &gt; 本批默认 &gt; 全局默认（REQ-003a）。</summary>
    public string? ResolveWorkspacePath(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            var globalDefault = _options.Workspaces.FirstOrDefault(w => w.IsGlobalDefault);
            return globalDefault?.Path;
        }

        var named = _options.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, workspace, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return named.Path;
        }

        return Path.IsPathRooted(workspace) ? workspace : null;
    }

    /// <summary>
    /// 任务边界凭证切换（DESIGN §6）：请求执行前确保 Live 凭证属于该组合的账号；
    /// 账号变化时执行「保活回写 + 原子回填」，失败则停用该账号走既有降级逻辑。
    /// </summary>
    private async Task<bool> EnsureLiveCredentialAsync(TaskItem task, AgentCombo combo, CancellationToken ct)
    {
        if (_vault is null)
        {
            return true;
        }

        var current = _vault.CurrentLiveAccount(_options.AgentKind);
        if (string.Equals(current, combo.Account.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            await _vault.SwitchToAsync(_options.AgentKind, combo.Account.Id, "task-boundary", ct)
                .ConfigureAwait(false);
            Raise(new BatchEvent(BatchEventKind.AccountSwitched, _clock.Now,
                $"凭证已切换：{current ?? "（无）"} → {combo.Account.Label}（任务边界）",
                task.Id, combo.Account.Label, combo.Model.Name));
            _logger.WriteModelSwitched(current, null, combo.Account.Id, combo.Model.Name, "凭证切换", task.Id);
            return true;
        }
        catch (CredentialVaultException ex)
        {
            combo.Account.Enabled = false;
            Raise(new BatchEvent(BatchEventKind.Warning, _clock.Now,
                $"账号 {combo.Account.Label} 凭证切换失败，已停用并切换下一账号：{ex.Message}",
                task.Id, combo.Account.Label));
            return false;
        }
    }

    private void CheckIdSnapshot()
    {
        var current = _tasks.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_idSnapshot is null)
        {
            _idSnapshot = current;
            return;
        }

        var doneIds = _tasks.Where(t => t.Status == TaskStatus.Done).Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = _idSnapshot.Where(id => doneIds.Contains(id) && !current.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            Raise(new BatchEvent(BatchEventKind.IdSnapshotWarning, _clock.Now,
                $"检测到已完成任务 ID 消失或变更（{string.Join("、", missing.Take(5))}），若任务被重排可能导致重跑，请确认",
                null)
            { Detail = string.Join(",", missing) });
        }

        _idSnapshot = current;
    }

    private void RebuildMatrix() =>
        _matrix = new DegradationMatrix(_options.Accounts, _options.ModelCandidates, _options.DegradationStrategy);

    private async Task SaveCooldownsAsync()
    {
        try
        {
            await _cooldownStore.SaveAsync(_cooldowns.Snapshot(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Write("WARN", $"冷却记录落盘失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 任务文件回写（P0-4 单写者）。原实现：并发的 <c>CompleteTask/FailTask/MarkInterrupted</c>
    /// 各自直接 <c>SaveAsync(_tasks)</c> —— 既在不同时刻快照（后落盘的旧快照会回退已完成状态），
    /// 又并发写同一结果文件。改为：
    /// ① <see cref="_persistGate"/> 串行化写入，同一时刻只有一个写者，先排队者先落盘（单调向前）；
    /// ② 序列化在 <see cref="_writeGate"/> 内完成，与任务状态写入互斥（读改写同锁）。
    /// </summary>
    private async Task PersistAsync(CancellationToken ct)
    {
        await _persistGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using (await EnterWriteGateAsync().ConfigureAwait(false))
            {
                await _taskStore.SaveAsync(_options.TaskFilePath, _tasks, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Write("ERROR", $"任务文件回写失败：{ex.Message}");
        }
        finally
        {
            _persistGate.Release();
        }
    }

    /// <summary>
    /// 进入任务状态写入区（P0-4）。不可取消：等待期间的取消会导致终态丢失（中断/完成不落盘），
    /// 故固定用 <see cref="CancellationToken.None"/>，由整个批处理的取消入口统一收敛。
    /// </summary>
    private async Task<IDisposable> EnterWriteGateAsync()
    {
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        return new GateReleaser(_writeGate);
    }

    private sealed class GateReleaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

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

    private void RaiseTaskChanged(TaskItem task)
    {
        Raise(new BatchEvent(BatchEventKind.Info, _clock.Now, string.Empty, task.Id) { Task = task });
    }

    private void Raise(BatchEvent ev)
    {
        EventRaised?.Invoke(this, ev);
        _logger.Write(ev);
    }

    private static string? Truncate(string? value, int maxChars)
    {
        if (value is null || maxChars <= 0)
        {
            return value;
        }

        return value.Length <= maxChars
            ? value
            : value[..maxChars] + $"\n…（结果已截断，超过 {maxChars / 1024}KB，完整内容见运行日志）";
    }

    /// <summary>兜底用量汇总（P1-1 拆分后由 <see cref="IFallbackBilling"/> 记账，此处供报告协作者读取）。</summary>
    private FallbackSummary BuildFallbackSummary() => _fallback.BuildSummary();

    /// <summary>
    /// 释放三座 <see cref="SemaphoreSlim"/> 门与运行级 <see cref="CancellationTokenSource"/>（CA1001）。
    /// 仅在所有任务已终态、批处理已停止后调用；运行中调用会破坏并发语义。
    /// </summary>
    public void Dispose()
    {
        _writeGate.Dispose();
        _persistGate.Dispose();
        _waitGate.Dispose();
        _runCts?.Dispose();
        _runCts = null;
        GC.SuppressFinalize(this);
    }
}
