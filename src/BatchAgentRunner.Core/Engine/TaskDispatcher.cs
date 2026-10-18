using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 批处理派发协作者（P1-1 拆分产物 ①，§3.4 <c>ITaskDispatcher</c>）：从 <see cref="BatchEngine"/>
/// 抽出「派发主循环 / 受控并发 / DAG 依赖判定」全部逻辑（原 <c>DispatchAsync</c> 的串行与受控并发双路径
/// + 每任务依赖校验）。引擎仅保留批处理生命周期编排（构建队列、DAG 拓扑序、终态判定），
/// 经 <see cref="TaskDispatcherContext"/> 注入批处理级协调能力（落盘单写者、状态广播、单任务执行）。
/// 本协作者即 §3.4 路线图 P1-1 的最后一个拆分产物——抽离后 <see cref="BatchEngine"/> 不再承载「派发」职责。
/// </summary>
public interface ITaskDispatcher
{
    /// <summary>按串行 / 受控并发派发 <paramref name="queue"/>；依赖未满足时按策略阻断或终止整批。</summary>
    Task DispatchAsync(IReadOnlyList<TaskItem> queue, CancellationToken ct);
}

/// <summary>派发所需的引擎能力集合（沿用 <see cref="TaskRunnerContext"/> 的上下文注入范式）。</summary>
public sealed class TaskDispatcherContext
{
    public RunOptions Options { get; init; } = null!;
    public IReadOnlyList<TaskItem> Tasks { get; init; } = null!;
    public IClock Clock { get; init; } = null!;
    public IRunLogger Logger { get; init; } = null!;
    public Func<TaskItem, CancellationToken, Task<TaskOutcome>> RunTask { get; init; } = null!;
    public Func<Task<IDisposable>> EnterWriteGate { get; init; } = null!;
    public Func<CancellationToken, Task> Persist { get; init; } = null!;
    public Func<bool> StopAfterCurrentTask { get; init; } = null!;
    public Action<BatchEvent> Raise { get; init; } = null!;
    public Action<BatchStatus> SetStatus { get; init; } = null!;
    public Action<string, string?> RaiseBlocked { get; init; } = null!;
    public Action<TaskItem> RaiseTaskChanged { get; init; } = null!;

    /// <summary>并行调度协调器（REQ-020 / REQ-021）：维护按工作空间在飞计数、全局派发暂停、统一切换目标与冻结槽位。</summary>
    public DispatchCoordinator Coordinator { get; init; } = null!;

    /// <summary>
    /// 统一切换执行回调（REQ-021）：当在飞数降为 0 且派发已暂停时，调度器调用一次该回调执行
    /// 第 3 部分 §6 凭证切换至目标账号。由引擎注入，避免调度器反向依赖凭证 Vault。
    /// </summary>
    public Func<string, CancellationToken, Task> PerformUnifiedSwitch { get; init; } = null!;
}

/// <summary>默认实现：派发双路径与依赖判定全部来自构造期注入的上下文，无隐藏静态依赖。</summary>
public sealed class TaskDispatcher : ITaskDispatcher
{
    private readonly TaskDispatcherContext _ctx;

    public TaskDispatcher(TaskDispatcherContext context) => _ctx = context;

    public Task DispatchAsync(IReadOnlyList<TaskItem> queue, CancellationToken ct) => DispatchCoreAsync(queue, ct);

    // ---------------- 调度主循环 ----------------

    private async Task DispatchCoreAsync(IReadOnlyList<TaskItem> queue, CancellationToken ct)
    {
        // 二期：含任务依赖的批次强制串行派发——并发下依赖终态不可预测，串行才能保证 DAG 拓扑序语义
        var hasDependencies = queue.Any(t => t.DependsOn is { Count: > 0 });
        if (hasDependencies && _ctx.Options.Concurrency > 1)
        {
            _ctx.Raise(new BatchEvent(BatchEventKind.Info, _ctx.Clock.Now,
                "批次含任务依赖（DAG），已强制串行派发以保证依赖顺序"));
        }

        if (_ctx.Options.Concurrency <= 1 || hasDependencies)
        {
            foreach (var task in queue)
            {
                if (ct.IsCancellationRequested || _ctx.StopAfterCurrentTask())
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

                var outcome = await _ctx.RunTask(task, ct).ConfigureAwait(false);
                if (outcome is TaskOutcome.Blocked or TaskOutcome.Terminated or TaskOutcome.StopBatch)
                {
                    break;
                }
            }
        }
        else
        {
            // 受控并发（REQ-012）＋ 按工作空间串行（REQ-020）＋ 并行下统一切换（REQ-021）：
            // 协调器驱动的主循环——派发资格经 Coordinator.CanDispatch 守卫（按空间串行 + 派发暂停），
            // 任务终态经 Coordinator.ReleaseInFlight 释放在飞槽位，必要时触发一次统一切换。
            var dispatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var running = new List<Task>();
            var stopDispatch = false;

            while (true)
            {
                // 尚有 pending/interrupted 待派发任务的不同工作空间数（用于并行度上界）
                var distinctWs = queue
                    .Where(t => !dispatched.Contains(t.Id) && t.Status is TaskStatus.Pending or TaskStatus.Interrupted)
                    .Select(t => NormalizeWorkspaceKey(t.Workspace))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                foreach (var task in queue.Where(t => !dispatched.Contains(t.Id)
                             && t.Status is TaskStatus.Pending or TaskStatus.Interrupted))
                {
                    var wsKey = NormalizeWorkspaceKey(task.Workspace);
                    if (!_ctx.Coordinator.CanDispatch(wsKey, distinctWs, _ctx.Options.Concurrency))
                    {
                        continue;
                    }

                    dispatched.Add(task.Id);
                    _ctx.Coordinator.RegisterInFlight(task.Id, wsKey);
                    running.Add(RunOneAsync(task, () => stopDispatch = true, ct));
                }

                if (running.Count == 0)
                {
                    break; // 无可派发且无在飞 → 结束
                }

                await Task.WhenAny(running).ConfigureAwait(false);

                // 回收已完成的 worker（含其内部的统一切换），保留仍在运行的
                var stillRunning = new List<Task>();
                foreach (var w in running)
                {
                    if (w.IsCompleted)
                    {
                        try
                        {
                            await w.ConfigureAwait(false);
                        }
                        catch
                        {
                            // RunOneAsync 已捕获单任务异常；此处仅兜底，不影响调度
                        }
                    }
                    else
                    {
                        stillRunning.Add(w);
                    }
                }

                running = stillRunning;

                if (stopDispatch || ct.IsCancellationRequested || _ctx.StopAfterCurrentTask())
                {
                    break;
                }
            }

            // 排空仍在运行的任务（硬停止/取消时不再派发新任务，等待已派发任务收尾）
            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    // ---------------- 单任务包装（在飞登记 + 统一切换） ----------------

    /// <summary>
    /// 包装单任务执行：运行 → 释放在飞槽位；若释放后满足「派发已暂停且全部在飞结束」，
    /// 执行一次统一账号切换（REQ-021），随后调用方循环将继续派发后续任务。
    /// 单任务异常由 <see cref="ITaskRunner"/> 兜底捕获，此处不再二次捕获（仅兜底 rethrow）。
    /// </summary>
    private async Task RunOneAsync(TaskItem task, Action onStop, CancellationToken ct)
    {
        try
        {
            var outcome = await _ctx.RunTask(task, ct).ConfigureAwait(false);
            if (outcome is TaskOutcome.Blocked or TaskOutcome.Terminated or TaskOutcome.StopBatch)
            {
                onStop();
            }
        }
        finally
        {
            var wsKey = NormalizeWorkspaceKey(task.Workspace);
            var unifiedTarget = _ctx.Coordinator.ReleaseInFlight(task.Id);
            if (unifiedTarget is not null && _ctx.PerformUnifiedSwitch is not null)
            {
                try
                {
                    await _ctx.PerformUnifiedSwitch(unifiedTarget, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                        $"并行下统一账号切换失败：{ex.Message}", null));
                }
            }
        }
    }

    /// <summary>归一化工作空间键：null/空视为单一默认工作空间桶，使未指定工作空间的任务同样串行（REQ-020）。</summary>
    private static string NormalizeWorkspaceKey(string? workspace) =>
        string.IsNullOrWhiteSpace(workspace) ? "(default)" : workspace;

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

        if (_ctx.Options.DependencyFailurePolicy == DependencyFailurePolicy.SkipDependents)
        {
            return DependencyVerdict.Satisfied; // 忽略依赖失败，下游照常执行
        }

        var statusById = new Dictionary<string, TaskStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _ctx.Tasks)
        {
            statusById[t.Id] = t.Status;
        }

        var blockOnNeedsAttention = _ctx.Options.DependencyFailurePolicy == DependencyFailurePolicy.BlockDependents;
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

        if (_ctx.Options.DependencyFailurePolicy == DependencyFailurePolicy.AbortBatch)
        {
            using (await _ctx.EnterWriteGate().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
            {
                task.Status = TaskStatus.Blocked;
                task.Error = reason;
                task.Finish(TaskStatus.Blocked, _ctx.Clock.Now);
            }

            _ctx.RaiseTaskChanged(task);
            await _ctx.Persist(CancellationToken.None).ConfigureAwait(false);
            _ctx.SetStatus(BatchStatus.Blocked);
            _ctx.RaiseBlocked($"任务 {task.Id} {reason}，按依赖失败策略终止整批", task.Id);
            return false;
        }

        using (await _ctx.EnterWriteGate().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
        {
            task.Status = TaskStatus.Blocked;
            task.Error = reason;
            task.Finish(TaskStatus.Blocked, _ctx.Clock.Now);
        }

        _ctx.RaiseTaskChanged(task);
        _ctx.Logger.WriteTaskFinished(task.Id, TaskStatus.Blocked, task.DurationSeconds, task.RetryCount);
        _ctx.Raise(new BatchEvent(BatchEventKind.TaskFinished, _ctx.Clock.Now,
            $"任务 {task.Id} {reason}", task.Id)
        { Task = task });
        await _ctx.Persist(CancellationToken.None).ConfigureAwait(false);
        return true;
    }
}
