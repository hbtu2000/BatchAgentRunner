using System.Diagnostics.CodeAnalysis;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Core.Text;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;
// 注：TaskStatus 取自领域内核（Domain），与 System.Threading.Tasks.TaskStatus 同名，此处以别名消解歧义。

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 单任务状态机执行体（P-03 拆分产物 ②）：承载「单任务状态机（重试 / 退避 / 降级 / 超时 / 兜底 / 终止）」的全部步骤，
/// 与 <see cref="BatchEngine"/> 抽出的「单任务状态机（重试 / 退避 / 降级 / 超时）」逻辑同源，但决策已下沉：
/// <list type="bullet">
///   <item>降级 / 冷却 / 兜底 / 免费时段四类判定 → <see cref="DegradationPolicy"/> / <see cref="CooldownPolicy"/> /
///     <see cref="FallbackPolicy"/> / <see cref="FreeTimeWindowPolicy"/> 四个纯函数式策略对象。</item>
///   <item>终态写入 / 请求构建 / 凭证切换 / 事件上报 → <see cref="TaskTerminalWriter"/> / <see cref="TaskRequestBuilder"/> /
///     <see cref="TaskCredentialSwitch"/> / <see cref="TaskEventReporter"/> 四个协作器。</item>
/// </list>
/// 本类型即 P-03「状态机驱动」本体；<see cref="TaskRunner"/> 仅作薄 facade 委托至此（§3.3）。
/// </summary>
public sealed class TaskStepExecutor
{
    private readonly TaskRunnerContext _ctx;
    private readonly DegradationPolicy _degradation;
    private readonly CooldownPolicy _cooldown;
    private readonly FreeTimeWindowPolicy _freeTime;
    private readonly FallbackPolicy _fallback;

    public TaskStepExecutor(TaskRunnerContext context)
    {
        _ctx = context;
        var evaluator = context.Evaluator;
        // 注意：此处传入 MatrixProvider 委托而非 MatrixProvider() 的即时结果，
        // 使降级 / 免费时段策略在每次判定时现取矩阵，从而观察 NotifyConfigurationChanged() 后的重建矩阵（P-03 复测修复）。
        _degradation = new DegradationPolicy(context.MatrixProvider, evaluator, context.Options);
        _cooldown = new CooldownPolicy(context.Cooldowns);
        _freeTime = new FreeTimeWindowPolicy(context.Options, context.MatrixProvider, evaluator);
        _fallback = new FallbackPolicy(context.Fallback);
    }

    public Task<TaskOutcome> RunSafeAsync(TaskItem task, CancellationToken ct) => ExecuteTaskSafeAsync(task, ct);

    /// <summary>
    /// 单任务执行兜底：引擎自身的未预期异常（如启动进程失败、落盘失败）不允许静默冒泡
    /// 杀死整批（2026-10-16 实测：异常被 UI 吞掉、运行日志无记录、报告不落盘，排障极困难）。
    /// 此处记录 ERROR 日志与事件、任务判失败后继续下一任务；用户取消（OCE）按终止处理。
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
            await TaskTerminalWriter.MarkInterruptedAsync(_ctx, task, "用户终止，当前任务安全中断").ConfigureAwait(false);
            outcome = TaskOutcome.Terminated;
        }
        catch (Exception ex)
        {
            _ctx.Logger.Write(RunLogLevels.Error, $"任务 {task.Id} 执行发生未预期异常：{ex.GetType().Name}：{ex.Message}");
            _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                $"任务 {task.Id} 执行异常（{ex.GetType().Name}）：{ex.Message}", task.Id));
            try
            {
                await TaskTerminalWriter.FailTaskAsync(_ctx, task, $"执行异常（{ex.GetType().Name}）：{ex.Message}").ConfigureAwait(false);
            }
            catch (Exception persistEx)
            {
                _ctx.Logger.Write(RunLogLevels.Error, $"任务 {task.Id} 失败落盘也异常：{persistEx.GetType().Name}：{persistEx.Message}");
            }

            outcome = TaskOutcome.Finished;
        }

        await _ctx.GitWorkflow.CompleteAsync(task, gitContext, ct).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// 单任务状态机驱动（P-03 内拆后）：仅保留「薄循环 + 分步委派」，所有分支逻辑下沉到下方的子步骤方法 / 策略对象。
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
            // 误切换判据：本次尝试之前刚发生过切换（切换带来的可用性收益须由这次尝试验证）
            var switchedBeforeAttempt = state.SwitchedBeforeAttempt;
            state.SwitchedBeforeAttempt = false;
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
                await TaskTerminalWriter.CompleteTaskAsync(_ctx, task, attempt.Result).ConfigureAwait(false);
                return TaskOutcome.Finished;
            }

            var failure = attempt.Result.Failure ?? new AgentFailure(AgentFailureKind.TaskError, "未知错误");
            var failureOutcome = await HandleFailureAsync(task, failure, state, switchedBeforeAttempt, taskCts.Token)
                .ConfigureAwait(false);
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
            _ctx.Logger.Write(RunLogLevels.Info, $"任务 {task.Id} 清除上次部分输出：{TextTruncate.Result(task.Result, 200)}");
            task.Result = null;
        }

        _ctx.RaiseTaskChanged(task);
        _ctx.Logger.WriteTaskStarted(task.Id, null, null, _ctx.Options.Expert.ToString(), task.Workspace);
        // P-10 / A-8：任务开始计数（吞吐与"在飞"口径的分子侧）
        _ctx.Metrics.RecordTaskStarted();
        _ctx.Raise(new BatchEvent(BatchEventKind.TaskStarted, _ctx.Clock.Now, $"开始执行任务 {task.Id}", task.Id) { Task = task });
    }

    /// <summary>循环顶部的取消 / 尝试上限 / 净时长上限检查；返回非 null 表示直接以该结局返回。</summary>
    private async Task<TaskOutcome?> CheckLimitsAsync(TaskItem task, TaskExecutionState state, CancellationTokenSource taskCts)
    {
        if (taskCts.IsCancellationRequested)
        {
            await TaskTerminalWriter.MarkInterruptedAsync(_ctx, task, "用户终止，当前任务安全中断").ConfigureAwait(false);
            return TaskOutcome.Terminated;
        }

        if (state.Attempts >= _ctx.Options.MaxAttemptsPerTask)
        {
            await TaskTerminalWriter.FailTaskAsync(_ctx, task, $"单任务总尝试次数达到上限（{_ctx.Options.MaxAttemptsPerTask} 次）").ConfigureAwait(false);
            return TaskOutcome.Finished;
        }

        if (_ctx.Clock.Now - state.StartedAt > _ctx.Options.MaxTaskDuration)
        {
            // 暂停等待时长不计入「单任务总时长」上限（DESIGN-额度耗尽暂停续跑：暂停不计入时长）
            _ctx.TaskWaitSeconds.TryGetValue(task.Id, out var waitedSoFar);
            if ((_ctx.Clock.Now - state.StartedAt).TotalSeconds - waitedSoFar > _ctx.Options.MaxTaskDuration.TotalSeconds)
            {
                await TaskTerminalWriter.FailTaskAsync(_ctx, task,
                    $"单任务净执行时长达到上限（{_ctx.Options.MaxTotalMinutesPerTask} 分钟，不含暂停等待）").ConfigureAwait(false);
                return TaskOutcome.Finished;
            }
        }

        return null;
    }

    /// <summary>选下一组合：临时限流重试同一组合 / 矩阵选择（经 <see cref="DegradationPolicy"/>）/ 未命中走 <see cref="HandleNoComboAsync"/>。</summary>
    private async Task<(TaskOutcome? Terminal, AgentCombo? Next)> SelectComboAsync(TaskItem task, TaskExecutionState state, CancellationToken ct)
    {
        var now = _ctx.Clock.Now;
        var decision = _degradation.DecideNext(state.Combo, state.RetrySameCombo, now);

        foreach (var skipped in decision.Skipped)
        {
            TaskEventReporter.RaiseSkipEvent(_ctx, skipped);
        }

        if (!decision.HasSelection)
        {
            var terminal = await HandleNoComboAsync(task, state, now, ct).ConfigureAwait(false);
            return (terminal, null);
        }

        state.RetrySameCombo = false;
        return (null, decision.NextCombo);
    }

    /// <summary>无可用组合时的处理：已投入则兜底、否则按等待窗口 Blocked / 等待后续跑 / 防御性无进展 Blocked。</summary>
    private async Task<TaskOutcome?> HandleNoComboAsync(TaskItem task, TaskExecutionState state, DateTimeOffset now, CancellationToken ct)
    {
        var invested = state.Attempts > 0 || !string.IsNullOrEmpty(task.Result);

        // 兜底：仅对「已投入执行的当前任务」生效（REQ-004b）
        if (_fallback.ShouldAttempt(invested))
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
        var wait = _freeTime.DecideWait(earliest, now);
        if (!wait.ShouldWait)
        {
            await TaskTerminalWriter.MarkInterruptedAsync(_ctx, task, wait.BlockReason!).ConfigureAwait(false);
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
            await TaskTerminalWriter.MarkInterruptedAsync(_ctx, task, "用户取消等待，当前任务安全中断").ConfigureAwait(false);
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
            await TaskTerminalWriter.MarkInterruptedAsync(_ctx, task,
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
                task.Id, oldCombo.Account.Label, next.Model.Name));
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
            await TaskTerminalWriter.MarkInterruptedAsync(_ctx, task,
                $"并行冻结任务的原账号 {oldCombo.Account.Label} 兜底不可用，已转入等待/阻塞，待统一切换后由后续任务处理")
                .ConfigureAwait(false);
            return TaskOutcome.Finished;
        }

        if (oldCombo is not null)
        {
            TaskEventReporter.RaiseSwitchEvents(_ctx, oldCombo, next, task.Id);
            if (!string.Equals(oldCombo.Account.Id, next.Account.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(oldCombo.Model.Name, next.Model.Name, StringComparison.OrdinalIgnoreCase))
            {
                // 本次尝试是「切换后的第一次尝试」，其结果用于判定这次切换是否白切（误切换率）
                state.SwitchedBeforeAttempt = true;
            }
        }

        return null;
    }

    /// <summary>单次尝试：确保 Live 凭证（失败返回空 Result 触发续跑）→ 建请求 → 带超时执行 → 追加部分输出。</summary>
    private async Task<AttemptResult> RunAttemptAsync(TaskItem task, AgentCombo combo, CancellationToken ct)
    {
        if (!await TaskCredentialSwitch.EnsureLiveCredentialAsync(_ctx, task, combo, ct).ConfigureAwait(false))
        {
            return new AttemptResult(null, null);
        }

        var request = TaskRequestBuilder.BuildRequest(_ctx, task, combo, isFallback: false);
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
            await TaskTerminalWriter.MarkInterruptedAsync(_ctx, task, "用户终止，当前任务安全中断").ConfigureAwait(false);
            return new AttemptResult(TaskOutcome.Terminated, null);
        }

        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            TaskTerminalWriter.AppendPartialOutput(_ctx, task, result.Output!);
        }

        return new AttemptResult(null, result);
    }

    /// <summary>失败分发：经 <see cref="DegradationPolicy.DecideFailure"/> 纯判定后解释决策（副作用在此执行）。</summary>
    private async Task<TaskOutcome?> HandleFailureAsync(TaskItem task, AgentFailure failure,
        TaskExecutionState state, bool switchedBeforeAttempt, CancellationToken ct)
    {
        var decision = _degradation.DecideFailure(failure, state.RateLimitStreak, state.NormalRetries);
        switch (decision.Kind)
        {
            case FailureKind.RetrySame:
                state.RateLimitStreak = decision.RateLimitStreak!.Value;
                task.RetryCount++;
                var backoff = _ctx.RetryPolicy.BackoffFor(state.RateLimitStreak);
                _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                    $"任务 {task.Id} 触发临时限流，{backoff.TotalSeconds:0} 秒后重试同一组合（第 {state.RateLimitStreak} 次）",
                    task.Id, state.Combo!.Account.Label, state.Combo.Model.Name));
                await _ctx.Clock.DelayAsync(backoff, ct).ConfigureAwait(false);
                state.RetrySameCombo = true;
                return null;

            case FailureKind.RecordCooldown:
                // P-10 / A-8：刚切换完就又额度耗尽 → 这次切换没换来可用组合，计「误切换」
                if (switchedBeforeAttempt)
                {
                    _ctx.Metrics.RecordMisswitch();
                }

                return await HandleQuotaExhaustedAsync(task, failure, state, ct).ConfigureAwait(false);

            case FailureKind.DisableAccount:
                // 凭据失效同属「切换后仍不可用」
                if (switchedBeforeAttempt)
                {
                    _ctx.Metrics.RecordMisswitch();
                }

                state.Combo!.Account.Enabled = false;
                _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                    $"账号 {state.Combo.Account.Label} 凭据失效，已停用并切换下一账号", task.Id, state.Combo.Account.Label));
                return null;

            case FailureKind.RetryTask:
                state.NormalRetries = decision.NormalRetries!.Value;
                task.RetryCount++;
                _ctx.Raise(new BatchEvent(BatchEventKind.Warning, _ctx.Clock.Now,
                    $"任务 {task.Id} 执行失败，重试第 {state.NormalRetries} 次：{failure.Message}", task.Id));
                return null;

            case FailureKind.FailTaskDangerous:
                _ctx.Raise(new BatchEvent(BatchEventKind.DangerousOperationBlocked, _ctx.Clock.Now,
                    $"检测到危险操作确认，已拒绝自动批准并中断任务 {task.Id}：{decision.Message}", task.Id, state.Combo!.Account.Label, state.Combo.Model.Name)
                { IsDangerous = true });
                _ctx.Logger.WriteDangerousOperation(task.Id, decision.Message!);
                // P-10 / A-8：高危确认点被拒绝的次数（安全维度指标）
                _ctx.Metrics.RecordDangerousBlock();
                await TaskTerminalWriter.FailTaskAsync(_ctx, task, decision.Message!).ConfigureAwait(false);
                return TaskOutcome.Finished;

            case FailureKind.FailTask:
                await TaskTerminalWriter.FailTaskAsync(_ctx, task, decision.Message!).ConfigureAwait(false);
                return TaskOutcome.Finished;

            case FailureKind.FailTaskStopBatch:
                await TaskTerminalWriter.FailTaskAsync(_ctx, task, decision.Message!).ConfigureAwait(false);
                _ctx.SetStatus(BatchStatus.Blocked);
                _ctx.RaiseBlocked($"任务 {task.Id} 失败，已按「失败即停」暂停批处理", task.Id);
                return TaskOutcome.StopBatch;

            default:
                return null;
        }
    }

    /// <summary>额度耗尽处理：记录冷却重置、清过期、重置限流连击，续循环（冷却判定经 <see cref="CooldownPolicy"/>）。</summary>
    private async Task<TaskOutcome?> HandleQuotaExhaustedAsync(TaskItem task, AgentFailure failure, TaskExecutionState state, CancellationToken ct)
    {
        var combo = state.Combo!;
        var record = _cooldown.RecordAndPurge(combo.Account.Id, combo.Model.Name, failure.ResetAt, _ctx.Clock.Now, failure.Raw);
        _ctx.Raise(new BatchEvent(BatchEventKind.QuotaResetRecorded, _ctx.Clock.Now,
            $"组合 {combo} 额度已耗尽，记录重置时间 {record.ResetAt:yyyy-MM-dd HH:mm}（{record.DescribeSource()}）",
            task.Id, combo.Account.Label, combo.Model.Name)
        { Detail = record.RawValue });
        _ctx.Logger.WriteQuotaResetRecorded(combo.Account.Id, combo.Model.Name, record.ResetAt, record.Source);
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

    // ---------------- 兜底（REQ-004b，选取/成本截断判定经 FallbackPolicy） ----------------

    private async Task<(bool Success, decimal Cost)> TryFallbackAsync(TaskItem task, CancellationToken ct)
    {
        var selection = _fallback.Select(task.Id);
        if (selection is null)
        {
            return (false, 0m);
        }

        return await RunFallbackAsync(selection, task, accountScoped: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 仅从指定账号兜底池兜底收尾（REQ-021 并发冻结任务）：与 <see cref="TryFallbackAsync"/> 流程一致，
    /// 但经 <see cref="FallbackPolicy.SelectForAccount"/> 限制为「原启动账号自身兜底池、不跨账号」。
    /// </summary>
    private async Task<bool> TryFallbackOnAccountAsync(TaskItem task, string accountId, CancellationToken ct)
    {
        var selection = _fallback.SelectForAccount(task.Id, accountId);
        if (selection is null)
        {
            return false;
        }

        var (success, _) = await RunFallbackAsync(selection, task, accountScoped: true, ct).ConfigureAwait(false);
        return success;
    }

    private async Task<(bool Success, decimal Cost)> RunFallbackAsync(FallbackSelection selection, TaskItem task, bool accountScoped, CancellationToken ct)
    {
        var account = selection.Account;
        var unitPrice = selection.UnitPrice;
        var fallbackModel = selection.ToModelCandidate();
        var combo = new AgentCombo(account, fallbackModel);

        _ctx.Raise(new BatchEvent(BatchEventKind.FallbackUsed, _ctx.Clock.Now,
            accountScoped
                ? $"免费组合当前不可用，已使用兜底模型 {fallbackModel.Name}（原账号 {account.Label}）收尾当前任务 {task.Id}"
                : $"免费组合当前不可用，已使用兜底模型 {fallbackModel.Name} 收尾当前任务 {task.Id}",
            task.Id, account.Label, fallbackModel.Name));

        var request = TaskRequestBuilder.BuildRequest(_ctx, task, combo, isFallback: true);
        _ctx.Fallback.RecordModelUsed(fallbackModel.Name);
        // P-10 / A-8：兜底触发次数（真正发起兜底请求才算触发，选中但成本超限未发起不计）
        _ctx.Metrics.RecordFallback();

        var result = await ExecuteWithTimeoutAsync(request, ct).ConfigureAwait(false);

        var cost = result.Usage?.Cost ?? unitPrice;
        var basis = result.Usage?.Basis ?? CostBasis.Estimated;

        // P0-1：原写法 Math.Min(cost, Math.Max(MaxCostPerTask, cost)) 恒等于 cost（no-op），
        // 单任务成本上限完全失效 → 实测花费再高也全额计入。改为按 MaxCostPerTask 真实截断。
        // 不变量：记入兜底累计 / task.FallbackCost 的 cost 必须 ≤ Fallback.MaxCostPerTask
        // （由 BatchEngineTests.兜底成本超单任务上限时按上限截断 锁住）。
        cost = _fallback.CapCost(cost);

        if (result.Succeeded)
        {
            _ctx.Fallback.RecordSuccess(cost, basis);

            using (await _ctx.EnterWriteGate().ConfigureAwait(false)) // P0-4：与落盘序列化互斥
            {
                task.Status = TaskStatus.Done;
                task.Result = TextTruncate.Result(result.Output, _ctx.Options.ResultMaxKB * 1024);
                task.ModelUsed = fallbackModel.Name;
                task.AccountUsed = account.Label;
                task.ExpertUsed = _ctx.Options.Expert.ToString();
                task.FallbackUsed = true;
                task.FallbackCost = cost;
                task.Error = null;
                task.Finish(TaskStatus.Done, _ctx.Clock.Now);
            }

            var basisNote = basis == CostBasis.Measured ? "实测" : "估算";
            _ctx.Raise(new BatchEvent(BatchEventKind.FallbackUsed, _ctx.Clock.Now,
                accountScoped
                    ? $"兜底完成（原账号）：任务 {task.Id}（花费 ¥{cost:0.####}）"
                    : $"兜底完成：任务 {task.Id}（花费 ¥{cost:0.####}，{basisNote}）",
                task.Id, account.Label, fallbackModel.Name)
            { Cost = cost, Task = task });
            _ctx.Logger.WriteFallbackUsed(task.Id, fallbackModel.Name, cost, basis);
            _ctx.RaiseTaskChanged(task);
            await _ctx.Persist(ct).ConfigureAwait(false);
            return (true, cost);
        }

        // 兜底本身也失败：按普通失败处理，不循环兜底（边缘情况 10）
        _ctx.Fallback.RecordFailure(cost, basis);
        await TaskTerminalWriter.FailTaskAsync(_ctx, task, $"兜底模型 {fallbackModel.Name} 执行失败：{result.Failure?.Message ?? "未知错误"}").ConfigureAwait(false);
        return (false, cost);
    }

    /// <summary>
    /// 单任务执行的可变状态对象（P-03 内拆）：原 <c>ExecuteTaskAsync</c> 的 <c>while(true)</c> 循环局部变量
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

        /// <summary>
        /// 本次尝试之前是否刚发生过组合切换（P-10 / A-8 误切换率判据）。
        /// 切换本身不是错，错在切换后仍未换来可用组合——故须由切换后的第一次尝试结果来判定。
        /// </summary>
        public bool SwitchedBeforeAttempt;
    }

    /// <summary>单次尝试的结果载体：<see cref="Terminal"/> 非 null 表示直接以该结局返回；<see cref="Result"/> 非 null 表示需进入失败分发。</summary>
    private readonly record struct AttemptResult(TaskOutcome? Terminal, AgentExecutionResult? Result);
}
