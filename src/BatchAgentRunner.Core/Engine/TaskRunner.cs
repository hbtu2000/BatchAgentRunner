using System.Diagnostics.CodeAnalysis;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 单任务执行协作者（P1-1 拆分产物 ②，§3.4 <c>ITaskRunner</c>）：从 <see cref="BatchEngine"/>
/// 抽出「单任务状态机（重试 / 退避 / 降级 / 超时）」全部逻辑（原 <c>ExecuteTaskAsync</c> 的
/// while(true) + switch + goto 模型），并承接其直接耦合的终态写入、兜底计费、凭证切换、组合切换等辅助方法。
/// 引擎保留批处理级协调能力（落盘单写者、waiting 门、状态广播），经 <see cref="TaskRunnerContext"/> 注入。
/// 本协作者即 P1-2「状态机外置」的落地。
/// </summary>
public interface ITaskRunner
{
    /// <summary>执行单任务（含兜底异常捕获，对应原 ExecuteTaskSafeAsync）。</summary>
    Task<TaskOutcome> RunSafeAsync(TaskItem task, CancellationToken ct);
}

/// <summary>单任务执行所需的引擎能力集合（沿用 <c>WorkflowEngineContext</c> 的上下文注入范式）。</summary>
public sealed class TaskRunnerContext
{
    public RunOptions Options { get; init; } = null!;
    public IAgentCliAdapter Adapter { get; init; } = null!;
    public Func<DegradationMatrix> MatrixProvider { get; init; } = null!;
    public AvailabilityEvaluator Evaluator { get; init; } = null!;
    public QuotaCooldownRegistry Cooldowns { get; init; } = null!;
    public RetryPolicy RetryPolicy { get; init; } = null!;
    public IClock Clock { get; init; } = null!;
    public IRunLogger Logger { get; init; } = null!;
    public ICredentialVault? Vault { get; init; }
    public IBatchReportBuilder Report { get; init; } = null!;
    public IBatchNotifier Notifier { get; init; } = null!;
    public IFallbackBilling Fallback { get; init; } = null!;
    public IGitWorkflow GitWorkflow { get; init; } = null!;
    public IFileSystem FileSystem { get; init; } = null!;
    public Dictionary<string, double> TaskWaitSeconds { get; init; } = null!;
    public object StatsLock { get; init; } = null!;
    public Func<string?, string?> ResolveWorkspace { get; init; } = null!;
    public Func<Task> SaveCooldowns { get; init; } = null!;
    public Func<CancellationToken, Task> Persist { get; init; } = null!;
    public Func<Task<IDisposable>> EnterWriteGate { get; init; } = null!;
    public Action<BatchEvent> Raise { get; init; } = null!;
    public Action<BatchStatus> SetStatus { get; init; } = null!;
    public Action<string, string?> RaiseBlocked { get; init; } = null!;
    public Action<TaskItem> RaiseTaskChanged { get; init; } = null!;
    public Action<string?> SetCurrentTaskId { get; init; } = null!;
    public Func<RestoreCandidate, string?, CancellationToken, Task<bool>> WaitForRestore { get; init; } = null!;
    public Func<TaskItem, CancellationToken, Task<TaskOutcome>> ExecuteWorkflow { get; init; } = null!;
    public Func<int> GetPendingCount { get; init; } = null!;
}

/// <summary>默认实现：状态机与辅助方法全部来自构造期注入的上下文，无隐藏静态依赖。</summary>
public sealed class TaskRunner : ITaskRunner
{
    private readonly TaskRunnerContext _ctx;

    public TaskRunner(TaskRunnerContext context) => _ctx = context;

    public Task<TaskOutcome> RunSafeAsync(TaskItem task, CancellationToken ct) => ExecuteTaskSafeAsync(task, ct);

    /// <summary>
    /// 单任务执行兜底：引擎自身的未预期异常（如启动进程失败、落盘失败）不允许静默冒泡
    /// 杀死整批（2026-10-16 实测：异常被 UI 吞掉、运行日志无记录、报告不落盘，排障极困难）。
    /// 此处记录 ERROR 日志与事件、任务判失败后继续下一任务；用户取消（OCE）按终止处理。
    /// 二期扩展（P1-1 拆分产物 ④）：执行前后的 diff 采集 / Git 分支与 PR / Markdown 回写
    /// 全部委托 <see cref="IGitWorkflow"/>（尽力而为，失败不阻断任务）。
    /// </summary>
    private async Task<TaskOutcome> ExecuteTaskSafeAsync(TaskItem task, CancellationToken ct)
    {
        var gitContext = await _ctx.GitWorkflow.BeginAsync(task, ct).ConfigureAwait(false);

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
            _ctx.Logger.Write("ERROR", $"任务 {task.Id} 执行发生未预期异常：{ex.GetType().Name}：{ex.Message}");
            _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                $"任务 {task.Id} 执行异常（{ex.GetType().Name}）：{ex.Message}", task.Id));
            try
            {
                await FailTask(task, $"执行异常（{ex.GetType().Name}）：{ex.Message}").ConfigureAwait(false);
            }
            catch (Exception persistEx)
            {
                _ctx.Logger.Write("ERROR", $"任务 {task.Id} 失败落盘也异常：{persistEx.GetType().Name}：{persistEx.Message}");
            }

            outcome = TaskOutcome.Finished;
        }

        await _ctx.GitWorkflow.CompleteAsync(task, gitContext, ct).ConfigureAwait(false);
        return outcome;
    }

    private async Task<TaskOutcome> ExecuteTaskAsync(TaskItem task, CancellationToken ct)
    {
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var startedAt = _ctx.Clock.Now;
        _ctx.SetCurrentTaskId(task.Id);

        task.Status = TaskStatus.Running;
        task.StartedAt = startedAt;
        task.Error = null;
        task.RetryCount = 0;
        task.FinishedAt = null;
        task.DurationSeconds = 0;
        // interrupted 任务续跑重跑时清除旧的「部分输出」（旧值保留在运行日志，REQ-008）
        if (task.HasPartialResult)
        {
            _ctx.Logger.Write("INFO", $"任务 {task.Id} 清除上次部分输出：{Truncate(task.Result, 200)}");
            task.Result = null;
        }

        _ctx.RaiseTaskChanged(task);
        _ctx.Logger.WriteTaskStarted(task.Id, null, null, _ctx.Options.Expert.ToString(), task.Workspace);
        _ctx.Raise(new BatchEvent(BatchEventKind.TaskStarted, _ctx.Clock.Now, $"开始执行任务 {task.Id}", task.Id) { Task = task });

        // 二期：工作流任务走 WorkflowExecutor（多阶段 + 门禁 + 修订循环），复用一期降级/额度/冷却内核
        if (task.IsWorkflow)
        {
            return await _ctx.ExecuteWorkflow(task, taskCts.Token).ConfigureAwait(false);
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

            if (attempts >= _ctx.Options.MaxAttemptsPerTask)
            {
                await FailTask(task, $"单任务总尝试次数达到上限（{_ctx.Options.MaxAttemptsPerTask} 次）").ConfigureAwait(false);
                return TaskOutcome.Finished;
            }

            if (_ctx.Clock.Now - startedAt > _ctx.Options.MaxTaskDuration)
            {
                // 暂停等待时长不计入「单任务总时长」上限（DESIGN-额度耗尽暂停续跑：暂停不计入时长）
                _ctx.TaskWaitSeconds.TryGetValue(task.Id, out var waitedSoFar);
                if ((_ctx.Clock.Now - startedAt).TotalSeconds - waitedSoFar > _ctx.Options.MaxTaskDuration.TotalSeconds)
                {
                    await FailTask(task,
                        $"单任务净执行时长达到上限（{_ctx.Options.MaxTotalMinutesPerTask} 分钟，不含暂停等待）").ConfigureAwait(false);
                    return TaskOutcome.Finished;
                }
            }

            var now = _ctx.Clock.Now;
            AgentCombo? next;

            if (retrySameCombo && combo is not null && _ctx.Evaluator.Evaluate(combo, now).Available)
            {
                // 临时限流：退避后重试同一组合，不切换（REQ-005）
                next = combo;
            }
            else
            {
                var selection = _ctx.MatrixProvider().SelectNext(combo, now, _ctx.Evaluator);

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
                    if (invested && _ctx.Options.Fallback.Enabled)
                    {
                        var fb = await TryFallbackAsync(task, taskCts.Token).ConfigureAwait(false);
                        if (fb.Success)
                        {
                            return TaskOutcome.Finished;
                        }
                    }

                    var earliest = _ctx.MatrixProvider().EarliestRestore(now, _ctx.Evaluator);
                    // 额度耗尽（QuotaReset）放开等待上限：额度每日必然重置，确定可恢复 → 无限等待直到重置（DESIGN-额度耗尽暂停续跑）。
                    // 纯免费时段外 / 时段+额度混合仍受 MaxWait 约束，避免跨天/跨周末无限挂起。
                    var canWait = earliest is not null &&
                                  (IsQuotaUnboundedWait(earliest, now) || earliest.RestoreAt <= now.Add(_ctx.Options.MaxWait));

                    if (!canWait)
                    {
                        await MarkInterrupted(task, earliest is null
                            ? "免费组合矩阵当前不可用（额度耗尽 / 时段外 / 冷却中）"
                            : $"免费组合矩阵当前不可用，最早恢复时间 {earliest.RestoreAt:HH:mm} 超出最长可等待时长").ConfigureAwait(false);
                        _ctx.SetStatus(BatchStatus.Blocked);
                        _ctx.RaiseBlocked(
                            earliest is null
                                ? "所有候选组合额度均已用尽 / 均不在免费时段，批处理已暂停"
                                : $"所有候选组合当前不可用，最早恢复时间 {earliest.RestoreAt:HH:mm}（超出最长可等待时长 {_ctx.Options.MaxWaitMinutes} 分钟）",
                            task.Id);
                        _ctx.Logger.WriteBatchBlocked("全部组合不可用", _ctx.GetPendingCount());
                        return TaskOutcome.Blocked;
                    }

                    var waited = await _ctx.WaitForRestore(earliest!, task.Id, taskCts.Token).ConfigureAwait(false);
                    if (!waited)
                    {
                        await MarkInterrupted(task, "用户取消等待，当前任务安全中断").ConfigureAwait(false);
                        _ctx.SetStatus(BatchStatus.Blocked);
                        _ctx.Raise(new BatchEvent(BatchEventKind.BatchWaitingCancelled, _ctx.Clock.Now,
                            "已取消等待，批处理已暂停", task.Id)
                        { BatchStatus = BatchStatus.Blocked });
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
            _ctx.Raise(new BatchEvent(BatchEventKind.Info, _ctx.Clock.Now,
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
                    if (rateLimitStreak <= _ctx.Options.RateLimitBackoffRetries)
                    {
                        task.RetryCount++;
                        var backoff = _ctx.RetryPolicy.BackoffFor(rateLimitStreak);
                        _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                            $"任务 {task.Id} 触发临时限流，{backoff.TotalSeconds:0} 秒后重试同一组合（第 {rateLimitStreak} 次）",
                            task.Id, combo.Account.Label, combo.Model.Name));
                        await _ctx.Clock.DelayAsync(backoff, taskCts.Token).ConfigureAwait(false);
                        retrySameCombo = true;
                        continue;
                    }

                    // 退避重试仍失败 → 升级为额度耗尽处理（REQ-005）
                    goto case AgentFailureKind.QuotaExhausted;

                case AgentFailureKind.QuotaExhausted:
                    {
                        var record = _ctx.Cooldowns.Record(combo.Account.Id, combo.Model.Name, failure.ResetAt, _ctx.Clock.Now,
                            failure.Raw);
                        _ctx.Raise(new BatchEvent(BatchEventKind.QuotaResetRecorded, _ctx.Clock.Now,
                            $"组合 {combo} 额度已耗尽，记录重置时间 {record.ResetAt:yyyy-MM-dd HH:mm}（{record.DescribeSource()}）",
                            task.Id, combo.Account.Label, combo.Model.Name)
                        { Detail = record.RawValue });
                        _ctx.Logger.WriteQuotaResetRecorded(combo.Account.Id, combo.Model.Name, record.ResetAt, record.Source);
                        _ctx.Cooldowns.PurgeExpired(_ctx.Clock.Now);
                        await _ctx.SaveCooldowns().ConfigureAwait(false);
                        rateLimitStreak = 0;
                        continue;
                    }

                case AgentFailureKind.AuthExpired:
                    combo.Account.Enabled = false;
                    _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                        $"账号 {combo.Account.Label} 凭据失效，已停用并切换下一账号", task.Id, combo.Account.Label));
                    continue;

                case AgentFailureKind.DangerousOperation:
                    _ctx.Raise(new BatchEvent(BatchEventKind.DangerousOperationBlocked, _ctx.Clock.Now,
                        $"检测到危险操作确认，已拒绝自动批准并中断任务 {task.Id}：{failure.Message}",
                        task.Id, combo.Account.Label, combo.Model.Name)
                    { IsDangerous = true });
                    _ctx.Logger.WriteDangerousOperation(task.Id, failure.Message);
                    await FailTask(task, failure.Message).ConfigureAwait(false);
                    return TaskOutcome.Finished;

                case AgentFailureKind.Timeout:
                    await FailTask(task, $"单任务执行超时（{_ctx.Options.TaskTimeoutMinutes} 分钟），已保留部分输出").ConfigureAwait(false);
                    return TaskOutcome.Finished;

                default:
                    normalRetries++;
                    if (normalRetries <= _ctx.Options.MaxRetriesPerTask)
                    {
                        task.RetryCount++;
                        _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                            $"任务 {task.Id} 执行失败，重试第 {normalRetries} 次：{failure.Message}", task.Id));
                        continue;
                    }

                    await FailTask(task, failure.Message).ConfigureAwait(false);
                    if (_ctx.Options.StopOnFailure)
                    {
                        _ctx.SetStatus(BatchStatus.Blocked);
                        _ctx.RaiseBlocked($"任务 {task.Id} 失败，已按「失败即停」暂停批处理", task.Id);
                        return TaskOutcome.StopBatch;
                    }

                    return TaskOutcome.Finished;
            }
        }
    }

    private async Task<AgentExecutionResult> ExecuteWithTimeoutAsync(AgentRequest request, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_ctx.Options.TaskTimeout);

        try
        {
            return await _ctx.Adapter.ExecuteAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AgentExecutionResult.Fail(request,
                new AgentFailure(AgentFailureKind.Timeout, $"执行超过 {_ctx.Options.TaskTimeoutMinutes} 分钟"));
        }
    }

    /// <summary>是否对「额度耗尽」场景放开等待上限（无限等待直到重置）。</summary>
    private bool IsQuotaUnboundedWait(RestoreCandidate? candidate, DateTimeOffset now) =>
        _ctx.Options.WaitForQuotaReset
        && candidate is { WaitKind: WaitKind.QuotaReset }
        && _ctx.MatrixProvider().AllUnavailablePurelyByQuota(now, _ctx.Evaluator);

    // ---------------- 兜底（REQ-004b） ----------------

    private async Task<(bool Success, decimal Cost)> TryFallbackAsync(TaskItem task,
        CancellationToken ct)
    {
        var selection = _ctx.Fallback.TrySelect(task.Id);
        if (selection is null)
        {
            return (false, 0m);
        }

        var account = selection.Account;
        var unitPrice = selection.UnitPrice;

        var fallbackModel = selection.ToModelCandidate();
        var combo = new AgentCombo(account, fallbackModel);

        _ctx.Raise(new BatchEvent(BatchEventKind.FallbackUsed, _ctx.Clock.Now,
            $"免费组合当前不可用，已使用兜底模型 {fallbackModel.Name} 收尾当前任务 {task.Id}",
            task.Id, account.Label, fallbackModel.Name));

        var request = BuildRequest(task, combo, isFallback: true);
        _ctx.Fallback.RecordModelUsed(fallbackModel.Name);

        var result = await ExecuteWithTimeoutAsync(request, ct).ConfigureAwait(false);

        var cost = result.Usage?.Cost ?? unitPrice;
        var basis = result.Usage?.Basis ?? CostBasis.Estimated;

        // P0-1：原写法 Math.Min(cost, Math.Max(MaxCostPerTask, cost)) 恒等于 cost（no-op），
        // 单任务成本上限完全失效 → 实测花费再高也全额计入。改为按 MaxCostPerTask 真实截断。
        // 不变量：TryFallbackAsync 记入兜底累计 / task.FallbackCost 的 cost
        // 必须 ≤ Fallback.MaxCostPerTask（由 BatchEngineTests.兜底成本超单任务上限时按上限截断 锁住）。
        cost = _ctx.Fallback.CapCost(cost);

        if (result.Succeeded)
        {
            _ctx.Fallback.RecordSuccess(cost, basis);

            using (await _ctx.EnterWriteGate().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
            {
                task.Status = TaskStatus.Done;
                task.Result = Truncate(result.Output, _ctx.Options.ResultMaxKB * 1024);
                task.ModelUsed = fallbackModel.Name;
                task.AccountUsed = account.Label;
                task.ExpertUsed = _ctx.Options.Expert.ToString();
                task.FallbackUsed = true;
                task.FallbackCost = cost;
                task.Error = null;
                task.Finish(TaskStatus.Done, _ctx.Clock.Now);
            }

            _ctx.Raise(new BatchEvent(BatchEventKind.FallbackUsed, _ctx.Clock.Now,
                $"兜底完成：任务 {task.Id}（花费 ¥{cost:0.####}，{(basis == CostBasis.Measured ? "实测" : "估算")}）",
                task.Id, account.Label, fallbackModel.Name)
            { Cost = cost, Task = task });
            _ctx.Logger.WriteFallbackUsed(task.Id, fallbackModel.Name, cost, basis);
            _ctx.RaiseTaskChanged(task);
            await _ctx.Persist(ct).ConfigureAwait(false);
            return (true, cost);
        }

        // 兜底本身也失败：按普通失败处理，不循环兜底（边缘情况 10）
        _ctx.Fallback.RecordFailure(cost, basis);
        await FailTask(task, $"兜底模型 {fallbackModel.Name} 执行失败：{result.Failure?.Message ?? "未知错误"}").ConfigureAwait(false);
        return (false, cost);
    }

    // ---------------- 任务状态写入 ----------------

    private async Task CompleteTask(TaskItem task, AgentExecutionResult result)
    {
        var combo = result.Request;

        // P0-4：终态写入与落盘序列化互斥，保证落盘的是一致状态（不会写完 Status
        // 还没写 Result 就被另一个写者序列化走）。事件广播保持在锁外。
        using (await _ctx.EnterWriteGate().ConfigureAwait(false))
        {
            task.Status = TaskStatus.Done;
            task.Result = Truncate(result.Output, _ctx.Options.ResultMaxKB * 1024);
            task.ModelUsed = combo.Model.Name;
            task.AccountUsed = combo.Account.Label;
            task.ExpertUsed = _ctx.Options.Expert.ToString();
            task.FallbackUsed = false;
            task.Error = null;
            task.Finish(TaskStatus.Done, _ctx.Clock.Now);
            ApplyPausedExclusion(task);

            _ctx.Report.TrackUsage(combo.Account.Label, combo.Model.Name, result.Usage);
        }

        _ctx.Raise(new BatchEvent(BatchEventKind.TaskFinished, _ctx.Clock.Now,
            $"任务 {task.Id} 完成（{task.DurationSeconds:0.#}s，{combo.Account.Label} + {combo.Model.Name}）",
            task.Id, combo.Account.Label, combo.Model.Name)
        { Task = task });
        _ctx.Logger.WriteTaskFinished(task.Id, TaskStatus.Done, task.DurationSeconds, task.RetryCount);
        _ctx.RaiseTaskChanged(task);
        await _ctx.Persist(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FailTask(TaskItem task, string error)
    {
        using (await _ctx.EnterWriteGate().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
        {
            task.Status = TaskStatus.Failed;
            task.Error = error;
            task.Finish(TaskStatus.Failed, _ctx.Clock.Now);
            ApplyPausedExclusion(task);
        }

        _ctx.Raise(new BatchEvent(BatchEventKind.TaskFinished, _ctx.Clock.Now,
            $"任务 {task.Id} 失败（重试 {task.RetryCount} 次）：{error}", task.Id)
        { Task = task });
        _ctx.Logger.WriteTaskFinished(task.Id, TaskStatus.Failed, task.DurationSeconds, task.RetryCount);
        _ctx.RaiseTaskChanged(task);
        await _ctx.Persist(CancellationToken.None).ConfigureAwait(false);

        // 二期：任务失败通知（REQ-018；按规则触发，失败不阻断）
        await _ctx.Notifier.DispatchAsync(NotifyTrigger.TaskFailed, null, error).ConfigureAwait(false);
    }

    private async Task MarkInterrupted(TaskItem task, string reason)
    {
        using (await _ctx.EnterWriteGate().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
        {
            if (!string.IsNullOrWhiteSpace(task.Result) && !task.HasPartialResult)
            {
                task.Result = TaskItem.PartialResultPrefix + task.Result;
            }

            task.Status = TaskStatus.Interrupted;
            task.Error = reason;
            task.Finish(TaskStatus.Interrupted, _ctx.Clock.Now);
            ApplyPausedExclusion(task);
        }

        _ctx.Raise(new BatchEvent(BatchEventKind.TaskFinished, _ctx.Clock.Now,
            $"任务 {task.Id} 已中断：{reason}", task.Id)
        { Task = task });
        _ctx.Logger.WriteTaskFinished(task.Id, TaskStatus.Interrupted, task.DurationSeconds, task.RetryCount);
        _ctx.RaiseTaskChanged(task);
        await _ctx.Persist(CancellationToken.None).ConfigureAwait(false);
    }

    private void AppendPartialOutput(TaskItem task, string output)
    {
        var merged = string.IsNullOrWhiteSpace(task.Result) ? output : task.Result + "\n" + output;
        task.Result = Truncate(merged, _ctx.Options.ResultMaxKB * 1024);
    }

    /// <summary>
    /// 从任务时长中剔除其自身经历的暂停等待（DESIGN-额度耗尽暂停续跑：暂停时间不计入时长）。
    /// 仅在终止阶段调用，等待只发生在 <see cref="ExecuteTaskAsync"/> 循环内，故本任务必有对应累计。
    /// </summary>
    private void ApplyPausedExclusion(TaskItem task)
    {
        double waited;
        lock (_ctx.StatsLock)
        {
            if (!_ctx.TaskWaitSeconds.TryGetValue(task.Id, out waited) || waited <= 0)
            {
                return;
            }

            _ctx.TaskWaitSeconds.Remove(task.Id);
        }

        task.DurationSeconds = Math.Max(0, Math.Round(task.DurationSeconds - waited, 3));
    }

    // ---------------- 辅助 ----------------

    private AgentRequest BuildRequest(TaskItem task, AgentCombo combo, bool isFallback)
    {
        var workspace = _ctx.ResolveWorkspace(task.Workspace ?? _ctx.Options.DefaultWorkspace)
                        ?? _ctx.FileSystem.CurrentDirectory;

        return new AgentRequest(
            task.Id,
            task.Instruction,
            combo.Account,
            combo.Model,
            _ctx.Options.Expert,
            workspace,
            _ctx.Options.TaskTimeout,
            _ctx.Options.AutoAnswer)
        {
            IsFallback = isFallback,
            OnOutputChunk = chunk =>
                _ctx.Raise(new BatchEvent(BatchEventKind.TaskOutput, _ctx.Clock.Now, chunk, task.Id))
        };
    }

    /// <summary>
    /// 任务边界凭证切换（DESIGN §6）：请求执行前确保 Live 凭证属于该组合的账号；
    /// 账号变化时执行「保活回写 + 原子回填」，失败则停用该账号走既有降级逻辑。
    /// </summary>
    private async Task<bool> EnsureLiveCredentialAsync(TaskItem task, AgentCombo combo, CancellationToken ct)
    {
        if (_ctx.Vault is null)
        {
            return true;
        }

        var current = _ctx.Vault.CurrentLiveAccount(_ctx.Options.AgentKind);
        if (string.Equals(current, combo.Account.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            await _ctx.Vault.SwitchToAsync(_ctx.Options.AgentKind, combo.Account.Id, "task-boundary", ct)
                .ConfigureAwait(false);
            _ctx.Raise(new BatchEvent(BatchEventKind.AccountSwitched, _ctx.Clock.Now,
                $"凭证已切换：{current ?? "（无）"} → {combo.Account.Label}（任务边界）",
                task.Id, combo.Account.Label, combo.Model.Name));
            _ctx.Logger.WriteModelSwitched(current, null, combo.Account.Id, combo.Model.Name, "凭证切换", task.Id);
            return true;
        }
        catch (CredentialVaultException ex)
        {
            combo.Account.Enabled = false;
            _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                $"账号 {combo.Account.Label} 凭证切换失败，已停用并切换下一账号：{ex.Message}",
                task.Id, combo.Account.Label));
            return false;
        }
    }

    private void RaiseSwitchEvents(AgentCombo from, AgentCombo to, string? taskId)
    {
        if (!string.Equals(from.Account.Id, to.Account.Id, StringComparison.OrdinalIgnoreCase))
        {
            _ctx.Raise(new BatchEvent(BatchEventKind.AccountSwitched, _ctx.Clock.Now,
                $"账号额度耗尽，已切换：{from.Account.Label} → {to.Account.Label}",
                taskId, to.Account.Label, to.Model.Name));
            _ctx.Logger.WriteModelSwitched(from.Account.Label, from.Model.Name, to.Account.Label, to.Model.Name,
                "账号切换", taskId);
        }
        else if (!string.Equals(from.Model.Name, to.Model.Name, StringComparison.OrdinalIgnoreCase))
        {
            _ctx.Raise(new BatchEvent(BatchEventKind.ModelSwitched, _ctx.Clock.Now,
                $"模型 {from.Model.Name} 免费额度已用尽，已自动切换至 {to.Model.Name}，当前任务继续执行",
                taskId, to.Account.Label, to.Model.Name));
            _ctx.Logger.WriteModelSwitched(from.Account.Label, from.Model.Name, to.Account.Label, to.Model.Name,
                "额度耗尽", taskId);
        }

        _ctx.Report.AddTimeline("切换", $"{from} → {to}", taskId, to.Account.Label, to.Model.Name);
    }

    private void RaiseSkipEvent(ComboAvailability skipped)
    {
        switch (skipped.Reason)
        {
            case ComboSkipReason.OutsideFreeWindow:
                _ctx.Raise(new BatchEvent(BatchEventKind.ModelWindowSkipped, _ctx.Clock.Now, skipped.DescribeSkip(),
                    null, skipped.Combo.Account.Label, skipped.Combo.Model.Name));
                _ctx.Logger.WriteWindowSkipped(skipped.Combo.Model.Name, skipped.NextWindowStart);
                _ctx.Report.AddTimeline("时段外跳过", skipped.DescribeSkip(), null, null, skipped.Combo.Model.Name);
                break;
            case ComboSkipReason.Cooldown:
                _ctx.Raise(new BatchEvent(BatchEventKind.ModelCooldownSkipped, _ctx.Clock.Now, skipped.DescribeSkip(),
                    null, skipped.Combo.Account.Label, skipped.Combo.Model.Name));
                if (skipped.ResetAt.HasValue)
                {
                    _ctx.Logger.WriteCooldownSkipped(skipped.Combo.Account.Id, skipped.Combo.Model.Name,
                        skipped.ResetAt.Value);
                }

                _ctx.Report.AddTimeline("冷却跳过", skipped.DescribeSkip(), null, skipped.Combo.Account.Label,
                    skipped.Combo.Model.Name);
                break;
        }
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
}
