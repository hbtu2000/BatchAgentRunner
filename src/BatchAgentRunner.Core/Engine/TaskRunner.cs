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
    /// <summary>运行配置。</summary>
    public RunOptions Options { get; init; } = null!;
    /// <summary>CLI 适配器（单任务执行入口）。</summary>
    public IAgentCliAdapter Adapter { get; init; } = null!;
    /// <summary>降级矩阵提供者（配置变更时重建，懒解析防陈旧矩阵）。</summary>
    public Func<DegradationMatrix> MatrixProvider { get; init; } = null!;
    /// <summary>可用性评估器。</summary>
    public AvailabilityEvaluator Evaluator { get; init; } = null!;
    /// <summary>额度冷却注册表。</summary>
    public QuotaCooldownRegistry Cooldowns { get; init; } = null!;
    /// <summary>重试策略（次数 / 退避）。</summary>
    public RetryPolicy RetryPolicy { get; init; } = null!;
    /// <summary>时钟。</summary>
    public IClock Clock { get; init; } = null!;
    /// <summary>运行日志。</summary>
    public IRunLogger Logger { get; init; } = null!;
    /// <summary>凭证 Vault（null = 不启用凭证切换）。</summary>
    public ICredentialVault? Vault { get; init; }
    /// <summary>报告构建器（用量记账）。</summary>
    public IBatchReportBuilder Report { get; init; } = null!;
    /// <summary>通知派发器。</summary>
    public IBatchNotifier Notifier { get; init; } = null!;
    /// <summary>兜底计费。</summary>
    public IFallbackBilling Fallback { get; init; } = null!;
    /// <summary>Git 工作流（项目级任务的回写/PR）。</summary>
    public IGitWorkflow GitWorkflow { get; init; } = null!;
    /// <summary>文件系统端口。</summary>
    public IFileSystem FileSystem { get; init; } = null!;
    /// <summary>任务等待累计（秒；从任务时长中剔除暂停等待）。</summary>
    public Dictionary<string, double> TaskWaitSeconds { get; init; } = null!;
    /// <summary>统计锁（与引擎共享）。</summary>
    public object StatsLock { get; init; } = null!;
    /// <summary>工作区路径解析。</summary>
    public Func<string?, string?> ResolveWorkspace { get; init; } = null!;
    /// <summary>冷却持久化委托。</summary>
    public Func<Task> SaveCooldowns { get; init; } = null!;
    /// <summary>任务级落盘委托。</summary>
    public Func<CancellationToken, Task> Persist { get; init; } = null!;
    /// <summary>写入门进入委托（与落盘序列化互斥）。</summary>
    public Func<Task<IDisposable>> EnterWriteGate { get; init; } = null!;
    /// <summary>事件上报委托。</summary>
    public Action<BatchEvent> Raise { get; init; } = null!;
    /// <summary>批次状态设置委托。</summary>
    public Action<BatchStatus> SetStatus { get; init; } = null!;
    /// <summary>阻塞事件上报委托。</summary>
    public Action<string, string?> RaiseBlocked { get; init; } = null!;
    /// <summary>任务变更事件上报委托。</summary>
    public Action<TaskItem> RaiseTaskChanged { get; init; } = null!;
    /// <summary>当前任务 id 设置委托。</summary>
    public Action<string?> SetCurrentTaskId { get; init; } = null!;
    /// <summary>等待恢复委托（转 WaitCoordinator）。</summary>
    public Func<RestoreCandidate, string?, CancellationToken, Task<bool>> WaitForRestore { get; init; } = null!;
    /// <summary>工作流任务执行委托（转 BatchLifecycle）。</summary>
    public Func<TaskItem, CancellationToken, Task<TaskOutcome>> ExecuteWorkflow { get; init; } = null!;
    /// <summary>运行级指标采集器（ARCH P-10 / A-8）：埋点集中在状态机的决策点，不在渲染路径上。</summary>
    public RunMetrics Metrics { get; init; } = null!;

    /// <summary>待派发任务计数（并发派发节奏用）。</summary>
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

    /// <summary>用执行上下文装配任务执行器（状态机执行委托给 <see cref="TaskStepExecutor"/>）。</summary>
    /// <param name="context">任务执行上下文（组合 / 凭证 / 事件 / 报告等协作者的装配源）。</param>
    public TaskRunner(TaskRunnerContext context) => _executor = new TaskStepExecutor(context);

    /// <summary>按单任务状态机安全执行任务（异常被收敛为 <see cref="TaskOutcome"/>，不向上抛）。</summary>
    /// <param name="task">待执行的任务（读取其指令、工作区与依赖声明）。</param>
    /// <param name="ct">批次取消令牌，触发后按安全中断收尾。</param>
    public Task<TaskOutcome> RunSafeAsync(TaskItem task, CancellationToken ct) => _executor.RunSafeAsync(task, ct);
}
