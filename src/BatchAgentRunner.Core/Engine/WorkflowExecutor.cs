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
    /// <summary>运行配置。</summary>
    public required RunOptions Options { get; init; }
    /// <summary>CLI 适配器。</summary>
    public required IAgentCliAdapter Adapter { get; init; }
    /// <summary>降级矩阵（构建期快照；配置变更后由引擎重建并重注）。</summary>
    public required DegradationMatrix Matrix { get; init; }
    /// <summary>可用性评估器。</summary>
    public required AvailabilityEvaluator Evaluator { get; init; }
    /// <summary>额度冷却注册表。</summary>
    public required QuotaCooldownRegistry Cooldowns { get; init; }
    /// <summary>错误分类器。</summary>
    public required QuotaErrorClassifier Classifier { get; init; }
    /// <summary>时钟。</summary>
    public required IClock Clock { get; init; }
    /// <summary>运行日志。</summary>
    public required IRunLogger Logger { get; init; }
    /// <summary>质量门禁评估器。</summary>
    public required GateEvaluator GateEvaluator { get; init; }
    /// <summary>事件上报回调（可选）。</summary>
    public Action<BatchEvent>? OnEvent { get; init; }
    /// <summary>任务变更回调（可选）。</summary>
    public Action<TaskItem>? OnTaskChanged { get; init; }
    /// <summary>落盘委托（可选）。</summary>
    public Func<Task>? Persist { get; init; }
    /// <summary>用量记账委托（可选）。</summary>
    public Func<string, string, AgentUsage?, Task>? TrackUsage { get; init; }
    /// <summary>凭证确保委托（可选；注入了才做账号级 Live 凭证切换）。</summary>
    public Func<TaskItem, AgentCombo, CancellationToken, Task<bool>>? EnsureCredential { get; init; }

    /// <summary>
    /// 等待恢复能力（P2-1 偿还：原 <c>WorkflowExecutor</c> 自带一份时钟轮询等待，与引擎的
    /// <c>WaitForRestoreAsync</c> 语义分叉——工作流路径无取消唤醒、无暂停时长统计。
    /// 现由引擎注入同一实现（单任务路径同款），等待语义全仓单一；未注入（测试直构）时判定为「无法等待」。
    /// </summary>
    public Func<RestoreCandidate, string?, CancellationToken, Task<bool>>? WaitForRestore { get; init; }

    /// <summary>
    /// 工作空间解析（P2-1/P2-2 偿还：原 <c>WorkflowExecutor</c> 自带一份解析，无法解析时回落 CWD，
    /// 与引擎 <c>ResolveWorkspacePath</c>「回落 null 由调用方决定」同名不同义）。现由引擎注入同一实现；
    /// 未注入时回落 <see cref="Directory.GetCurrentDirectory"/>（与原语义一致）。
    /// </summary>
    public Func<string?, string?>? ResolveWorkspacePath { get; init; }

    /// <summary>阶段级门禁修订循环默认上限（被阶段自身 MaxLoops 覆盖）。</summary>
    public int DefaultMaxGateLoops { get; init; } = 3;
}

/// <summary>工作流执行结果（交回引擎落盘）。</summary>
public sealed class WorkflowOutcome
{
    /// <summary>任务级终态（Done / Failed / Interrupted / NeedsAttention）。</summary>
    public TaskStatus FinalStatus { get; init; }
    /// <summary>结果正文（已按阶段产出汇总与截断）。</summary>
    public string? Result { get; init; }
    /// <summary>失败/中断错误摘要。</summary>
    public string? Error { get; init; }
    /// <summary>各阶段结果（按执行序）。</summary>
    public List<StageResult> StageResults { get; init; } = new();
    /// <summary>最后执行阶段的模型名。</summary>
    public string? ModelUsed { get; init; }
    /// <summary>最后执行阶段的账号展示名。</summary>
    public string? AccountUsed { get; init; }
    /// <summary>专家标识（配置级）。</summary>
    public string? ExpertUsed { get; init; }
    /// <summary>是否发生过兜底执行。</summary>
    public bool FallbackUsed { get; init; }
    /// <summary>兜底累计费用。</summary>
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

    /// <summary>构造工作流执行器（上下文由 <see cref="BatchLifecycle"/> 装配）。</summary>
    /// <param name="ctx">工作流执行上下文（选项 / 时钟 / 指标 / 报告等协作者）。</param>
    public WorkflowExecutor(WorkflowEngineContext ctx) => _ctx = ctx;

    /// <summary>执行工作流任务：按阶段顺序驱动 + 门禁修订循环，返回交回引擎落盘的终态结果。</summary>
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
        QuotaWaitPolicy.IsUnboundedQuotaWait(_ctx.Options, _ctx.Matrix, _ctx.Evaluator, candidate, now);

    /// <summary>
    /// 等待恢复（P2-1 偿还）：委托引擎注入的等待逻辑（含取消唤醒 / 暂停时长统计 / 批量事件），
    /// 与原私有时钟轮询副本的行为分叉随本删除消除；未注入时判定无法等待（按中断处理）。
    /// </summary>
    private Task<bool> WaitForRestoreAsync(RestoreCandidate candidate, string? taskId, CancellationToken ct) =>
        _ctx.WaitForRestore is { } wait
            ? wait(candidate, taskId, ct)
            : Task.FromResult(false);

    private string ResolveWorkspace(TaskItem task) =>
        _ctx.ResolveWorkspacePath?.Invoke(task.Workspace ?? _ctx.Options.DefaultWorkspace)
        ?? Directory.GetCurrentDirectory();

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
    /// <summary>构造阶段成功结果（记录产出摘要与本次所用模型/账号）。</summary>
    /// <param name="output">阶段输出摘要（后续阶段渲染与报告展示的数据源）。</param>
    /// <param name="model">本阶段实际使用的模型名，未知为 null。</param>
    /// <param name="account">本阶段实际使用的账号显示名，未知为 null。</param>
    public static StageRunResult Ok(string output, string? model, string? account) =>
        new(true, false, output, model, account, null, null);

    /// <summary>构造失败结果（携带失败分类，供上层判定重试或降级）。</summary>
    /// <param name="failure">失败分类与原始错误信息。</param>
    public static StageRunResult Fail(AgentFailure failure) =>
        new(false, false, null, null, null, failure.Message, failure);

    /// <summary>构造失败结果（仅带错误文案，无失败分类）。</summary>
    /// <param name="error">面向用户的失败原因文案。</param>
    public static StageRunResult Failed(string error) =>
        new(false, false, null, null, null, error, null);

    /// <summary>构造中断结果（用户终止，无输出且不计失败）。</summary>
    public static StageRunResult CreateInterrupted() =>
        new(false, true, null, null, null, "用户终止", null);
}
