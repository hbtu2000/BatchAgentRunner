using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Core.Text;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Core.Engine;

/// <summary>工作流执行所需共享服务（引擎注入，避免 WorkflowExecutor 直接依赖引擎私有状态）。</summary>
public sealed class WorkflowEngineContext
{
    public required RunOptions Options { get; init; }
    public required IAgentCliAdapter Adapter { get; init; }
    public required DegradationMatrix Matrix { get; init; }
    public required AvailabilityEvaluator Evaluator { get; init; }
    public required QuotaCooldownRegistry Cooldowns { get; init; }
    public required QuotaErrorClassifier Classifier { get; init; }
    public required IClock Clock { get; init; }
    public required IRunLogger Logger { get; init; }
    public required GateEvaluator GateEvaluator { get; init; }
    public Action<BatchEvent>? OnEvent { get; init; }
    public Action<TaskItem>? OnTaskChanged { get; init; }
    public Func<Task>? Persist { get; init; }
    public Func<string, string, AgentUsage?, Task>? TrackUsage { get; init; }
    public Func<TaskItem, AgentCombo, CancellationToken, Task<bool>>? EnsureCredential { get; init; }
    public SessionResumePolicy SessionPolicy { get; init; } = new(false);
    public int DefaultMaxGateLoops { get; init; } = 3;
}

/// <summary>工作流执行结果（交回引擎落盘）。</summary>
public sealed class WorkflowOutcome
{
    public TaskStatus FinalStatus { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public List<StageResult> StageResults { get; init; } = new();
    public string? ModelUsed { get; init; }
    public string? AccountUsed { get; init; }
    public string? ExpertUsed { get; init; }
    public bool FallbackUsed { get; init; }
    public decimal? FallbackCost { get; init; }
}

/// <summary>
/// 工作流执行器（二期核心）：按阶段顺序驱动，每个阶段复用一期「单指令执行」能力
/// （降级矩阵 / 免费时段 / 额度冷却 / 兜底 / 危险操作拦截），并叠加质量门禁与修订循环。
/// 设计为自包含编排器，由 <see cref="BatchEngine"/> 在检测到 <see cref="TaskItem.Workflow"/> 时委托调用，
/// 不改动一期既有单任务执行路径。
/// </summary>
public sealed class WorkflowExecutor
{
    private readonly WorkflowEngineContext _ctx;

    public WorkflowExecutor(WorkflowEngineContext ctx) => _ctx = ctx;

    public async Task<WorkflowOutcome> RunAsync(TaskItem task, CancellationToken ct)
    {
        var workflow = task.Workflow!;
        var stageResults = new List<StageResult>();
        var renderCtx = new WorkflowRenderContext();
        SeedTaskFields(renderCtx, task);

        var lastModel = _ctx.Options.ModelCandidates.FirstOrDefault()?.Name;
        var lastAccount = _ctx.Options.Accounts.FirstOrDefault()?.DisplayName;

        for (var si = 0; si < workflow.Stages.Count; si++)
        {
            var stage = workflow.Stages[si];
            var stageResult = new StageResult { StageId = stage.Id, Name = stage.Name };

            // 阶段级依赖：前置阶段必须 Done
            if (!string.IsNullOrEmpty(stage.DependsOnStage))
            {
                var dep = stageResults.FirstOrDefault(r => string.Equals(r.StageId, stage.DependsOnStage, StringComparison.OrdinalIgnoreCase));
                if (dep is null || dep.Status != TaskStatus.Done)
                {
                    stageResult.Status = TaskStatus.Blocked;
                    stageResult.OutputSummary = $"依赖阶段 {stage.DependsOnStage} 未完成";
                    stageResults.Add(stageResult);
                    return Finalize(workflow, task, stageResults, TaskStatus.NeedsAttention,
                        null, "工作流因阶段依赖未满足而中断", lastModel, lastAccount);
                }
            }

            var loopGuard = 0;
            var maxLoops = stage.MaxLoops ?? _ctx.DefaultMaxGateLoops;
            var instructionBase = StageTemplateRenderer.Render(stage.InstructionTemplate, renderCtx).Text;
            var instruction = instructionBase;

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    stageResult.Status = TaskStatus.Interrupted;
                    stageResults.Add(stageResult);
                    return Finalize(workflow, task, stageResults, TaskStatus.Interrupted, null, "用户终止",
                        lastModel, lastAccount);
                }

                var run = await RunStageInstructionAsync(task, stage, instruction, ct).ConfigureAwait(false);

                if (!run.Succeeded)
                {
                    stageResult.Status = run.Interrupted ? TaskStatus.Interrupted : TaskStatus.Failed;
                    stageResult.OutputSummary = Truncate(run.Error, 500);
                    stageResults.Add(stageResult);
                    return Finalize(workflow, task, stageResults,
                        run.Interrupted ? TaskStatus.Interrupted : MapStageFailure(workflow, stage),
                        null, run.Error, lastModel, lastAccount);
                }

                renderCtx.SetStageOutput(stage.Id, run.Output ?? string.Empty);
                lastModel = run.ModelUsed;
                lastAccount = run.AccountUsed;

                // 门禁判定
                if (stage.Gate is not null)
                {
                    var gateCtx = new GateEvaluationContext
                    {
                        Gate = stage.Gate,
                        StageOutput = run.Output ?? string.Empty,
                        WorkingDirectory = ResolveWorkspace(task),
                        TaskId = task.Id,
                        StageId = stage.Id,
                        AccountUsed = run.AccountUsed,
                        ModelUsed = run.ModelUsed
                    };
                    var gate = await _ctx.GateEvaluator.EvaluateAsync(stage.Gate, gateCtx, ct).ConfigureAwait(false);
                    stageResult.LoopCount = loopGuard + 1;
                    stageResult.GatePassed = gate.Passed;

                    if (gate.Passed)
                    {
                        stageResult.Status = TaskStatus.Done;
                        stageResult.OutputSummary = Truncate(run.Output, 800);
                        stageResult.ModelUsed = run.ModelUsed;
                        stageResult.AccountUsed = run.AccountUsed;
                        stageResults.Add(stageResult);
                        break;
                    }

                    loopGuard++;
                    if (loopGuard >= maxLoops)
                    {
                        stageResult.Status = TaskStatus.NeedsAttention;
                        stageResult.GateLeftover = gate.Leftover;
                        stageResult.OutputSummary = Truncate(run.Output, 400);
                        stageResults.Add(stageResult);
                        return Finalize(workflow, task, stageResults, MapGateFail(workflow, stage),
                            null, $"阶段 {stage.Name} 门禁修订循环达上限（{maxLoops} 次）未通过，遗留问题：{Truncate(gate.Leftover, 300)}",
                            lastModel, lastAccount);
                    }

                    // 修订循环：把遗留问题作为补充指令重跑本阶段
                    instruction = instructionBase + "\n\n门禁未通过，遗留问题：\n" + (gate.Leftover ?? "（无详情）");
                    Raise(new BatchEvent(BatchEventKind.Info, _ctx.Clock.Now,
                        $"任务 {task.Id} 阶段 {stage.Name} 门禁未通过，进入第 {loopGuard + 1} 次修订循环", task.Id));
                    continue;
                }

                stageResult.Status = TaskStatus.Done;
                stageResult.OutputSummary = Truncate(run.Output, 800);
                stageResult.ModelUsed = run.ModelUsed;
                stageResult.AccountUsed = run.AccountUsed;
                stageResults.Add(stageResult);
                break;
            }
        }

        // 汇总各阶段产出
        var summary = string.Join("\n\n", stageResults.Where(r => !string.IsNullOrEmpty(r.OutputSummary))
            .Select(r => $"## {r.Name ?? r.StageId}\n{r.OutputSummary}"));
        return Finalize(workflow, task, stageResults, TaskStatus.Done, summary, null, lastModel, lastAccount);
    }

    // ---------------- 单阶段指令执行（复用一期降级/额度/冷却/兜底语义） ----------------

    private async Task<StageRunResult> RunStageInstructionAsync(TaskItem task, WorkflowStage stage, string instruction, CancellationToken ct)
    {
        var combo = (AgentCombo?)null;
        var retrySameCombo = false;
        var attempts = 0;
        var rateLimitStreak = 0;
        var normalRetries = 0;

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return StageRunResult.CreateInterrupted();
            }

            if (attempts >= _ctx.Options.MaxAttemptsPerTask)
            {
                return StageRunResult.Failed("单阶段总尝试次数达到上限");
            }

            var now = _ctx.Clock.Now;
            AgentCombo? next;

            if (retrySameCombo && combo is not null && _ctx.Evaluator.Evaluate(combo, now).Available)
            {
                next = combo;
            }
            else
            {
                next = ResolveCombo(stage, now);
                if (next is null)
                {
                    var earliest = _ctx.Matrix.EarliestRestore(now, _ctx.Evaluator);
                    var canWait = earliest is not null &&
                                  (IsQuotaUnboundedWait(earliest, now) ||
                                   earliest.RestoreAt <= now.Add(_ctx.Options.MaxWait));
                    if (canWait && earliest is not null)
                    {
                        var waited = await WaitForRestoreAsync(earliest, task.Id, ct).ConfigureAwait(false);
                        if (!waited)
                        {
                            return StageRunResult.CreateInterrupted();
                        }

                        continue;
                    }

                    // 兜底（仅对已投入执行的阶段生效）
                    if (attempts > 0 && _ctx.Options.Fallback.Enabled)
                    {
                        var fb = TryFallback();
                        if (fb is not null)
                        {
                            var r = await ExecuteComboAsync(task, stage, instruction, fb, isFallback: true, ct).ConfigureAwait(false);
                            if (r.Succeeded)
                            {
                                return r;
                            }

                            return StageRunResult.Failed(r.Error ?? "兜底执行失败");
                        }
                    }

                    return StageRunResult.Failed("工作流阶段当前无可用组合（额度耗尽 / 时段外 / 冷却中）");
                }
            }

            retrySameCombo = false;
            if (combo is not null && !ReferenceEquals(next, combo))
            {
                Raise(new BatchEvent(BatchEventKind.ModelSwitched, _ctx.Clock.Now,
                    $"阶段组合切换：{combo} → {next}", task.Id, next.Account.DisplayName, next.Model.Name));
            }

            combo = next;
            attempts++;

            if (_ctx.EnsureCredential is not null && !await _ctx.EnsureCredential(task, combo, ct).ConfigureAwait(false))
            {
                continue;
            }

            var result = await ExecuteComboAsync(task, stage, instruction, combo, isFallback: false, ct).ConfigureAwait(false);
            if (result.Succeeded || result.Interrupted)
            {
                return result;
            }

            var failure = result.Failure!;
            switch (failure.Kind)
            {
                case AgentFailureKind.RateLimited:
                    rateLimitStreak++;
                    if (rateLimitStreak <= _ctx.Options.RateLimitBackoffRetries)
                    {
                        var backoff = TimeSpan.FromSeconds(Math.Max(1, _ctx.Options.RateLimitInitialBackoffSeconds));
                        await _ctx.Clock.DelayAsync(backoff, ct).ConfigureAwait(false);
                        retrySameCombo = true;
                        continue;
                    }

                    goto case AgentFailureKind.QuotaExhausted;

                case AgentFailureKind.QuotaExhausted:
                    var rec = _ctx.Cooldowns.Record(combo.Account.Id, combo.Model.Name, failure.ResetAt, _ctx.Clock.Now, failure.Raw);
                    _ctx.Logger.WriteQuotaResetRecorded(combo.Account.Id, combo.Model.Name, rec.ResetAt, rec.Source);
                    rateLimitStreak = 0;
                    continue;

                case AgentFailureKind.AuthExpired:
                    combo!.Account.Enabled = false;
                    continue;

                case AgentFailureKind.DangerousOperation:
                    return StageRunResult.Failed(failure.Message);

                case AgentFailureKind.Timeout:
                    return StageRunResult.Failed($"单阶段执行超时（{_ctx.Options.TaskTimeoutMinutes} 分钟）");

                default:
                    normalRetries++;
                    if (normalRetries <= _ctx.Options.MaxRetriesPerTask)
                    {
                        continue;
                    }

                    return StageRunResult.Failed(failure.Message);
            }
        }
    }

    private async Task<StageRunResult> ExecuteComboAsync(TaskItem task, WorkflowStage stage, string instruction, AgentCombo combo, bool isFallback, CancellationToken ct)
    {
        var workspace = ResolveWorkspace(task);
        var expert = stage.Executor?.ExpertId is { Length: > 0 } ex ? new ExpertRef(ex) : _ctx.Options.Expert;
        var request = new AgentRequest(
            task.Id, instruction, combo.Account, combo.Model, expert, workspace, _ctx.Options.TaskTimeout, _ctx.Options.AutoAnswer)
        {
            IsFallback = isFallback,
            OnOutputChunk = chunk => Raise(new BatchEvent(BatchEventKind.TaskOutput, _ctx.Clock.Now, chunk, task.Id))
        };

        Raise(new BatchEvent(BatchEventKind.Info, _ctx.Clock.Now,
            $"任务 {task.Id} 阶段使用 {combo.Account.DisplayName} + {combo.Model.Name} 执行", task.Id,
            combo.Account.DisplayName, combo.Model.Name));

        try
        {
            var result = await _ctx.Adapter.ExecuteAsync(request, ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                _ctx.TrackUsage?.Invoke(combo.Account.DisplayName, combo.Model.Name, result.Usage);
                return StageRunResult.Ok(result.Output ?? string.Empty, combo.Model.Name, combo.Account.DisplayName);
            }

            return StageRunResult.Fail(result.Failure ?? new AgentFailure(AgentFailureKind.TaskError, "未知错误"));
        }
        catch (OperationCanceledException)
        {
            return StageRunResult.CreateInterrupted();
        }
    }

    private AgentCombo? ResolveCombo(WorkflowStage stage, DateTimeOffset now)
    {
        // 阶段执行器覆盖：精确匹配模型（及可选账号）
        if (stage.Executor is { Model: { Length: > 0 } } ov)
        {
            foreach (var c in _ctx.Matrix.Ordered)
            {
                if (string.Equals(c.Model.Name, ov.Model, StringComparison.OrdinalIgnoreCase) &&
                    (ov.AccountId is null || string.Equals(c.Account.Id, ov.AccountId, StringComparison.OrdinalIgnoreCase)) &&
                    _ctx.Evaluator.Evaluate(c, now).Available)
                {
                    return c;
                }
            }
        }

        var selection = _ctx.Matrix.SelectNext(null, now, _ctx.Evaluator);
        return selection.Combo;
    }

    private AgentCombo? TryFallback()
    {
        var (account, fallback) = _ctx.Matrix.Accounts
            .Where(a => a.Enabled)
            .SelectMany(a => (a.FallbackModels ?? new List<FallbackModel>()).Select(fb => (Account: a, Fallback: fb)))
            .FirstOrDefault();
        if (fallback is null)
        {
            return null;
        }

        return new AgentCombo(account, new ModelCandidate
        {
            Name = fallback.Name,
            FreeTimeWindow = null,
            EstimatedUnitPrice = fallback.EstimatedUnitPrice
        });
    }

    private bool IsQuotaUnboundedWait(RestoreCandidate? candidate, DateTimeOffset now) =>
        _ctx.Options.WaitForQuotaReset
        && candidate is { WaitKind: WaitKind.QuotaReset }
        && _ctx.Matrix.AllUnavailablePurelyByQuota(now, _ctx.Evaluator);

    private async Task<bool> WaitForRestoreAsync(RestoreCandidate candidate, string? taskId, CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            var now = _ctx.Clock.Now;
            if (now >= candidate.RestoreAt)
            {
                return true;
            }

            if (_ctx.Matrix.SelectFirst(now, _ctx.Evaluator).Found)
            {
                return true;
            }

            var remaining = candidate.RestoreAt - now;
            var slice = remaining > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : remaining;
            await _ctx.Clock.DelayAsync(slice, ct).ConfigureAwait(false);
        }
    }

    private string ResolveWorkspace(TaskItem task)
    {
        var ws = task.Workspace ?? _ctx.Options.DefaultWorkspace;
        if (string.IsNullOrWhiteSpace(ws))
        {
            return Directory.GetCurrentDirectory();
        }

        var named = _ctx.Options.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, ws, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return named.Path;
        }

        return Path.IsPathRooted(ws) ? ws : Directory.GetCurrentDirectory();
    }

    private static void SeedTaskFields(WorkflowRenderContext ctx, TaskItem task)
    {
        ctx.SetTaskField("instruction", task.Instruction);
        ctx.SetTaskField("id", task.Id);
        if (task.TemplateVars is not null)
        {
            foreach (var (k, v) in task.TemplateVars.Values)
            {
                ctx.SetVar(k, v);
            }
        }
    }

    private static TaskStatus MapStageFailure(WorkflowSpec workflow, WorkflowStage stage) =>
        stage.OnGateFail == StageOnGateFail.MarkNeedsAttention ? TaskStatus.NeedsAttention
            : workflow.OnFailure == WorkflowOnFailure.Fail ? TaskStatus.Failed
            : TaskStatus.NeedsAttention;

    private static TaskStatus MapGateFail(WorkflowSpec workflow, WorkflowStage stage) =>
        stage.OnGateFail == StageOnGateFail.MarkNeedsAttention || workflow.OnFailure != WorkflowOnFailure.Fail
            ? TaskStatus.NeedsAttention
            : TaskStatus.Failed;

    private WorkflowOutcome Finalize(
        WorkflowSpec workflow, TaskItem task, List<StageResult> stageResults, TaskStatus status,
        string? result, string? error, string? model, string? account)
    {
        _ctx.OnTaskChanged?.Invoke(task);
        return new WorkflowOutcome
        {
            FinalStatus = status,
            Result = result,
            Error = error,
            StageResults = stageResults,
            ModelUsed = model,
            AccountUsed = account,
            ExpertUsed = _ctx.Options.Expert.ToString(),
            FallbackUsed = false
        };
    }

    private void Raise(BatchEvent ev)
    {
        _ctx.OnEvent?.Invoke(ev);
        _ctx.Logger.Write(ev);
    }

    private static string? Truncate(string? value, int max) =>
        TextTruncate.Truncate(value, max, "…（已截断）");
}

/// <summary>单阶段执行结果（WorkflowExecutor 内部）。</summary>
internal sealed record StageRunResult(
    bool Succeeded, bool Interrupted, string? Output, string? ModelUsed, string? AccountUsed, string? Error, AgentFailure? Failure)
{
    public static StageRunResult Ok(string output, string? model, string? account) =>
        new(true, false, output, model, account, null, null);

    public static StageRunResult Fail(AgentFailure failure) =>
        new(false, false, null, null, null, failure.Message, failure);

    public static StageRunResult Failed(string error) =>
        new(false, false, null, null, null, error, null);

    public static StageRunResult CreateInterrupted() =>
        new(false, true, null, null, null, "用户终止", null);
}
