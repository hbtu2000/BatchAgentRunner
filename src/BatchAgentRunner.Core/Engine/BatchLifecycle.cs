using System.Diagnostics.CodeAnalysis;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Core.Text;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 批处理生命周期编排者（P-02 / ARCH §3.3 + A-7）：从 <see cref="BatchEngine"/> 抽出「批次级生命周期编排」
/// （<see cref="RunAsync"/> = 原 <c>StartAsync</c>）与「工作流任务执行」（<see cref="ExecuteWorkflowAsync"/>），
/// 使 facade 退化为「按序编排协作者 + 维护批次级状态」。独占与协作者的协作时序：
/// 构建队列 → DAG 拓扑序 → 启动前预检闸门 → 不可用等待 → 派发 → 终态判定 → 落盘 → 报告 → 通知。
/// </summary>
/// <remarks>
/// 公开行为（事件序列、状态转移、报告/通知触发时机）与抽离前逐字节等价：仅把编排体搬迁至此，无业务语义改动。
/// 共享可变状态（取消标记 / 暂停标记 / 当前任务 id / 运行级 CTS）收口到 <see cref="BatchRunState"/>，
/// 由引擎与生命周期共享，避免 facade 与编排者各自持有副本导致不一致。
/// </remarks>
public sealed class BatchLifecycle
{
    private readonly BatchLifecycleContext _ctx;

    /// <summary>构造生命周期编排者（上下文由 <see cref="BatchEngine"/> 构造期装配）。</summary>
    /// <param name="context">协作者装配上下文（时钟 / 队列 / 状态 / 落盘 / 指标）。</param>
    public BatchLifecycle(BatchLifecycleContext context) => _ctx = context;

    /// <summary>
    /// 启动或续跑（REQ-008）。<paramref name="waitIfUnavailable"/> 为 true 时，启动前全部组合不可用会进入 waiting
    /// （REQ-010 用户选择「等待自动启动」），否则直接 blocked 退出。对应原 <see cref="BatchEngine.StartAsync"/>。
    /// </summary>
    /// <param name="waitIfUnavailable">启动时全部组合不可用，是否进入等待（true）还是直接 blocked 退出（false）。</param>
    /// <param name="cancellationToken">批次取消令牌（与内部运行 CTS 链接）。</param>
    /// <returns>批次终态报告。</returns>
    public async Task<BatchReport> RunAsync(bool waitIfUnavailable = false, CancellationToken cancellationToken = default)
    {
        _ctx.RunState.RunCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _ctx.RunState.RunCts.Token;
        var startedAt = _ctx.Clock.Now;
        _ctx.Metrics.MarkBatchStarted(startedAt);
        _ctx.RunState.TerminateRequested = false;
        _ctx.RunState.StopAfterCurrentTask = false;

        await _ctx.Persistence.LoadTasksAsync(token).ConfigureAwait(false);
        await _ctx.Persistence.BackupAsync(token).ConfigureAwait(false);

        // ID 快照校验（REQ-001a）
        _ctx.Persistence.CheckIdSnapshot();

        var queue = _ctx.Tasks
            .Where(t => _ctx.Options.ForceRerun
                ? true
                : t.Status is TaskStatus.Pending or TaskStatus.Interrupted)
            .ToList();

        // DAG 派发：存在任务依赖时按拓扑序派发，环在加载期快速失败
        if (queue.Any(t => t.DependsOn is { Count: > 0 }))
        {
            try
            {
                queue = DagScheduler.TopoSort(queue).ToList();
            }
            catch (DagCycleException ex)
            {
                _ctx.SetStatus(BatchStatus.Blocked);
                _ctx.RaiseBlocked($"任务依赖存在环：{ex.Message}", null);
                await _ctx.Persistence.PersistAsync(token).ConfigureAwait(false);
                return BuildAndReturn(startedAt);
            }
        }

        _ctx.Report.SetSkippedDoneCount(_ctx.Options.ForceRerun
            ? 0
            : _ctx.Tasks.Count(t => t.Status is TaskStatus.Done or TaskStatus.Failed));
        var skippedDoneCount = _ctx.Report.SkippedDoneCount;

        if (skippedDoneCount > 0)
        {
            _ctx.Raise(new BatchEvent(BatchEventKind.Info, _ctx.Clock.Now, $"已跳过 {skippedDoneCount} 条已完成任务"));
        }

        _ctx.SetStatus(BatchStatus.Running);

        // 启动前预检闸门（REQ-010）：CLI 缺失属环境错误，快速失败、不进入执行循环。
        var env = await _ctx.Adapter.ValidateEnvironmentAsync(token).ConfigureAwait(false);
        if (!env.Ok)
        {
            _ctx.SetStatus(BatchStatus.Blocked);
            _ctx.RaiseBlocked($"启动前预检未通过：{env.Message}", null);
            await _ctx.Persistence.PersistAsync(token).ConfigureAwait(false);
            return BuildAndReturn(startedAt);
        }

        // 启动前不可用处理
        if (!_ctx.MatrixProvider().IsEmpty && _ctx.MatrixProvider().AllUnavailable(_ctx.Clock.Now, _ctx.Evaluator))
        {
            var earliest = _ctx.MatrixProvider().EarliestRestore(_ctx.Clock.Now, _ctx.Evaluator);
            // 额度耗尽场景即便未勾选「等待自动启动」也自动挂起等待（必然恢复，DESIGN-额度耗尽暂停续跑）
            if (earliest is not null && (waitIfUnavailable || IsQuotaUnboundedWait(earliest, _ctx.Clock.Now)))
            {
                var ok = await _ctx.WaitForRestore(earliest, null, token).ConfigureAwait(false);
                if (!ok)
                {
                    _ctx.SetStatus(BatchStatus.Blocked);
                    _ctx.RaiseBlocked("启动前等待被取消", null);
                }
            }
            else
            {
                _ctx.SetStatus(BatchStatus.Blocked);
                _ctx.RaiseBlocked(earliest is null
                    ? "所有候选组合当前不可用"
                    : $"所有候选组合当前不可用，最早恢复时间 {earliest.RestoreAt:HH:mm}", null);
            }
        }

        if (_ctx.GetStatus() == BatchStatus.Running)
        {
            // REQ-021：并发下所有在飞任务必须共用同一活动账号。派发开始前先把 Live 凭证固定到降级矩阵首选账号。
            await _ctx.AccountSwitch.EnsurePreferredLiveAccountAsync(token).ConfigureAwait(false);
            await _ctx.Dispatcher.DispatchAsync(queue, token).ConfigureAwait(false);
        }

        // 终态判定
        if (_ctx.RunState.TerminateRequested)
        {
            _ctx.SetStatus(BatchStatus.Terminated);
        }
        else if (_ctx.GetStatus() is BatchStatus.Running or BatchStatus.Waiting)
        {
            var allTerminal = _ctx.Tasks.All(t => t.IsTerminal);
            if (allTerminal)
            {
                _ctx.SetStatus(BatchStatus.Completed);
                _ctx.Raise(new BatchEvent(BatchEventKind.BatchCompleted, _ctx.Clock.Now,
                    $"批处理完成：成功 {_ctx.Tasks.Count(t => t.Status == TaskStatus.Done)}，失败 {_ctx.Tasks.Count(t => t.Status == TaskStatus.Failed)}，中断 {_ctx.Tasks.Count(t => t.Status == TaskStatus.Interrupted)}，待人工 {_ctx.Tasks.Count(t => t.Status == TaskStatus.NeedsAttention)}，阻塞 {_ctx.Tasks.Count(t => t.Status == TaskStatus.Blocked)}")
                {
                    BatchStatus = BatchStatus.Completed
                });
            }
            else
            {
                _ctx.SetStatus(BatchStatus.Blocked);
            }
        }

        await _ctx.Persistence.PersistAsync(token).ConfigureAwait(false);
        var built = BuildAndReturn(startedAt);

        // 批处理终态通知推送（REQ-018；按规则触发，失败不阻断）
        await _ctx.Notifier.DispatchAsync(
            _ctx.GetStatus() switch
            {
                BatchStatus.Completed => NotifyTrigger.BatchCompleted,
                BatchStatus.Blocked => NotifyTrigger.BatchBlocked,
                BatchStatus.Terminated => NotifyTrigger.BatchTerminated,
                _ => (NotifyTrigger?)null
            },
            startedAt).ConfigureAwait(false);

        return built;
    }

    private BatchReport BuildAndReturn(DateTimeOffset startedAt)
    {
        var built = _ctx.Report.Build(startedAt);
        _ctx.SetReport(built);

        // P-10 / A-8：批次到达终态固定输出一次指标快照（与日志同 RunId 关联），
        // 使「这批跑得怎么样」不必再从日志文本里人肉统计。
        _ctx.Logger.Write(_ctx.Metrics.ToLogEntry(_ctx.Clock.Now));
        return built;
    }

    /// <summary>
    /// 工作流任务执行（多阶段顺序驱动 + 质量门禁 + 修订循环）。委托自包含的 <see cref="WorkflowExecutor"/>，
    /// 生命周期只负责落盘与终态映射。对应原 <c>BatchEngine.ExecuteWorkflowAsync</c>（P-02 随编排体一同下沉）。
    /// </summary>
    public async Task<TaskOutcome> ExecuteWorkflowAsync(TaskItem task, CancellationToken ct)
    {
        var outcome = await new WorkflowExecutor(BuildWorkflowContext()).RunAsync(task, ct).ConfigureAwait(false);

        using (await _ctx.Persistence.EnterWriteGateAsync().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
        {
            task.Status = outcome.FinalStatus;
            task.Result = TextTruncate.Result(outcome.Result, _ctx.Options.ResultMaxKB * 1024);
            task.Error = outcome.Error;
            task.StageResults = outcome.StageResults;
            task.ModelUsed = outcome.ModelUsed;
            task.AccountUsed = outcome.AccountUsed;
            task.ExpertUsed = outcome.ExpertUsed;
            task.FallbackUsed = outcome.FallbackUsed;
            task.FallbackCost = outcome.FallbackCost;
            task.Finish(outcome.FinalStatus, _ctx.Clock.Now);
        }

        _ctx.RaiseTaskChanged(task);
        _ctx.Logger.WriteTaskFinished(task.Id, outcome.FinalStatus, task.DurationSeconds, task.RetryCount);
        // P-10 / A-8：工作流任务同样计入终态指标（否则多阶段任务会整批漏计吞吐与失败率）
        _ctx.Metrics.RecordTaskFinished(outcome.FinalStatus, task.DurationSeconds);
        _ctx.Raise(new BatchEvent(BatchEventKind.TaskFinished, _ctx.Clock.Now,
            $"工作流任务 {task.Id} 结束：{outcome.FinalStatus}", task.Id)
        { Task = task });

        await _ctx.Persistence.PersistAsync(CancellationToken.None).ConfigureAwait(false);

        if (outcome.FinalStatus == TaskStatus.NeedsAttention)
        {
            await _ctx.Notifier.DispatchAsync(NotifyTrigger.NeedsAttention, null, outcome.Error).ConfigureAwait(false);
        }

        return outcome.FinalStatus switch
        {
            TaskStatus.Interrupted => TaskOutcome.Terminated,
            TaskStatus.Failed => _ctx.Options.StopOnFailure ? TaskOutcome.StopBatch : TaskOutcome.Finished,
            _ => TaskOutcome.Finished // Done / NeedsAttention / Blocked 均不影响后续任务派发
        };
    }

    /// <summary>构造工作流执行上下文（把降级/额度/冷却/兜底内核能力注入工作流执行器）。对应原 <c>BatchEngine.BuildWorkflowContext</c>（P-02 下沉）。</summary>
    private WorkflowEngineContext BuildWorkflowContext() => new()
    {
        Options = _ctx.Options,
        Adapter = _ctx.Adapter,
        Matrix = _ctx.MatrixProvider(),
        Evaluator = _ctx.Evaluator,
        Cooldowns = _ctx.Cooldowns,
        Classifier = _ctx.Classifier,
        Clock = _ctx.Clock,
        Logger = _ctx.Logger,
        GateEvaluator = _ctx.GateEvaluator,
        OnEvent = _ctx.Raise,
        OnTaskChanged = _ctx.RaiseTaskChanged,
        Persist = () => _ctx.Persistence.PersistAsync(CancellationToken.None),
        TrackUsage = (account, model, usage) =>
        {
            _ctx.Report.TrackUsage(account, model, usage);
            return Task.CompletedTask;
        },
        EnsureCredential = _ctx.Vault is not null ? _ctx.AccountSwitch.EnsureLiveCredentialAsync : null,
        WaitForRestore = _ctx.WaitForRestore,
        ResolveWorkspacePath = _ctx.ResolveWorkspace,
        DefaultMaxGateLoops = _ctx.Options.DefaultMaxGateLoops
    };

    /// <summary>是否对「额度耗尽」场景放开等待上限（无限等待直到重置）。收敛于 <see cref="QuotaWaitPolicy"/>（P2-1 偿还：原三处重复谓词）。</summary>
    private bool IsQuotaUnboundedWait(RestoreCandidate? candidate, DateTimeOffset now) =>
        QuotaWaitPolicy.IsUnboundedQuotaWait(_ctx.Options, _ctx.MatrixProvider(), _ctx.Evaluator, candidate, now);

    /// <summary>批处理运行级共享可变状态（REQ-021 / P-02）：取消标记 / 暂停标记 / 当前任务 id / 运行级 CTS。引擎与生命周期共享同一实例。</summary>
    [SuppressMessage("Performance", "CA1001:具有可释放字段的类型应该实现 IDisposable",
        Justification = "RunCts 由引擎在 Dispose/终止流程中释放，生命周期不持有非托管资源；逃逸阀台账 #11 / 到期日 2027-01-20 / 责任人 工程")]
    public sealed class BatchRunState
    {
        /// <summary>运行级取消源（由引擎 <c>Dispose</c> 统一释放；逃逸阀台账 #11）。</summary>
        public CancellationTokenSource? RunCts { get; set; }
        /// <summary>是否已请求终止（当前任务结束后停批）。</summary>
        public bool TerminateRequested { get; set; }
        /// <summary>是否「完成当前任务即停」。</summary>
        public bool StopAfterCurrentTask { get; set; }
        /// <summary>当前执行中的任务 id（等待/取消时定位）。</summary>
        public string? CurrentTaskId { get; set; }
    }
}

/// <summary>批处理生命周期编排所需的引擎能力集合（沿用 <see cref="TaskRunnerContext"/> 的上下文注入范式）。</summary>
public sealed class BatchLifecycleContext
{
    /// <summary>运行配置。</summary>
    public RunOptions Options { get; init; } = null!;
    /// <summary>CLI 适配器（预检 / 模型目录）。</summary>
    public IAgentCliAdapter Adapter { get; init; } = null!;
    /// <summary>时钟（测试可替身冻结）。</summary>
    public IClock Clock { get; init; } = null!;
    /// <summary>可用性评估器（时段 / 冷却判定入口）。</summary>
    public AvailabilityEvaluator Evaluator { get; init; } = null!;
    /// <summary>额度冷却注册表。</summary>
    public QuotaCooldownRegistry Cooldowns { get; init; } = null!;
    /// <summary>错误分类器（限流 / 额度 / 凭据 / 普通）。</summary>
    public QuotaErrorClassifier Classifier { get; init; } = null!;
    /// <summary>质量门禁评估器。</summary>
    public GateEvaluator GateEvaluator { get; init; } = null!;
    /// <summary>任务清单（与引擎共享同一实例）。</summary>
    public List<TaskItem> Tasks { get; init; } = null!;
    /// <summary>降级矩阵提供者（配置变更时重建，懒解析防陈旧）。</summary>
    public Func<DegradationMatrix> MatrixProvider { get; init; } = null!;
    /// <summary>报告构建器。</summary>
    public IBatchReportBuilder Report { get; init; } = null!;
    /// <summary>通知派发器。</summary>
    public IBatchNotifier Notifier { get; init; } = null!;
    /// <summary>运行日志。</summary>
    public IRunLogger Logger { get; init; } = null!;
    /// <summary>任务派发协作者。</summary>
    public ITaskDispatcher Dispatcher { get; init; } = null!;
    /// <summary>等待协调者（等待循环 / 唤醒 / 可用性轮询）。</summary>
    public WaitCoordinator WaitCoordinator { get; init; } = null!;
    /// <summary>凭证切换协作者。</summary>
    public AccountSwitchCoordinator AccountSwitch { get; init; } = null!;
    /// <summary>持久化协作者（加载 / 备份 / 落盘 / 写入门）。</summary>
    public BatchPersistence Persistence { get; init; } = null!;
    /// <summary>批次运行级共享可变状态。</summary>
    public BatchLifecycle.BatchRunState RunState { get; init; } = null!;
    /// <summary>凭证 Vault（null = 不启用凭证切换）。</summary>
    public ICredentialVault? Vault { get; init; }
    /// <summary>等待恢复委托（转 WaitCoordinator）。</summary>
    public Func<RestoreCandidate, string?, CancellationToken, Task<bool>> WaitForRestore { get; init; } = null!;
    /// <summary>事件上报委托（统一填充 RunId）。</summary>
    public Action<BatchEvent> Raise { get; init; } = null!;
    /// <summary>批次状态设置委托。</summary>
    public Action<BatchStatus> SetStatus { get; init; } = null!;
    /// <summary>批次状态读取委托。</summary>
    public Func<BatchStatus> GetStatus { get; init; } = null!;
    /// <summary>报告就绪回调（供宿主读取）。</summary>
    public Action<BatchReport?> SetReport { get; init; } = null!;
    /// <summary>任务变更事件上报委托。</summary>
    public Action<TaskItem> RaiseTaskChanged { get; init; } = null!;
    /// <summary>工作区路径解析委托。</summary>
    public Func<string?, string?> ResolveWorkspace { get; init; } = null!;
    /// <summary>阻塞事件上报委托。</summary>
    public Action<string, string?> RaiseBlocked { get; init; } = null!;

    /// <summary>运行级指标采集器（ARCH P-10 / A-8）：批次结束时输出快照。</summary>
    public RunMetrics Metrics { get; init; } = null!;
}
