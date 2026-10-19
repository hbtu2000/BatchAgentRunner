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

    /// <summary>并行调度协调器（REQ-020 / REQ-021）：在飞任务据此请求延后切换（仅并发态启用）。</summary>
    public DispatchCoordinator? Coordinator { get; init; }

    /// <summary>
    /// 是否启用「并发下延后账号切换」（REQ-021）：仅当 <c>Concurrency &gt; 1</c> 且启用凭证 Vault 时为真。
    /// 为真时，在飞任务遇到需切换到不同账号的需求不即时切换，而是冻结槽位 + 全局派发暂停 + 原账号兜底收尾，
    /// 待在飞全部结束后由协调器统一切换。
    /// </summary>
    public bool DeferredAccountSwitchEnabled { get; init; }
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

    /// <summary>
    /// 单任务执行的可变状态对象（P1-2 内拆）：原 <c>ExecuteTaskAsync</c> 的 <c>while(true)</c> 循环局部变量
    /// 收敛到此处，使状态显式化、循环体退化为薄驱动，便于按子步骤拆分（§4.4 单方法 ≤60 行 / 圈复杂度 ≤15）。
    /// </summary>
    private sealed class TaskExecutionState
    {
        public AgentCombo? Combo;
        public bool RetrySameCombo;
        public int Attempts;
        public int RateLimitStreak;
        public int NormalRetries;
        public string? OriginalAccountId;
        public DateTimeOffset StartedAt;
    }

    /// <summary>单次尝试的结果载体：<see cref="Terminal"/> 非 null 表示直接以该结局返回；<see cref="Result"/> 非 null 表示需进入失败分发。</summary>
    private readonly record struct AttemptResult(TaskOutcome? Terminal, AgentExecutionResult? Result);

    /// <summary>
    /// 单任务状态机驱动（P1-2 内拆后）：仅保留「薄循环 + 分步委派」，所有分支逻辑下沉到下方的子步骤方法。
    /// 行为与原 <c>while(true)+switch+goto</c> 实现严格一致，仅结构重组。
    /// </summary>
    private async Task<TaskOutcome> ExecuteTaskAsync(TaskItem task, CancellationToken ct)
    {
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var startedAt = _ctx.Clock.Now;
        _ctx.SetCurrentTaskId(task.Id);

        await InitializeTaskAsync(task, startedAt).ConfigureAwait(false);

        // 二期：工作流任务走 WorkflowExecutor（多阶段 + 门禁 + 修订循环），复用一期降级/额度/冷却内核
        if (task.IsWorkflow)
        {
            return await _ctx.ExecuteWorkflow(task, taskCts.Token).ConfigureAwait(false);
        }

        var state = new TaskExecutionState { StartedAt = startedAt };

        while (true)
        {
            var limit = await CheckLimitsAsync(task, state, taskCts).ConfigureAwait(false);
            if (limit is not null)
            {
                return limit.Value;
            }

            var (terminal, next) = await SelectComboAsync(task, state, taskCts.Token).ConfigureAwait(false);
            if (terminal is not null)
            {
                return terminal.Value;
            }

            // next 为 null 表示「等待后续跑」的 continue（已在 SelectComboAsync 内处理），直接续循环
            if (next is null)
            {
                continue;
            }

            // 组合切换（含 REQ-021 并发冻结延后切换）：可能提前以 Finished 收尾
            var comboOutcome = await ApplyComboChangeAsync(task, state.Combo, next, state, taskCts.Token).ConfigureAwait(false);
            if (comboOutcome is not null)
            {
                return comboOutcome.Value;
            }

            state.Combo = next;
            state.Attempts++;
            if (state.OriginalAccountId is null)
            {
                state.OriginalAccountId = state.Combo.Account.Id;
            }

            var attempt = await RunAttemptAsync(task, state.Combo, taskCts.Token).ConfigureAwait(false);
            if (attempt.Terminal is not null)
            {
                return attempt.Terminal.Value;
            }

            if (attempt.Result is null)
            {
                // 凭证未就绪（EnsureLiveCredential 失败）→ 续跑重试，不计入终态
                continue;
            }

            if (attempt.Result.Succeeded)
            {
                await CompleteTask(task, attempt.Result).ConfigureAwait(false);
                return TaskOutcome.Finished;
            }

            var failure = attempt.Result.Failure ?? new AgentFailure(AgentFailureKind.TaskError, "未知错误");
            var failureOutcome = await HandleFailureAsync(task, failure, state, taskCts.Token).ConfigureAwait(false);
            if (failureOutcome is not null)
            {
                return failureOutcome.Value;
            }
        }
    }

    /// <summary>任务启动初始化：置 Running / 清 partial / 广播 TaskStarted。</summary>
    private async Task InitializeTaskAsync(TaskItem task, DateTimeOffset startedAt)
    {
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
    }

    /// <summary>循环顶部的取消 / 尝试上限 / 净时长上限检查；返回非 null 表示直接以该结局返回。</summary>
    private async Task<TaskOutcome?> CheckLimitsAsync(TaskItem task, TaskExecutionState state, CancellationTokenSource taskCts)
    {
        if (taskCts.IsCancellationRequested)
        {
            await MarkInterrupted(task, "用户终止，当前任务安全中断").ConfigureAwait(false);
            return TaskOutcome.Terminated;
        }

        if (state.Attempts >= _ctx.Options.MaxAttemptsPerTask)
        {
            await FailTask(task, $"单任务总尝试次数达到上限（{_ctx.Options.MaxAttemptsPerTask} 次）").ConfigureAwait(false);
            return TaskOutcome.Finished;
        }

        if (_ctx.Clock.Now - state.StartedAt > _ctx.Options.MaxTaskDuration)
        {
            // 暂停等待时长不计入「单任务总时长」上限（DESIGN-额度耗尽暂停续跑：暂停不计入时长）
            _ctx.TaskWaitSeconds.TryGetValue(task.Id, out var waitedSoFar);
            if ((_ctx.Clock.Now - state.StartedAt).TotalSeconds - waitedSoFar > _ctx.Options.MaxTaskDuration.TotalSeconds)
            {
                await FailTask(task,
                    $"单任务净执行时长达到上限（{_ctx.Options.MaxTotalMinutesPerTask} 分钟，不含暂停等待）").ConfigureAwait(false);
                return TaskOutcome.Finished;
            }
        }

        return null;
    }

    /// <summary>选下一组合：临时限流重试同一组合 / 矩阵选择 / 未命中走 <see cref="HandleNoComboAsync"/>。</summary>
    private async Task<(TaskOutcome? Terminal, AgentCombo? Next)> SelectComboAsync(TaskItem task, TaskExecutionState state, CancellationToken ct)
    {
        var now = _ctx.Clock.Now;
        AgentCombo? next;

        if (state.RetrySameCombo && state.Combo is not null && _ctx.Evaluator.Evaluate(state.Combo, now).Available)
        {
            // 临时限流：退避后重试同一组合，不切换（REQ-005）
            next = state.Combo;
        }
        else
        {
            var selection = _ctx.MatrixProvider().SelectNext(state.Combo, now, _ctx.Evaluator);

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
                var terminal = await HandleNoComboAsync(task, state, now, ct).ConfigureAwait(false);
                return (terminal, null);
            }
        }

        state.RetrySameCombo = false;
        return (null, next);
    }

    /// <summary>无可用组合时的处理：已投入则兜底、否则按等待窗口 Blocked / 等待后续跑 / 防御性无进展 Blocked。</summary>
    private async Task<TaskOutcome?> HandleNoComboAsync(TaskItem task, TaskExecutionState state, DateTimeOffset now, CancellationToken ct)
    {
        var invested = state.Attempts > 0 || !string.IsNullOrEmpty(task.Result);

        // 兜底：仅对「已投入执行的当前任务」生效（REQ-004b）
        if (invested && _ctx.Options.Fallback.Enabled)
        {
            var fb = await TryFallbackAsync(task, ct).ConfigureAwait(false);
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

        var waited = await _ctx.WaitForRestore(earliest!, task.Id, ct).ConfigureAwait(false);
        if (!waited)
        {
            await MarkInterrupted(task, "用户取消等待，当前任务安全中断").ConfigureAwait(false);
            _ctx.SetStatus(BatchStatus.Blocked);
            _ctx.Raise(new BatchEvent(BatchEventKind.BatchWaitingCancelled, _ctx.Clock.Now,
                "已取消等待，批处理已暂停", task.Id)
            { BatchStatus = BatchStatus.Blocked });
            return TaskOutcome.Blocked;
        }

        // 防御性终止：若等待返回成功，但时钟并未推进到恢复点、且仍无任何可用组合，
        // 说明 WaitForRestore 未实际推进时间（测试假实现异常 / 时钟未前进 / 调度异常）。
        // 继续 continue 会无限自旋、持续累积「组合跳过」事件直至内存耗尽并触发 CLR 致命错（0x80131506）。
        // 生产环境的 WaitForRestoreAsync 总会推进时钟或提前发现可用组合，不会命中此分支；此处仅兜底，避免空转。
        var nowAfterWait = _ctx.Clock.Now;
        var anyAvailableAfterWait = _ctx.MatrixProvider().SelectFirst(nowAfterWait, _ctx.Evaluator).Found;
        if (!anyAvailableAfterWait && nowAfterWait < earliest!.RestoreAt)
        {
            await MarkInterrupted(task,
                $"等待恢复后无进展（时钟未推进至 {earliest.RestoreAt:HH:mm} 且无可用组合），判为阻塞以避免无限空转").ConfigureAwait(false);
            _ctx.SetStatus(BatchStatus.Blocked);
            _ctx.RaiseBlocked(
                $"等待恢复后无进展，批处理已暂停（疑似 WaitForRestore 未实际推进时间），最早恢复 {earliest.RestoreAt:HH:mm}",
                task.Id);
            return TaskOutcome.Blocked;
        }

        return null; // 等待后续跑
    }

    /// <summary>组合切换处理：REQ-021 并发冻结延后切换（原账号兜底收尾，可能提前以 Finished 收尾）；否则仅广播切换事件。</summary>
    private async Task<TaskOutcome?> ApplyComboChangeAsync(TaskItem task, AgentCombo? oldCombo, AgentCombo next, TaskExecutionState state, CancellationToken ct)
    {
        if (oldCombo is not null &&
            _ctx.DeferredAccountSwitchEnabled &&
            _ctx.Coordinator is not null &&
            !string.Equals(next.Account.Id, state.OriginalAccountId, StringComparison.OrdinalIgnoreCase))
        {
            _ctx.Coordinator.RequestDeferredSwitch(task.Id, next.Account.Id);
            _ctx.Raise(new BatchEvent(BatchEventKind.SlotFrozen, _ctx.Clock.Now,
                $"并行执行中，在飞槽位已冻结、进入全局派发暂停；当前在飞任务走付费兜底在原账号 {oldCombo.Account.Label} 完成",
                task.Id, oldCombo.Account.Label));
            _ctx.Raise(new BatchEvent(BatchEventKind.AccountSwitchDeferred, _ctx.Clock.Now,
                $"账号切换已延后至当前在飞任务全部结束后执行（目标：{next.Account.Label}）",
                task.Id, next.Account.Label));

            // 原账号兜底收尾：仅在 originalAccountId 自身兜底池选取（不跨账号）
            var fbDone = await TryFallbackOnAccountAsync(task, oldCombo.Account.Id, ct).ConfigureAwait(false);
            if (fbDone)
            {
                return TaskOutcome.Finished;
            }

            // 原账号兜底池无可用组合或已超成本上限（REQ-021 / F-2）：转入 waiting/blocked（不计入在飞），
            // 释放在飞槽位，待统一切换后由后续任务处理；不阻塞整批。
            await MarkInterrupted(task,
                $"并行冻结任务的原账号 {oldCombo.Account.Label} 兜底不可用，已转入等待/阻塞，待统一切换后由后续任务处理")
                .ConfigureAwait(false);
            return TaskOutcome.Finished;
        }

        if (oldCombo is not null)
        {
            RaiseSwitchEvents(oldCombo, next, task.Id);
        }

        return null;
    }

    /// <summary>单次尝试：确保 Live 凭证（失败返回空 Result 触发续跑）→ 建请求 → 带超时执行 → 追加部分输出。</summary>
    private async Task<AttemptResult> RunAttemptAsync(TaskItem task, AgentCombo combo, CancellationToken ct)
    {
        if (!await EnsureLiveCredentialAsync(task, combo, ct).ConfigureAwait(false))
        {
            return new AttemptResult(null, null);
        }

        var request = BuildRequest(task, combo, isFallback: false);
        _ctx.Raise(new BatchEvent(BatchEventKind.Info, _ctx.Clock.Now,
            $"任务 {task.Id} 使用 {combo.Account.Label} + {combo.Model.Name} 执行",
            task.Id, combo.Account.Label, combo.Model.Name));

        AgentExecutionResult result;
        try
        {
            result = await ExecuteWithTimeoutAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await MarkInterrupted(task, "用户终止，当前任务安全中断").ConfigureAwait(false);
            return new AttemptResult(TaskOutcome.Terminated, null);
        }

        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            AppendPartialOutput(task, result.Output!);
        }

        return new AttemptResult(null, result);
    }

    /// <summary>失败分发：按 <see cref="AgentFailureKind"/> 路由到各分支；返回非 null 表示直接以该结局返回。</summary>
    private async Task<TaskOutcome?> HandleFailureAsync(TaskItem task, AgentFailure failure, TaskExecutionState state, CancellationToken ct)
    {
        switch (failure.Kind)
        {
            case AgentFailureKind.RateLimited:
                state.RateLimitStreak++;
                if (state.RateLimitStreak <= _ctx.Options.RateLimitBackoffRetries)
                {
                    task.RetryCount++;
                    var backoff = _ctx.RetryPolicy.BackoffFor(state.RateLimitStreak);
                    _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                        $"任务 {task.Id} 触发临时限流，{backoff.TotalSeconds:0} 秒后重试同一组合（第 {state.RateLimitStreak} 次）",
                        task.Id, state.Combo!.Account.Label, state.Combo.Model.Name));
                    await _ctx.Clock.DelayAsync(backoff, ct).ConfigureAwait(false);
                    state.RetrySameCombo = true;
                    return null;
                }

                // 退避重试仍失败 → 升级为额度耗尽处理（REQ-005）
                return await HandleQuotaExhaustedAsync(task, failure, state, ct).ConfigureAwait(false);

            case AgentFailureKind.QuotaExhausted:
                return await HandleQuotaExhaustedAsync(task, failure, state, ct).ConfigureAwait(false);

            case AgentFailureKind.AuthExpired:
                state.Combo!.Account.Enabled = false;
                _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                    $"账号 {state.Combo.Account.Label} 凭据失效，已停用并切换下一账号", task.Id, state.Combo.Account.Label));
                return null;

            case AgentFailureKind.DangerousOperation:
                _ctx.Raise(new BatchEvent(BatchEventKind.DangerousOperationBlocked, _ctx.Clock.Now,
                    $"检测到危险操作确认，已拒绝自动批准并中断任务 {task.Id}：{failure.Message}",
                    task.Id, state.Combo!.Account.Label, state.Combo.Model.Name)
                { IsDangerous = true });
                _ctx.Logger.WriteDangerousOperation(task.Id, failure.Message);
                await FailTask(task, failure.Message).ConfigureAwait(false);
                return TaskOutcome.Finished;

            case AgentFailureKind.Timeout:
                await FailTask(task, $"单任务执行超时（{_ctx.Options.TaskTimeoutMinutes} 分钟），已保留部分输出").ConfigureAwait(false);
                return TaskOutcome.Finished;

            default:
                state.NormalRetries++;
                if (state.NormalRetries <= _ctx.Options.MaxRetriesPerTask)
                {
                    task.RetryCount++;
                    _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                        $"任务 {task.Id} 执行失败，重试第 {state.NormalRetries} 次：{failure.Message}", task.Id));
                    return null;
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

    /// <summary>额度耗尽处理：记录冷却重置、清过期、落盘、重置限流连击，续循环。</summary>
    private async Task<TaskOutcome?> HandleQuotaExhaustedAsync(TaskItem task, AgentFailure failure, TaskExecutionState state, CancellationToken ct)
    {
        var combo = state.Combo!;
        var record = _ctx.Cooldowns.Record(combo.Account.Id, combo.Model.Name, failure.ResetAt, _ctx.Clock.Now, failure.Raw);
        _ctx.Raise(new BatchEvent(BatchEventKind.QuotaResetRecorded, _ctx.Clock.Now,
            $"组合 {combo} 额度已耗尽，记录重置时间 {record.ResetAt:yyyy-MM-dd HH:mm}（{record.DescribeSource()}）",
            task.Id, combo.Account.Label, combo.Model.Name)
        { Detail = record.RawValue });
        _ctx.Logger.WriteQuotaResetRecorded(combo.Account.Id, combo.Model.Name, record.ResetAt, record.Source);
        _ctx.Cooldowns.PurgeExpired(_ctx.Clock.Now);
        await _ctx.SaveCooldowns().ConfigureAwait(false);
        state.RateLimitStreak = 0;
        return null;
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

    /// <summary>
    /// 仅从指定账号兜底池兜底收尾（REQ-021 并发冻结任务）：与 <see cref="TryFallbackAsync"/> 流程一致，
    /// 但经 <see cref="IFallbackBilling.TrySelectForAccount"/> 限制为「原启动账号自身兜底池、不跨账号」。
    /// </summary>
    private async Task<bool> TryFallbackOnAccountAsync(TaskItem task, string accountId, CancellationToken ct)
    {
        var selection = _ctx.Fallback.TrySelectForAccount(task.Id, accountId);
        if (selection is null)
        {
            return false;
        }

        var account = selection.Account;
        var unitPrice = selection.UnitPrice;
        var fallbackModel = selection.ToModelCandidate();
        var combo = new AgentCombo(account, fallbackModel);

        _ctx.Raise(new BatchEvent(BatchEventKind.FallbackUsed, _ctx.Clock.Now,
            $"免费组合当前不可用，已使用兜底模型 {fallbackModel.Name}（原账号 {account.Label}）收尾当前任务 {task.Id}",
            task.Id, account.Label, fallbackModel.Name));

        var request = BuildRequest(task, combo, isFallback: true);
        _ctx.Fallback.RecordModelUsed(fallbackModel.Name);

        var result = await ExecuteWithTimeoutAsync(request, ct).ConfigureAwait(false);

        var cost = result.Usage?.Cost ?? unitPrice;
        var basis = result.Usage?.Basis ?? CostBasis.Estimated;
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
                $"兜底完成（原账号）：任务 {task.Id}（花费 ¥{cost:0.####}）",
                task.Id, account.Label, fallbackModel.Name)
            { Cost = cost, Task = task });
            _ctx.Logger.WriteFallbackUsed(task.Id, fallbackModel.Name, cost, basis);
            _ctx.RaiseTaskChanged(task);
            await _ctx.Persist(ct).ConfigureAwait(false);
            return true;
        }

        _ctx.Fallback.RecordFailure(cost, basis);
        await FailTask(task, $"兜底模型 {fallbackModel.Name} 执行失败：{result.Failure?.Message ?? "未知错误"}").ConfigureAwait(false);
        return false;
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

        // REQ-021：并发下禁止即时切换 Live 凭证（在飞任务会新旧账号混用）。活动账号已由引擎在派发前固定
        // （EnsurePreferredLiveAccountAsync）与每次统一切换后重建（SwitchAccountAsync 旋转矩阵首选），
        // 故在飞任务始终共用同一账号，无需、也不允许逐任务切换。
        if (_ctx.DeferredAccountSwitchEnabled)
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
