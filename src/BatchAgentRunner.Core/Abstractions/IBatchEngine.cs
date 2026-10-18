using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// 批处理调度引擎的公共契约（对应 <see cref="BatchAgentRunner.Core.Engine.BatchEngine"/>）。
/// </summary>
/// <remarks>
/// 阶段 2（P1-3）提取，作为 strangler fig 的目标接口。阶段 2 的护栏要求：
/// <b>新代码一律依赖本接口，不得直接引用 <c>BatchEngine</c> 内部成员</b>（由 §4.3 架构断言强制）。
/// 接口面与 <c>BatchEngine</c> 公共面严格一致；外壳删除前，引擎实例既可当具体类型也可当本接口使用。
/// </remarks>
public interface IBatchEngine : IDisposable
{
    /// <summary>不可变运行配置。</summary>
    RunOptions Options { get; }

    /// <summary>实时任务列表（引擎内部可变，订阅 <see cref="TasksChanged"/> 感知变更）。</summary>
    IReadOnlyList<TaskItem> Tasks { get; }

    /// <summary>当前降级可用性矩阵。</summary>
    DegradationMatrix Matrix { get; }

    /// <summary>额度/可用性冷却注册表。</summary>
    QuotaCooldownRegistry Cooldowns { get; }

    /// <summary>引擎批次状态（Idle→Running→…）。</summary>
    BatchStatus Status { get; }

    /// <summary>加载时跳过的已完成任务计数。</summary>
    int SkippedDoneCount { get; }

    /// <summary>完成时的最终批次报告（完成时设置）。</summary>
    BatchReport? Report { get; }

    /// <summary>当前执行任务 id。</summary>
    string? CurrentTaskId { get; }

    /// <summary>引擎事件（界面与日志订阅）。</summary>
    event EventHandler<BatchEvent>? EventRaised;

    /// <summary>任务集合/任务项发生变化（界面整体刷新）。</summary>
    event EventHandler? TasksChanged;

    /// <summary>加载冷却、构建内部协作者/矩阵。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>加载任务、统计已完成跳过。</summary>
    Task<IReadOnlyList<TaskItem>> LoadTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>启动前预检（文件/工作区/依赖 DAG/额度）。</summary>
    Task<PreflightReport> PreflightAsync(CancellationToken cancellationToken = default);

    /// <summary>主入口：构建队列、派发、处理 blocked/cycle，返回批次报告。</summary>
    Task<BatchReport> StartAsync(bool waitIfUnavailable = false, CancellationToken cancellationToken = default);

    /// <summary>请求优雅停止；可选等待在飞任务。</summary>
    Task TerminateAsync(bool waitCurrentTask = false);

    /// <summary>取消进行中的可用性等待。</summary>
    void CancelWaiting();

    /// <summary>配置变更信号：重建矩阵、广播 ConfigChanged 事件。</summary>
    void NotifyConfigurationChanged();

    /// <summary>清除单个账号/模型冷却条目。</summary>
    void ResetCooldown(string accountId, string modelName);

    /// <summary>清除全部冷却并持久化。</summary>
    void ResetAllCooldowns();

    /// <summary>解析任务工作区路径（回退当前目录/绝对路径检查）。</summary>
    string? ResolveWorkspacePath(string? workspace);
}
