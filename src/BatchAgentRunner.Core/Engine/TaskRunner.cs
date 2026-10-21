using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 单任务执行协作者（P1-1 拆分产物 ②，§3.4 <c>ITaskRunner</c>）：薄 facade。
/// P-03 收口后，状态机细节与「降级 / 冷却 / 兜底 / 免费时段」四类决策已下沉至
/// <see cref="TaskStepExecutor"/> 与 <see cref="DegradationPolicy"/> / <see cref="CooldownPolicy"/> /
/// <see cref="FallbackPolicy"/> / <see cref="FreeTimeWindowPolicy"/>，本类型仅负责
/// 「接收上下文 → 构造执行体 → 委托执行」，自身零状态机逻辑（见 ARCH §6.2 P-03 执行记录）。
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
    /// <summary>运行级指标采集器（ARCH P-10 / A-8）：埋点集中在状态机的决策点，不在渲染路径上。</summary>
    public RunMetrics Metrics { get; init; } = null!;

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

/// <summary>默认实现：薄 facade，状态机执行委托给 <see cref="TaskStepExecutor"/>（P-03 拆分产物 ②）。</summary>
public sealed class TaskRunner : ITaskRunner
{
    private readonly TaskStepExecutor _executor;

    public TaskRunner(TaskRunnerContext context) => _executor = new TaskStepExecutor(context);

    public Task<TaskOutcome> RunSafeAsync(TaskItem task, CancellationToken ct) => _executor.RunSafeAsync(task, ct);
}
