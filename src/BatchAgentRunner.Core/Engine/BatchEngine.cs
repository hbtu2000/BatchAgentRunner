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
public sealed class BatchEngine : IDisposable
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
    /// <summary>兜底成本/用量锁（P0-5）：跨任务共享，且「检查上限 → 累加」是 check-then-act。</summary>
    private readonly object _fallbackLock = new();
    /// <summary>持久化单写者门（P0-4）：同一时刻只允许一个写者落盘任务文件。</summary>
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly SemaphoreSlim _waitGate = new(1, 1);
    private readonly List<TimelineEntry> _timeline = new();
    private readonly Dictionary<string, UsageEntry> _accountUsage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageEntry> _modelUsage = new(StringComparer.OrdinalIgnoreCase);

    private DegradationMatrix _matrix;
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource? _wake;
    private bool _waitingCancelled;
    private bool _terminateRequested;
    private bool _stopAfterCurrentTask;
    private string? _currentTaskId;
    private decimal _fallbackBatchCost;
    private int _fallbackUsedCount;
    private CostBasis _fallbackCostBasis = CostBasis.Estimated;
    private bool _fallbackStoppedByLimit;
    private readonly List<string> _fallbackModelsUsed = new();
    private double _windowWaitSeconds;
    private double _cooldownWaitSeconds;
    /// <summary>单任务级暂停等待累计（DESIGN-额度耗尽暂停续跑：从任务时长中剔除）。key 不区分大小写。</summary>
    private readonly Dictionary<string, double> _taskWaitSeconds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _idSnapshot;

    // ---------------- 二期扩展（全部可选，向后兼容） ----------------
    private readonly List<NotificationRule> _notifications;
    private readonly IDiffCollector? _diffCollector;
    private readonly IGitIntegration? _git;
    private readonly List<DiffRecord> _diffs = new();
    private readonly SessionResumePolicy _sessionPolicy;
    private readonly INotificationDispatcher? _notificationDispatcher;
    private readonly IMarkdownInPlaceWriter? _markdownWriter;
    private readonly GateEvaluator _gateEvaluator;

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
        GateEvaluator? gateEvaluator = null)
    {
        _options = options;
        _adapter = adapter;
        _taskStore = taskStore;
        _cooldownStore = cooldownStore;
        _logger = logger;
        _vault = credentialVault;
        _notifications = notifications?.ToList() ?? options.Notifications?.ToList() ?? new List<NotificationRule>();
        _diffCollector = diffCollector;
        _git = git;
        _notificationDispatcher = notificationDispatcher;
        _markdownWriter = markdownWriter;
        _gateEvaluator = gateEvaluator ?? new GateEvaluator();
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
    }

    // ---------------- 公开状态 ----------------

    public RunOptions Options => _options;

    public IReadOnlyList<TaskItem> Tasks => _tasks;

    public DegradationMatrix Matrix => _matrix;

    public QuotaCooldownRegistry Cooldowns => _cooldowns;

    public BatchStatus Status { get; private set; } = BatchStatus.Idle;

    public int SkippedDoneCount { get; private set; }

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

    /// <summary>启动前预检（REQ-010）。</summary>
    public async Task<PreflightReport> PreflightAsync(CancellationToken cancellationToken = default)
    {
        var report = new PreflightReport();

        if (string.IsNullOrWhiteSpace(_options.TaskFilePath))
        {
            report.AddError("未选择任务文件");
        }
        else if (!File.Exists(_options.TaskFilePath))
        {
            report.AddError($"任务文件不存在：{_options.TaskFilePath}");
        }
        else
        {
            try
            {
                var loaded = await _taskStore.LoadAsync(_options.TaskFilePath, cancellationToken).ConfigureAwait(false);
                using (await EnterWriteGateAsync().ConfigureAwait(false))
                {
                    _tasks.Clear();
                    _tasks.AddRange(loaded.Tasks);
                }

                report.LoadedTaskCount = loaded.Tasks.Count;
                report.PendingTaskCount = loaded.Tasks.Count(t => t.Status is TaskStatus.Pending or TaskStatus.Interrupted);
                foreach (var issue in loaded.Issues)
                {
                    report.AddWarning(issue.Message);
                }

                if (_tasks.Count == 0)
                {
                    report.AddError("任务文件中没有解析到任何任务");
                }

                // 工作空间校验（REQ-003a）
                foreach (var ws in _options.Workspaces.Where(w => w.IsValid))
                {
                    if (!Directory.Exists(ws.Path))
                    {
                        report.AddError($"工作空间「{ws.Name}」目录不存在或无访问权限：{ws.Path}");
                    }
                }

                foreach (var t in _tasks.Where(t => !string.IsNullOrWhiteSpace(t.Workspace)))
                {
                    var resolved = ResolveWorkspacePath(t.Workspace!);
                    if (resolved is null)
                    {
                        report.AddError($"任务 {t.Id} 引用的工作空间「{t.Workspace}」不存在（命名空间悬空）");
                    }
                }

                // 二期 DAG 预检：环检测（错误，阻止启动）+ 悬空依赖（告警，执行时保守阻断）
                if (_tasks.Any(t => t.DependsOn is { Count: > 0 }))
                {
                    try
                    {
                        DagScheduler.TopoSort(_tasks);
                    }
                    catch (DagCycleException ex)
                    {
                        report.AddError($"任务依赖存在环：{ex.Message}");
                    }

                    var ids = new HashSet<string>(_tasks.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
                    foreach (var t in _tasks.Where(t => t.DependsOn is { Count: > 0 }))
                    {
                        foreach (var dep in t.DependsOn!)
                        {
                            if (!ids.Contains(dep))
                            {
                                report.AddWarning($"任务 {t.Id} 的依赖 {dep} 不存在（悬空依赖，执行时将保守阻断）");
                            }
                        }
                    }
                }
            }
            catch (TaskFileFormatException ex)
            {
                report.AddError($"任务文件解析失败：{ex.Message}");
            }
        }

        // CLI 可用性
        var env = await _adapter.ValidateEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        if (!env.Ok)
        {
            report.AddError($"Agent CLI 不可用：{env.Message}");
        }

        // 账号池预检：失效账号自动停用（REQ-010）
        var enabledAccounts = _options.Accounts.Where(a => a.Enabled).ToList();
        if (_vault is not null && _options.Concurrency > 1)
        {
            // DESIGN §3：同一时刻每个 CLI 只有一个身份，多账号并发切换会互相覆盖 Live
            report.AddWarning("已启用凭证 Vault：同一时刻每个 CLI 只有一个登录身份，并发 > 1 时多账号切换会互相覆盖，建议 concurrency = 1");
        }
        if (enabledAccounts.Count == 0)
        {
            report.AddError("账号池为空：请至少添加一个启用账号");
        }

        foreach (var account in enabledAccounts)
        {
            // 凭证 Vault 集成（DESIGN §7）：预检前先切换到该账号；预检即验证快照有效性。
            if (_vault is not null)
            {
                if (!_vault.SnapshotExists(_options.AgentKind, account.Id))
                {
                    account.Enabled = false;
                    report.DisabledAccounts.Add(account.Label);
                    report.AddWarning($"账号 {account.Label} 尚无凭证快照，已停用：请在配置页对该账号执行「引导登录」");
                    continue;
                }

                try
                {
                    await _vault.SwitchToAsync(_options.AgentKind, account.Id, "preflight", cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CredentialVaultException ex)
                {
                    account.Enabled = false;
                    report.DisabledAccounts.Add(account.Label);
                    report.AddWarning($"账号 {account.Label} 凭证切换失败已停用：{ex.Message}");
                    continue;
                }
            }

            var r = await _adapter.ValidateAccountAsync(account, cancellationToken).ConfigureAwait(false);
            if (!r.Ok)
            {
                account.Enabled = false;
                report.DisabledAccounts.Add(account.Label);
                report.AddWarning($"账号 {account.Label} 预检失败已停用：{r.Message}");
                continue;
            }

            if (_vault is not null)
            {
                try
                {
                    await _vault.MarkVerifiedAsync(_options.AgentKind, account.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CredentialVaultException ex)
                {
                    report.AddWarning($"账号 {account.Label} 快照验证时间更新失败：{ex.Message}");
                }
            }
        }

        if (_options.Fallback.Enabled && _options.Accounts.All(a => !a.Enabled || !a.HasFallbackModels))
        {
            report.AddWarning("已启用兜底，但没有任何启用账号配置兜底模型池（accounts[].fallback_models），兜底不会生效");
        }

        // 专家（REQ-004a）
        if (_options.Expert.IsSpecified && _adapter.Kind == AgentKind.Mock)
        {
            report.AddWarning("当前使用仿真适配器，专家参数不会被真实消费");
        }

        RebuildMatrix();

        // 降级矩阵为空 = 所有启用账号既没有自有候选池、全局共享列表也为空（REQ-003，故事 2.1）
        if (_matrix.IsEmpty)
        {
            report.AddError("降级矩阵为空：请为至少一个启用账号配置候选模型池（accounts[].model_candidates），或填写全局共享候选模型列表");
        }

        // 时段 / 冷却可用性评估（REQ-003b / REQ-004c）
        var now = _clock.Now;
        if (!_matrix.IsEmpty)
        {
            var selection = _matrix.SelectFirst(now, _evaluator);
            if (!selection.Found)
            {
                report.AllCombosUnavailable = true;
                var earliest = _matrix.EarliestRestore(now, _evaluator);
                report.EarliestRestoreAt = earliest?.RestoreAt;
                report.WaitKind = earliest?.WaitKind ?? WaitKind.None;
                report.UnavailableReason = earliest is null
                    ? "无可用组合"
                    : string.Join("；", selection.Skipped.Take(3).Select(s => s.DescribeSkip()));
            }
        }

        return report;
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
                Report = BuildReport(startedAt);
                return Report;
            }
        }

        SkippedDoneCount = _options.ForceRerun
            ? 0
            : _tasks.Count(t => t.Status is TaskStatus.Done or TaskStatus.Failed);

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
            Report = BuildReport(startedAt);
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
        Report = BuildReport(startedAt);

        // 二期：批处理终态通知推送（REQ-018；按规则触发，失败不阻断）
        await DispatchNotificationsAsync(
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

                var outcome = await ExecuteTaskSafeAsync(task, ct).ConfigureAwait(false);
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

                    var outcome = await ExecuteTaskSafeAsync(task, ct).ConfigureAwait(false);
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

    /// <summary>
    /// 单任务执行兜底：引擎自身的未预期异常（如启动进程失败、落盘失败）不允许静默冒泡
    /// 杀死整批（2026-10-16 实测：异常被 UI 吞掉、运行日志无记录、报告不落盘，排障极困难）。
    /// 此处记录 ERROR 日志与事件、任务判失败后继续下一任务；用户取消（OCE）按终止处理。
    /// 二期扩展（可选）：执行前后采集结果 diff、项目级任务建分支/开 PR、Markdown 原地回写。
    /// </summary>
    private async Task<TaskOutcome> ExecuteTaskSafeAsync(TaskItem task, CancellationToken ct)
    {
        string? snapshotId = null;
        if (_diffCollector is not null && _options.CollectDiffs && !ct.IsCancellationRequested)
        {
            try
            {
                snapshotId = await _diffCollector.SnapshotAsync(task, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Write("WARN", $"任务 {task.Id} diff 快照失败（已忽略）：{ex.Message}");
            }
        }

        // 二期：项目级任务——执行前建分支（尽力而为，失败不阻断任务）
        var effectiveProject = EffectiveProjectRef(task);
        if (_git is not null && effectiveProject is not null)
        {
            try
            {
                await _git.CreateBranchAsync(effectiveProject, task.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Write("WARN", $"任务 {task.Id} 创建 Git 分支失败（已忽略）：{ex.Message}");
            }
        }

        TaskOutcome outcome;
        try
        {
            outcome = await ExecuteTaskAsync(task, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await MarkInterrupted(task, "用户终止，当前任务安全中断").ConfigureAwait(false);
            outcome = TaskOutcome.Terminated;
        }
        catch (Exception ex)
        {
            _logger.Write("ERROR", $"任务 {task.Id} 执行发生未预期异常：{ex.GetType().Name}：{ex.Message}");
            Raise(new BatchEvent(BatchEventKind.Warning, _clock.Now,
                $"任务 {task.Id} 执行异常（{ex.GetType().Name}）：{ex.Message}", task.Id));
            try
            {
                await FailTask(task, $"执行异常（{ex.GetType().Name}）：{ex.Message}").ConfigureAwait(false);
            }
            catch (Exception persistEx)
            {
                _logger.Write("ERROR", $"任务 {task.Id} 失败落盘也异常：{persistEx.GetType().Name}：{persistEx.Message}");
            }

            outcome = TaskOutcome.Finished;
        }

        // 二期：项目级任务成功后开 PR（尽力而为；需任务/全局显式开启 auto_open_pr）
        if (_git is not null && effectiveProject is not null && task.Status == TaskStatus.Done
            && effectiveProject.AutoOpenPr
            && !string.IsNullOrWhiteSpace(effectiveProject.TaskBranch))
        {
            try
            {
                await _git.OpenPullRequestAsync(effectiveProject, task.Id, effectiveProject.TaskBranch, null, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Write("WARN", $"任务 {task.Id} 打开 PR 失败（已忽略）：{ex.Message}");
            }
        }

        // 二期：结果 diff 采集
        if (snapshotId is not null && _diffCollector is not null)
        {
            try
            {
                var recs = await _diffCollector.CollectAsync(snapshotId, task, ct).ConfigureAwait(false);
                if (recs.Count > 0)
                {
                    lock (_diffs)
                    {
                        _diffs.AddRange(recs);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Write("WARN", $"任务 {task.Id} diff 采集失败（已忽略）：{ex.Message}");
            }
        }

        // 二期：Markdown 任务文件原地回写（仅在显式开启且任务文件为 .md 时）
        if (_markdownWriter is not null && _options.MarkdownInPlaceWriteback && IsMarkdownTaskFile())
        {
            try
            {
                await _markdownWriter.WriteBackAsync(_options.TaskFilePath, task, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Write("WARN", $"任务 {task.Id} Markdown 原回写失败（已忽略）：{ex.Message}");
            }
        }

        return outcome;
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
            $"工作流任务 {task.Id} 结束：{outcome.FinalStatus}", task.Id) { Task = task });

        await PersistAsync(CancellationToken.None).ConfigureAwait(false);

        if (outcome.FinalStatus == TaskStatus.NeedsAttention)
        {
            await DispatchNotificationsAsync(NotifyTrigger.NeedsAttention, null, outcome.Error).ConfigureAwait(false);
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
            TrackUsage(account, model, usage);
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
            $"任务 {task.Id} {reason}", task.Id) { Task = task });
        await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    // ---------------- 二期：通知推送 ----------------

    /// <summary>
    /// 按通知规则推送（REQ-018）。未注入分发器、无规则或 trigger 为 null 时为空操作；
    /// 推送失败只记日志，绝不阻断批处理。
    /// </summary>
    private async Task DispatchNotificationsAsync(NotifyTrigger? trigger, DateTimeOffset? batchStartedAt = null,
        string? message = null)
    {
        if (trigger is null || _notificationDispatcher is null || _notifications.Count == 0)
        {
            return;
        }

        var now = _clock.Now;
        var context = new NotificationContext
        {
            Trigger = trigger.Value,
            BatchName = string.IsNullOrWhiteSpace(_options.TaskFilePath)
                ? "批处理"
                : Path.GetFileNameWithoutExtension(_options.TaskFilePath),
            BatchStatus = Status,
            Total = _tasks.Count,
            Done = _tasks.Count(t => t.Status == TaskStatus.Done),
            Failed = _tasks.Count(t => t.Status == TaskStatus.Failed),
            NeedsAttention = _tasks.Count(t => t.Status == TaskStatus.NeedsAttention),
            Blocked = _tasks.Count(t => t.Status == TaskStatus.Blocked),
            DurationSeconds = batchStartedAt.HasValue
                ? Math.Round((now - batchStartedAt.Value).TotalSeconds, 3)
                : 0,
            Message = message
        };

        try
        {
            await _notificationDispatcher.DispatchAsync(_notifications, context, default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Write("WARN", $"通知推送失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>任务文件是否为 Markdown（决定二期原地回写是否适用）。</summary>
    private bool IsMarkdownTaskFile() =>
        !string.IsNullOrWhiteSpace(_options.TaskFilePath) &&
        _options.TaskFilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    private async Task<TaskOutcome> ExecuteTaskAsync(TaskItem task, CancellationToken ct)
    {
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var startedAt = _clock.Now;
        _currentTaskId = task.Id;

        task.Status = TaskStatus.Running;
        task.StartedAt = startedAt;
        task.Error = null;
        task.RetryCount = 0;
        task.FinishedAt = null;
        task.DurationSeconds = 0;
        // interrupted 任务续跑重跑时清除旧的「部分输出」（旧值保留在运行日志，REQ-008）
        if (task.HasPartialResult)
        {
            _logger.Write("INFO", $"任务 {task.Id} 清除上次部分输出：{Truncate(task.Result, 200)}");
            task.Result = null;
        }

        RaiseTaskChanged(task);
        _logger.WriteTaskStarted(task.Id, null, null, _options.Expert.ToString(), task.Workspace);
        Raise(new BatchEvent(BatchEventKind.TaskStarted, _clock.Now, $"开始执行任务 {task.Id}", task.Id) { Task = task });

        // 二期：工作流任务走 WorkflowExecutor（多阶段 + 门禁 + 修订循环），复用一期降级/额度/冷却内核
        if (task.IsWorkflow)
        {
            return await ExecuteWorkflowAsync(task, taskCts.Token).ConfigureAwait(false);
        }

        AgentCombo? combo = null;
        var retrySameCombo = false;
        var attempts = 0;
        var rateLimitStreak = 0;
        var normalRetries = 0;

        while (true)
        {
            if (taskCts.IsCancellationRequested)
            {
                await MarkInterrupted(task, "用户终止，当前任务安全中断").ConfigureAwait(false);
                return TaskOutcome.Terminated;
            }

            if (attempts >= _options.MaxAttemptsPerTask)
            {
                await FailTask(task, $"单任务总尝试次数达到上限（{_options.MaxAttemptsPerTask} 次）").ConfigureAwait(false);
                return TaskOutcome.Finished;
            }

            if (_clock.Now - startedAt > _options.MaxTaskDuration)
            {
                // 暂停等待时长不计入「单任务总时长」上限（DESIGN-额度耗尽暂停续跑：暂停不计入时长）
                _taskWaitSeconds.TryGetValue(task.Id, out var waitedSoFar);
                if ((_clock.Now - startedAt).TotalSeconds - waitedSoFar > _options.MaxTaskDuration.TotalSeconds)
                {
                    await FailTask(task,
                        $"单任务净执行时长达到上限（{_options.MaxTotalMinutesPerTask} 分钟，不含暂停等待）").ConfigureAwait(false);
                    return TaskOutcome.Finished;
                }
            }

            var now = _clock.Now;
            AgentCombo? next;

            if (retrySameCombo && combo is not null && _evaluator.Evaluate(combo, now).Available)
            {
                // 临时限流：退避后重试同一组合，不切换（REQ-005）
                next = combo;
            }
            else
            {
                var selection = _matrix.SelectNext(combo, now, _evaluator);

                foreach (var skipped in selection.Skipped)
                {
                    RaiseSkipEvent(skipped);
                }

                if (selection.Found)
                {
                    next = selection.Combo!;
                }
                else
                {
                    var invested = attempts > 0 || !string.IsNullOrEmpty(task.Result);

                    // 兜底：仅对「已投入执行的当前任务」生效（REQ-004b）
                    if (invested && _options.Fallback.Enabled)
                    {
                        var fb = await TryFallbackAsync(task, taskCts.Token).ConfigureAwait(false);
                        if (fb.Success)
                        {
                            return TaskOutcome.Finished;
                        }
                    }

                    var earliest = _matrix.EarliestRestore(now, _evaluator);
                    // 额度耗尽（QuotaReset）放开等待上限：额度每日必然重置，确定可恢复 → 无限等待直到重置（DESIGN-额度耗尽暂停续跑）。
                    // 纯免费时段外 / 时段+额度混合仍受 MaxWait 约束，避免跨天/跨周末无限挂起。
                    var canWait = earliest is not null &&
                                  (IsQuotaUnboundedWait(earliest, now) || earliest.RestoreAt <= now.Add(_options.MaxWait));

                    if (!canWait)
                    {
                        await MarkInterrupted(task, earliest is null
                            ? "免费组合矩阵当前不可用（额度耗尽 / 时段外 / 冷却中）"
                            : $"免费组合矩阵当前不可用，最早恢复时间 {earliest.RestoreAt:HH:mm} 超出最长可等待时长").ConfigureAwait(false);
                        SetStatus(BatchStatus.Blocked);
                        RaiseBlocked(earliest is null
                            ? "所有候选组合额度均已用尽 / 均不在免费时段，批处理已暂停"
                            : $"所有候选组合当前不可用，最早恢复时间 {earliest.RestoreAt:HH:mm}（超出最长可等待时长 {_options.MaxWaitMinutes} 分钟）");
                        _logger.WriteBatchBlocked("全部组合不可用", _tasks.Count(t => !t.IsTerminal));
                        return TaskOutcome.Blocked;
                    }

                    var waited = await WaitForRestoreAsync(earliest!, task.Id, taskCts.Token).ConfigureAwait(false);
                    if (!waited)
                    {
                        await MarkInterrupted(task, "用户取消等待，当前任务安全中断").ConfigureAwait(false);
                        SetStatus(BatchStatus.Blocked);
                        Raise(new BatchEvent(BatchEventKind.BatchWaitingCancelled, _clock.Now,
                            "已取消等待，批处理已暂停", task.Id) { BatchStatus = BatchStatus.Blocked });
                        return TaskOutcome.Blocked;
                    }

                    continue;
                }
            }

            retrySameCombo = false;

            if (combo is not null && !ReferenceEquals(next, combo))
            {
                RaiseSwitchEvents(combo, next, task.Id);
            }

            combo = next;
            attempts++;

            if (!await EnsureLiveCredentialAsync(task, combo, taskCts.Token).ConfigureAwait(false))
            {
                continue;
            }

            var request = BuildRequest(task, combo, isFallback: false);
            Raise(new BatchEvent(BatchEventKind.Info, _clock.Now,
                $"任务 {task.Id} 使用 {combo.Account.Label} + {combo.Model.Name} 执行",
                task.Id, combo.Account.Label, combo.Model.Name));

            AgentExecutionResult result;
            try
            {
                result = await ExecuteWithTimeoutAsync(request, taskCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await MarkInterrupted(task, "用户终止，当前任务安全中断").ConfigureAwait(false);
                return TaskOutcome.Terminated;
            }

            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                AppendPartialOutput(task, result.Output!);
            }

            if (result.Succeeded)
            {
                await CompleteTask(task, result).ConfigureAwait(false);
                return TaskOutcome.Finished;
            }

            var failure = result.Failure ?? new AgentFailure(AgentFailureKind.TaskError, "未知错误");

            switch (failure.Kind)
            {
                case AgentFailureKind.RateLimited:
                    rateLimitStreak++;
                    if (rateLimitStreak <= _options.RateLimitBackoffRetries)
                    {
                        task.RetryCount++;
                        var backoff = _retryPolicy.BackoffFor(rateLimitStreak);
                        Raise(new BatchEvent(BatchEventKind.Warning, _clock.Now,
                            $"任务 {task.Id} 触发临时限流，{backoff.TotalSeconds:0} 秒后重试同一组合（第 {rateLimitStreak} 次）",
                            task.Id, combo.Account.Label, combo.Model.Name));
                        await _clock.DelayAsync(backoff, taskCts.Token).ConfigureAwait(false);
                        retrySameCombo = true;
                        continue;
                    }

                    // 退避重试仍失败 → 升级为额度耗尽处理（REQ-005）
                    goto case AgentFailureKind.QuotaExhausted;

                case AgentFailureKind.QuotaExhausted:
                {
                    var record = _cooldowns.Record(combo.Account.Id, combo.Model.Name, failure.ResetAt, _clock.Now,
                        failure.Raw);
                    Raise(new BatchEvent(BatchEventKind.QuotaResetRecorded, _clock.Now,
                        $"组合 {combo} 额度已耗尽，记录重置时间 {record.ResetAt:yyyy-MM-dd HH:mm}（{record.DescribeSource()}）",
                        task.Id, combo.Account.Label, combo.Model.Name)
                    { Detail = record.RawValue });
                    _logger.WriteQuotaResetRecorded(combo.Account.Id, combo.Model.Name, record.ResetAt, record.Source);
                    _cooldowns.PurgeExpired(_clock.Now);
                    await SaveCooldownsAsync().ConfigureAwait(false);
                    rateLimitStreak = 0;
                    continue;
                }

                case AgentFailureKind.AuthExpired:
                    combo.Account.Enabled = false;
                    Raise(new BatchEvent(BatchEventKind.Warning, _clock.Now,
                        $"账号 {combo.Account.Label} 凭据失效，已停用并切换下一账号", task.Id, combo.Account.Label));
                    continue;

                case AgentFailureKind.DangerousOperation:
                    Raise(new BatchEvent(BatchEventKind.DangerousOperationBlocked, _clock.Now,
                        $"检测到危险操作确认，已拒绝自动批准并中断任务 {task.Id}：{failure.Message}",
                        task.Id, combo.Account.Label, combo.Model.Name)
                    { IsDangerous = true });
                    _logger.WriteDangerousOperation(task.Id, failure.Message);
                    await FailTask(task, failure.Message).ConfigureAwait(false);
                    return TaskOutcome.Finished;

                case AgentFailureKind.Timeout:
                    await FailTask(task, $"单任务执行超时（{_options.TaskTimeoutMinutes} 分钟），已保留部分输出").ConfigureAwait(false);
                    return TaskOutcome.Finished;

                default:
                    normalRetries++;
                    if (normalRetries <= _options.MaxRetriesPerTask)
                    {
                        task.RetryCount++;
                        Raise(new BatchEvent(BatchEventKind.Warning, _clock.Now,
                            $"任务 {task.Id} 执行失败，重试第 {normalRetries} 次：{failure.Message}", task.Id));
                        continue;
                    }

                    await FailTask(task, failure.Message).ConfigureAwait(false);
                    if (_options.StopOnFailure)
                    {
                        SetStatus(BatchStatus.Blocked);
                        RaiseBlocked($"任务 {task.Id} 失败，已按「失败即停」暂停批处理", task.Id);
                        return TaskOutcome.StopBatch;
                    }

                    return TaskOutcome.Finished;
            }
        }
    }

    private async Task<AgentExecutionResult> ExecuteWithTimeoutAsync(AgentRequest request, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.TaskTimeout);

        try
        {
            return await _adapter.ExecuteAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AgentExecutionResult.Fail(request,
                new AgentFailure(AgentFailureKind.Timeout, $"执行超过 {_options.TaskTimeoutMinutes} 分钟"));
        }
    }

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
            if (candidate.WaitKind == WaitKind.FreeWindow || candidate.WaitKind == WaitKind.Mixed)
            {
                _windowWaitSeconds += elapsed;
            }
            else
            {
                _cooldownWaitSeconds += elapsed;
            }

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
                $"已恢复执行（暂停 {elapsed / 60:0.0} 分钟，不计入任务时长）") { BatchStatus = BatchStatus.Running });
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

    private async Task<(bool Success, decimal Cost)> TryFallbackAsync(TaskItem task,
        CancellationToken ct)
    {
        if (!_options.Fallback.Enabled)
        {
            return (false, 0m);
        }

        // 兜底模型池挂在账号下（REQ-004b，v1.12）：
        // 按「账号顺序 × 该账号兜底池顺序」选取第一个可用兜底组合；账号仅需启用。
        // 兜底模型不参与免费时段过滤（R-3）。
        var (account, fallback) = _matrix.Accounts
            .Where(a => a.Enabled)
            .SelectMany(a => (a.FallbackModels ?? new List<FallbackModel>()).Select(fb => (Account: a, Fallback: fb)))
            .FirstOrDefault();

        if (fallback is null)
        {
            // 没有任何启用账号配置兜底模型池 → 视为未具备兜底能力（REQ-004b）
            return (false, 0m);
        }

        var unitPrice = fallback.EstimatedUnitPrice ?? 0m;

        // P0-5：兜底成本/用量是跨任务共享状态（Concurrency>1 时并发累加），且「检查上限 → 累加」
        // 属 check-then-act，必须与累加共用同一把锁；事件广播在锁外进行（避免持锁调用订阅方）。
        string? limitReason = null;
        lock (_fallbackLock)
        {
            if (_fallbackBatchCost + unitPrice > _options.Fallback.MaxCostPerBatch)
            {
                limitReason = $"兜底成本已用尽（单批上限 ¥{_options.Fallback.MaxCostPerBatch}），已停用兜底并转为阻塞";
            }
            else if (unitPrice > _options.Fallback.MaxCostPerTask)
            {
                limitReason = $"兜底单价 ¥{unitPrice} 超出单任务上限 ¥{_options.Fallback.MaxCostPerTask}，已跳过兜底";
            }

            if (limitReason is not null)
            {
                _fallbackStoppedByLimit = true;
            }
        }

        if (limitReason is not null)
        {
            Raise(new BatchEvent(BatchEventKind.Warning, _clock.Now, limitReason, task.Id));
            return (false, 0m);
        }

        // 兜底模型不参与免费时段过滤（R-3）：FreeTimeWindow 恒为 null
        var fallbackModel = new ModelCandidate
        {
            Name = fallback.Name,
            FreeTimeWindow = null,
            EstimatedUnitPrice = fallback.EstimatedUnitPrice
        };
        var combo = new AgentCombo(account, fallbackModel);

        Raise(new BatchEvent(BatchEventKind.FallbackUsed, _clock.Now,
            $"免费组合当前不可用，已使用兜底模型 {fallbackModel.Name} 收尾当前任务 {task.Id}",
            task.Id, account.Label, fallbackModel.Name));

        var request = BuildRequest(task, combo, isFallback: true);
        lock (_fallbackLock)
        {
            if (!_fallbackModelsUsed.Contains(fallbackModel.Name, StringComparer.Ordinal))
            {
                _fallbackModelsUsed.Add(fallbackModel.Name);
            }
        }

        var result = await ExecuteWithTimeoutAsync(request, ct).ConfigureAwait(false);

        var cost = result.Usage?.Cost ?? unitPrice;
        var basis = result.Usage?.Basis ?? CostBasis.Estimated;

        // P0-1：原写法 Math.Min(cost, Math.Max(MaxCostPerTask, cost)) 恒等于 cost（no-op），
        // 单任务成本上限完全失效 → 实测花费再高也全额计入。改为按 MaxCostPerTask 真实截断。
        // 不变量：TryFallbackAsync 记入 _fallbackBatchCost / task.FallbackCost 的 cost
        // 必须 ≤ Fallback.MaxCostPerTask（由 BatchEngineTests.兜底成本超单任务上限时按上限截断 锁住）。
        cost = Math.Min(cost, Math.Max(0m, _options.Fallback.MaxCostPerTask));

        if (result.Succeeded)
        {
            lock (_fallbackLock)
            {
                _fallbackBatchCost += cost;
                _fallbackUsedCount++;
                _fallbackCostBasis = basis;
            }

            using (await EnterWriteGateAsync().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
            {
                task.Status = TaskStatus.Done;
                task.Result = Truncate(result.Output, _options.ResultMaxKB * 1024);
                task.ModelUsed = fallbackModel.Name;
                task.AccountUsed = account.Label;
                task.ExpertUsed = _options.Expert.ToString();
                task.FallbackUsed = true;
                task.FallbackCost = cost;
                task.Error = null;
                task.Finish(TaskStatus.Done, _clock.Now);
            }

            Raise(new BatchEvent(BatchEventKind.FallbackUsed, _clock.Now,
                $"兜底完成：任务 {task.Id}（花费 ¥{cost:0.####}，{(basis == CostBasis.Measured ? "实测" : "估算")}）",
                task.Id, account.Label, fallbackModel.Name) { Cost = cost, Task = task });
            _logger.WriteFallbackUsed(task.Id, fallbackModel.Name, cost, basis);
            RaiseTaskChanged(task);
            await PersistAsync(ct).ConfigureAwait(false);
            return (true, cost);
        }

        // 兜底本身也失败：按普通失败处理，不循环兜底（边缘情况 10）
        lock (_fallbackLock)
        {
            _fallbackBatchCost += cost;
            _fallbackCostBasis = basis;
        }
        await FailTask(task, $"兜底模型 {fallbackModel.Name} 执行失败：{result.Failure?.Message ?? "未知错误"}").ConfigureAwait(false);
        return (false, cost);
    }

    // ---------------- 任务状态写入 ----------------

    private async Task CompleteTask(TaskItem task, AgentExecutionResult result)
    {
        var combo = result.Request;

        // P0-4：终态写入与落盘序列化互斥，保证落盘的是一致状态（不会写完 Status
        // 还没写 Result 就被另一个写者序列化走）。事件广播保持在锁外。
        using (await EnterWriteGateAsync().ConfigureAwait(false))
        {
            task.Status = TaskStatus.Done;
            task.Result = Truncate(result.Output, _options.ResultMaxKB * 1024);
            task.ModelUsed = combo.Model.Name;
            task.AccountUsed = combo.Account.Label;
            task.ExpertUsed = _options.Expert.ToString();
            task.FallbackUsed = false;
            task.Error = null;
            task.Finish(TaskStatus.Done, _clock.Now);
            ApplyPausedExclusion(task);

            TrackUsageCore(combo.Account.Label, combo.Model.Name, result.Usage);
        }

        Raise(new BatchEvent(BatchEventKind.TaskFinished, _clock.Now,
            $"任务 {task.Id} 完成（{task.DurationSeconds:0.#}s，{combo.Account.Label} + {combo.Model.Name}）",
            task.Id, combo.Account.Label, combo.Model.Name) { Task = task });
        _logger.WriteTaskFinished(task.Id, TaskStatus.Done, task.DurationSeconds, task.RetryCount);
        RaiseTaskChanged(task);
        await PersistAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FailTask(TaskItem task, string error)
    {
        using (await EnterWriteGateAsync().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
        {
            task.Status = TaskStatus.Failed;
            task.Error = error;
            task.Finish(TaskStatus.Failed, _clock.Now);
            ApplyPausedExclusion(task);
        }

        Raise(new BatchEvent(BatchEventKind.TaskFinished, _clock.Now,
            $"任务 {task.Id} 失败（重试 {task.RetryCount} 次）：{error}", task.Id) { Task = task });
        _logger.WriteTaskFinished(task.Id, TaskStatus.Failed, task.DurationSeconds, task.RetryCount);
        RaiseTaskChanged(task);
        await PersistAsync(CancellationToken.None).ConfigureAwait(false);

        // 二期：任务失败通知（REQ-018；按规则触发，失败不阻断）
        await DispatchNotificationsAsync(NotifyTrigger.TaskFailed, null, error).ConfigureAwait(false);
    }

    private async Task MarkInterrupted(TaskItem task, string reason)
    {
        using (await EnterWriteGateAsync().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
        {
            if (!string.IsNullOrWhiteSpace(task.Result) && !task.HasPartialResult)
            {
                task.Result = TaskItem.PartialResultPrefix + task.Result;
            }

            task.Status = TaskStatus.Interrupted;
            task.Error = reason;
            task.Finish(TaskStatus.Interrupted, _clock.Now);
            ApplyPausedExclusion(task);
        }

        Raise(new BatchEvent(BatchEventKind.TaskFinished, _clock.Now,
            $"任务 {task.Id} 已中断：{reason}", task.Id) { Task = task });
        _logger.WriteTaskFinished(task.Id, TaskStatus.Interrupted, task.DurationSeconds, task.RetryCount);
        RaiseTaskChanged(task);
        await PersistAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void AppendPartialOutput(TaskItem task, string output)
    {
        var merged = string.IsNullOrWhiteSpace(task.Result) ? output : task.Result + "\n" + output;
        task.Result = Truncate(merged, _options.ResultMaxKB * 1024);
    }

    /// <summary>
    /// 从任务时长中剔除其自身经历的暂停等待（DESIGN-额度耗尽暂停续跑：暂停时间不计入时长）。
    /// 仅在终止阶段调用，等待只发生在 <see cref="ExecuteTaskAsync"/> 循环内，故本任务必有对应累计。
    /// </summary>
    private void ApplyPausedExclusion(TaskItem task)
    {
        double waited;
        lock (_statsLock)
        {
            if (!_taskWaitSeconds.TryGetValue(task.Id, out waited) || waited <= 0)
            {
                return;
            }

            _taskWaitSeconds.Remove(task.Id);
        }

        task.DurationSeconds = Math.Max(0, Math.Round(task.DurationSeconds - waited, 3));
    }

    // ---------------- 辅助 ----------------

    private AgentRequest BuildRequest(TaskItem task, AgentCombo combo, bool isFallback)
    {
        var workspace = ResolveWorkspacePath(task.Workspace ?? _options.DefaultWorkspace)
                        ?? Directory.GetCurrentDirectory();

        return new AgentRequest(
            task.Id,
            task.Instruction,
            combo.Account,
            combo.Model,
            _options.Expert,
            workspace,
            _options.TaskTimeout,
            _options.AutoAnswer)
        {
            IsFallback = isFallback,
            OnOutputChunk = chunk =>
                Raise(new BatchEvent(BatchEventKind.TaskOutput, _clock.Now, chunk, task.Id))
        };
    }

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

    private void RaiseSwitchEvents(AgentCombo from, AgentCombo to, string? taskId)
    {
        if (!string.Equals(from.Account.Id, to.Account.Id, StringComparison.OrdinalIgnoreCase))
        {
            Raise(new BatchEvent(BatchEventKind.AccountSwitched, _clock.Now,
                $"账号额度耗尽，已切换：{from.Account.Label} → {to.Account.Label}",
                taskId, to.Account.Label, to.Model.Name));
            _logger.WriteModelSwitched(from.Account.Label, from.Model.Name, to.Account.Label, to.Model.Name,
                "账号切换", taskId);
        }
        else if (!string.Equals(from.Model.Name, to.Model.Name, StringComparison.OrdinalIgnoreCase))
        {
            Raise(new BatchEvent(BatchEventKind.ModelSwitched, _clock.Now,
                $"模型 {from.Model.Name} 免费额度已用尽，已自动切换至 {to.Model.Name}，当前任务继续执行",
                taskId, to.Account.Label, to.Model.Name));
            _logger.WriteModelSwitched(from.Account.Label, from.Model.Name, to.Account.Label, to.Model.Name,
                "额度耗尽", taskId);
        }

        AddTimeline("切换", $"{from} → {to}", taskId, to.Account.Label, to.Model.Name);
    }

    private void RaiseSkipEvent(ComboAvailability skipped)
    {
        switch (skipped.Reason)
        {
            case ComboSkipReason.OutsideFreeWindow:
                Raise(new BatchEvent(BatchEventKind.ModelWindowSkipped, _clock.Now, skipped.DescribeSkip(),
                    null, skipped.Combo.Account.Label, skipped.Combo.Model.Name));
                _logger.WriteWindowSkipped(skipped.Combo.Model.Name, skipped.NextWindowStart);
                AddTimeline("时段外跳过", skipped.DescribeSkip(), null, null, skipped.Combo.Model.Name);
                break;
            case ComboSkipReason.Cooldown:
                Raise(new BatchEvent(BatchEventKind.ModelCooldownSkipped, _clock.Now, skipped.DescribeSkip(),
                    null, skipped.Combo.Account.Label, skipped.Combo.Model.Name));
                if (skipped.ResetAt.HasValue)
                {
                    _logger.WriteCooldownSkipped(skipped.Combo.Account.Id, skipped.Combo.Model.Name,
                        skipped.ResetAt.Value);
                }

                AddTimeline("冷却跳过", skipped.DescribeSkip(), null, skipped.Combo.Account.Label,
                    skipped.Combo.Model.Name);
                break;
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
                null) { Detail = string.Join(",", missing) });
        }

        _idSnapshot = current;
    }

    private void TrackUsage(string account, string model, AgentUsage? usage)
    {
        lock (_statsLock)
        {
            TrackUsageCore(account, model, usage);
        }
    }

    private void TrackUsageCore(string account, string model, AgentUsage? usage)
    {
        if (!_accountUsage.TryGetValue(account, out var au))
        {
            au = new UsageEntry { Name = account };
            _accountUsage[account] = au;
        }

        if (!_modelUsage.TryGetValue(model, out var mu))
        {
            mu = new UsageEntry { Name = model };
            _modelUsage[model] = mu;
        }

        au.TaskCount++;
        mu.TaskCount++;
        if (usage?.Cost is { } cost)
        {
            au.Cost = (au.Cost ?? 0m) + cost;
            mu.Cost = (mu.Cost ?? 0m) + cost;
            au.CostBasis = usage.Basis;
            mu.CostBasis = usage.Basis;
        }
    }

    private void AddTimeline(string kind, string message, string? taskId, string? account, string? model)
    {
        var entry = new TimelineEntry
        {
            At = _clock.Now, Kind = kind, Message = message, TaskId = taskId, Account = account, Model = model
        };
        lock (_statsLock)
        {
            _timeline.Add(entry);
        }
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
        AddTimeline("阻塞", reason, taskId, null, null);
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
            null) { BatchStatus = status });
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

    /// <summary>兜底用量快照（P0-5：读也走同一把锁，避免并发下读到撕裂状态）。</summary>
    private FallbackSummary BuildFallbackSummary()
    {
        lock (_fallbackLock)
        {
            return new FallbackSummary
            {
                UsedCount = _fallbackUsedCount,
                Model = _fallbackModelsUsed.Count > 0 ? string.Join("、", _fallbackModelsUsed) : null,
                TotalCost = _fallbackBatchCost,
                CostBasis = _fallbackCostBasis,
                StoppedByLimit = _fallbackStoppedByLimit
            };
        }
    }

    private BatchReport BuildReport(DateTimeOffset startedAt)
    {
        var finishedAt = _clock.Now;
        return new BatchReport
        {
            GeneratedAt = finishedAt,
            BatchStatus = Status,
            TaskFilePath = _options.TaskFilePath,
            Total = _tasks.Count,
            Done = _tasks.Count(t => t.Status == TaskStatus.Done),
            Failed = _tasks.Count(t => t.Status == TaskStatus.Failed),
            Interrupted = _tasks.Count(t => t.Status == TaskStatus.Interrupted),
            Pending = _tasks.Count(t => t.Status == TaskStatus.Pending),
            NeedsAttention = _tasks.Count(t => t.Status == TaskStatus.NeedsAttention),
            Blocked = _tasks.Count(t => t.Status == TaskStatus.Blocked),
            SkippedAlreadyDone = SkippedDoneCount,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            DurationSeconds = Math.Round((finishedAt - startedAt).TotalSeconds, 1),
            ActiveDurationSeconds = Math.Round(
                Math.Max(0, (finishedAt - startedAt).TotalSeconds - _windowWaitSeconds - _cooldownWaitSeconds), 1),
            AccountUsage = _accountUsage.Values.ToList(),
            ModelUsage = _modelUsage.Values.ToList(),
            Timeline = _timeline.ToList(),
            Fallback = BuildFallbackSummary(),
            FailedTasks = _tasks.Where(t => t.Status == TaskStatus.Failed)
                .Select(t => new FailedTaskEntry
                {
                    TaskId = t.Id, Error = t.Error, RetryCount = t.RetryCount,
                    Model = t.ModelUsed, Account = t.AccountUsed
                }).ToList(),
            WindowWaitSeconds = Math.Round(_windowWaitSeconds, 1),
            CooldownWaitSeconds = Math.Round(_cooldownWaitSeconds, 1),
            Diffs = _diffs.ToList(),
            Tasks = _tasks.Select(t => new TaskReportEntry
            {
                TaskId = t.Id,
                Status = t.Status,
                DependsOn = t.DependsOn is null ? null : new List<string>(t.DependsOn),
                BlockedBy = ComputeBlockedBy(t),
                Error = t.Error,
                Leftover = ComputeLeftover(t),
                StageResults = t.StageResults is null ? null : new List<StageResult>(t.StageResults),
                TemplateId = t.TemplateVars?.TemplateId,
                DurationSeconds = t.DurationSeconds,
                ModelUsed = t.ModelUsed,
                AccountUsed = t.AccountUsed
            }).ToList()
        };
    }

    /// <summary>
    /// 生效的项目级 Git 配置（二期）：任务级 <see cref="TaskItem.ProjectRef"/> 优先，
    /// 空字段用全局 <see cref="RunOptions.Git"/> 兜底；任务无 ProjectRef 但全局配置了仓库时按全局合成。
    /// 全局仓库路径为空 = 未配置 Git 集成，返回 null（不执行任何 Git 操作）。
    /// </summary>
    private ProjectRef? EffectiveProjectRef(TaskItem task)
    {
        var git = _options.Git;
        var globalReady = !string.IsNullOrWhiteSpace(git.RepoPath);

        if (task.ProjectRef is null)
        {
            if (!globalReady)
            {
                return null;
            }

            var branch = RenderBranchName(git.BranchNaming, task.Id);
            return new ProjectRef
            {
                RepoPath = git.RepoPath,
                BaseBranch = git.BaseBranch,
                TaskBranch = branch,
                PrTargetBranch = git.PrTargetBranch ?? git.BaseBranch,
                AutoCreateBranch = true,
                AutoOpenPr = git.AutoOpenPr
            };
        }

        if (!globalReady && string.IsNullOrWhiteSpace(task.ProjectRef.RepoPath))
        {
            return null; // 两级都没配仓库
        }

        var effective = task.ProjectRef.Clone();
        if (string.IsNullOrWhiteSpace(effective.RepoPath))
        {
            effective.RepoPath = git.RepoPath;
        }

        if (string.IsNullOrWhiteSpace(effective.BaseBranch))
        {
            effective.BaseBranch = git.BaseBranch;
        }

        if (string.IsNullOrWhiteSpace(effective.PrTargetBranch))
        {
            effective.PrTargetBranch = git.PrTargetBranch ?? effective.BaseBranch;
        }

        if (string.IsNullOrWhiteSpace(effective.TaskBranch) && effective.AutoCreateBranch)
        {
            effective.TaskBranch = RenderBranchName(
                string.IsNullOrWhiteSpace(git.BranchNaming) ? "bar/{task_id}-{timestamp}" : git.BranchNaming,
                task.Id);
        }

        return effective;
    }

    /// <summary>渲染分支命名模板：{task_id} / {timestamp}(yyyyMMddHHmmss) / {date}(yyyyMMdd)。</summary>
    private static string RenderBranchName(string template, string taskId)
    {
        var now = DateTimeOffset.Now;
        return template
            .Replace("{task_id}", taskId, StringComparison.OrdinalIgnoreCase)
            .Replace("{timestamp}", now.ToString("yyyyMMddHHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>阻断来源：依赖失败（Failed/Blocked）或悬空（不存在）的依赖 Id。</summary>
    private List<string>? ComputeBlockedBy(TaskItem task)    {
        if (task.Status != TaskStatus.Blocked || task.DependsOn is not { Count: > 0 })
        {
            return null;
        }

        var byId = _tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        foreach (var dep in task.DependsOn)
        {
            if (!byId.TryGetValue(dep, out var depTask))
            {
                sources.Add($"{dep}（不存在）"); // 悬空依赖
            }
            else if (depTask.Status is TaskStatus.Failed or TaskStatus.Blocked)
            {
                sources.Add(dep);
            }
        }

        return sources.Count > 0 ? sources : null;
    }

    /// <summary>待人工原因：任务错误，或首个未完成阶段的门禁遗留/产出摘要。</summary>
    private static string? ComputeLeftover(TaskItem task)
    {
        if (!string.IsNullOrWhiteSpace(task.Error))
        {
            return task.Error;
        }

        if (task.StageResults is null)
        {
            return null;
        }

        var pending = task.StageResults.FirstOrDefault(s => s.Status != TaskStatus.Done);
        if (pending is null)
        {
            return null;
        }

        return pending.GateLeftover ?? pending.OutputSummary ?? $"阶段 {pending.Name ?? pending.StageId} 未完成";
    }

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
