using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 等待协调者（P-01 / ARCH §3.3 WaitCoordinator）：从 <see cref="BatchEngine"/> 抽出
/// 「等待循环 + 唤醒（<c>_wake</c>）+ 可用性轮询编排」全部逻辑，使 facade 退化为
/// 「按序编排协作者 + 维护批次级状态」。该协作者独占等待门（<c>_waitGate</c>）、
/// 唤醒源（<c>_wake</c>）、取消标记（<c>_waitingCancelled</c>），对外暴露
/// <see cref="WaitForRestoreAsync"/> / <see cref="CancelWaiting"/> / <see cref="SignalWake"/>，
/// facade 仅委托调用。
/// </summary>
/// <remarks>
/// 公开行为（<see cref="IBatchEngine"/> 的 waiting 语义、唤醒时机、可用性提前退出）与抽取前逐字节等价：
/// 仅把状态与循环体从引擎搬迁至此，无业务语义改动。所有对外依赖经 <see cref="WaitCoordinatorContext"/>
/// 注入，与 <see cref="TaskDispatcherContext"/> / <see cref="TaskRunnerContext"/> 同范式，便于单测替换。
/// </remarks>
public sealed class WaitCoordinator : IDisposable
{
    private readonly WaitCoordinatorContext _ctx;
    private readonly SemaphoreSlim _waitGate = new(1, 1);
    private TaskCompletionSource? _wake;
    private bool _waitingCancelled;

    /// <summary>用上下文能力集合构造等待协调者。</summary>
    /// <param name="context">等待协调所需的引擎能力集合。</param>
    public WaitCoordinator(WaitCoordinatorContext context) => _ctx = context;

    /// <summary>
    /// 等待可用性恢复（REQ-003b / REQ-004c）。返回 <c>false</c> 表示被取消（转入 blocked）。
    /// 循环内每轮重新评估当前矩阵可用性：配置变更/补充额度使某组合可用即提前退出等待；
    /// 否则按 2 秒切片睡眠直到 <see cref="RestoreCandidate.RestoreAt"/> 或外部唤醒。
    /// </summary>
    public async Task<bool> WaitForRestoreAsync(RestoreCandidate candidate, string? taskId, CancellationToken ct)
    {
        await _waitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 先清取消标记再置 Waiting 状态：避免取消请求落在「已置状态、未清标记」的窗口内被误清
            // （界面轮询到 Waiting 后立刻点取消，若此时标记尚未清零，取消会丢失导致批处理跑完而非转阻塞）
            _waitingCancelled = false;
            _ctx.SetStatus(BatchStatus.Waiting);
            var waitStart = _ctx.Clock.Now;

            _ctx.Raise(new BatchEvent(BatchEventKind.BatchWaiting, _ctx.Clock.Now,
                DescribeWaiting(candidate), _ctx.CurrentTaskIdProvider())
            {
                BatchStatus = BatchStatus.Waiting,
                ResumeAt = candidate.RestoreAt,
                WaitKind = candidate.WaitKind
            });
            _ctx.Logger.WriteBatchWaiting(candidate.RestoreAt, candidate.Combo.Model.Name, candidate.WaitKind);

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

                var now = _ctx.Clock.Now;
                if (now >= candidate.RestoreAt)
                {
                    break;
                }

                // 配置变更 / 补充额度后即时重评，已可用则提前退出
                if (_ctx.MatrixProvider().SelectFirst(now, _ctx.Evaluator).Found)
                {
                    _ctx.Raise(new BatchEvent(BatchEventKind.BatchResumed, now, "检测到可用组合，提前结束等待继续执行"));
                    _ctx.Logger.WriteBatchResumed("配置变更或额度补充，提前退出等待");
                    break;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _wake = tcs;
                var remaining = candidate.RestoreAt - now;
                var slice = remaining > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : remaining;
                var delay = _ctx.Clock.DelayAsync(slice, ct);
                await Task.WhenAny(delay, tcs.Task).ConfigureAwait(false);
            }

            var elapsed = (_ctx.Clock.Now - waitStart).TotalSeconds;
            _ctx.Report.AddWaitSeconds(candidate.WaitKind, elapsed);

            // 单任务级暂停累计（用于从任务时长中剔除，DESIGN-额度耗尽暂停续跑）
            if (!string.IsNullOrEmpty(taskId))
            {
                lock (_ctx.StatsLock)
                {
                    _ctx.TaskWaitSeconds.TryGetValue(taskId!, out var prior);
                    _ctx.TaskWaitSeconds[taskId!] = prior + elapsed;
                }
            }

            _ctx.SetStatus(BatchStatus.Running);
            _ctx.Raise(new BatchEvent(BatchEventKind.BatchResumed, _ctx.Clock.Now,
                $"已恢复执行（暂停 {elapsed / 60:0.0} 分钟，不计入任务时长）")
            { BatchStatus = BatchStatus.Running });
            _ctx.Logger.WriteBatchResumed("到达恢复时间点");
            return true;
        }
        finally
        {
            _waitGate.Release();
        }
    }

    /// <summary>取消 waiting（REQ-003b：取消后转入 blocked）。置取消标记并唤醒等待循环。</summary>
    public void CancelWaiting()
    {
        _waitingCancelled = true;
        _wake?.TrySetResult();
    }

    /// <summary>
    /// 唤醒等待循环（配置变更/补充额度/手动重置冷却后即时重评；REQ-003b / REQ-004c）。
    /// 不做取消——等待循环重新评估可用性，已可用则提前退出，否则回到睡眠。
    /// </summary>
    public void SignalWake() => _wake?.TrySetResult();

    /// <summary>释放等待门（CA1001：本协调者持有 <see cref="SemaphoreSlim"/> 门，由引擎在释放时一并释放）。</summary>
    public void Dispose() => _waitGate.Dispose();

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
}

/// <summary>等待协调所需的引擎能力集合（沿用 <see cref="TaskRunnerContext"/> 的上下文注入范式）。</summary>
public sealed class WaitCoordinatorContext
{
    /// <summary>时钟（等待切片与时间推进的单一事实来源）。</summary>
    public IClock Clock { get; init; } = null!;

    /// <summary>组合可用性评估器（等待循环每轮重评）。</summary>
    public AvailabilityEvaluator Evaluator { get; init; } = null!;

    /// <summary>降级矩阵延迟提供者（重评时现取，避免陈旧矩阵）。</summary>
    public Func<DegradationMatrix> MatrixProvider { get; init; } = null!;

    /// <summary>报告构建器（累计等待时长）。</summary>
    public IBatchReportBuilder Report { get; init; } = null!;

    /// <summary>运行日志（等待 / 恢复事件落日志）。</summary>
    public IRunLogger Logger { get; init; } = null!;

    /// <summary>单任务级暂停等待累计（从任务时长中剔除，key 不区分大小写）。</summary>
    public Dictionary<string, double> TaskWaitSeconds { get; init; } = null!;

    /// <summary>保护 <see cref="TaskWaitSeconds"/> 累计的锁。</summary>
    public object StatsLock { get; init; } = null!;

    /// <summary>批处理事件广播委托。</summary>
    public Action<BatchEvent> Raise { get; init; } = null!;

    /// <summary>批处理状态设置委托（Waiting / Running）。</summary>
    public Action<BatchStatus> SetStatus { get; init; } = null!;

    /// <summary>当前任务 ID 提供者（事件携带）。</summary>
    public Func<string?> CurrentTaskIdProvider { get; init; } = null!;
}
