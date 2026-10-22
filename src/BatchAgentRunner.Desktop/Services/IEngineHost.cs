using System.ComponentModel;
using System.Threading;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Desktop.Services;

/// <summary>
/// 引擎宿主公共契约（阶段 2 P1-4/P1-5）。
/// </summary>
/// <remarks>
/// 作为 Desktop 内部稳定的对外契约：所有页面 ViewModel 仅依赖本接口，不直接依赖具体
/// <see cref="EngineHost"/>，从而解除「UI 直引具体宿主类型」的耦合（P1-5）。类型落在 Desktop 内，
/// 但只暴露 Core 抽象与 Desktop 自身的 Vault 宿主，不泄漏 Infrastructure/Agents 具体实现。
/// </remarks>
public interface IEngineHost : IDisposable
{
    /// <summary>当前运行配置。</summary>
    RunOptions Options { get; }

    /// <summary>当前引擎实例（可能为 null：尚未初始化）。</summary>
    IBatchEngine? Engine { get; }

    /// <summary>批次状态。</summary>
    BatchStatus Status { get; }

    /// <summary>是否运行中。</summary>
    bool IsRunning { get; }

    /// <summary>运行日志文件路径。</summary>
    string? LogFilePath { get; }

    /// <summary>最近一次批次报告。</summary>
    BatchReport? LastReport { get; }

    /// <summary>最近一次报告路径。</summary>
    string? LastReportPath { get; }

    /// <summary>最近一次每日积分领取报告路径。</summary>
    string? LastCreditsClaimReportPath { get; }

    /// <summary>配置目录。</summary>
    string ConfigDirectory { get; }

    /// <summary>凭证 Vault 宿主。</summary>
    CredentialVaultHost VaultHost { get; }

    /// <summary>工作流模板库（P1-4/P1-5：模板库页只经此接口访问，实现由组合根装配）。</summary>
    IWorkflowTemplateStore TemplateStore { get; }

    /// <summary>引擎事件（已在 UI 线程广播）。</summary>
    event EventHandler<BatchEvent>? EventRaised;

    /// <summary>任务集合变更（已在 UI 线程广播）。</summary>
    event EventHandler? TasksChanged;

    /// <summary>每日积分领取进度（已在 UI 线程广播）。</summary>
    event EventHandler<CreditsClaimProgress>? CreditsClaimProgressRaised;

    /// <summary>属性变更通知（EngineHost 继承 ObservableObject）。</summary>
    event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>加载配置并初始化引擎（应用启动时调用）。</summary>
    Task InitializeAsync();
    /// <summary>配置变更后重建引擎，使账号 / 模型 / 时段即时生效。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RebuildEngineAsync(CancellationToken cancellationToken = default);
    /// <summary>把当前配置写回配置存储。</summary>
    Task SaveConfigAsync();
    /// <summary>按账号实时拉取可用模型列表，失败时返回空列表。</summary>
    /// <param name="account">目标账号；null 表示按默认账号视角查询。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(AccountConfig? account = null, CancellationToken cancellationToken = default);
    /// <summary>从任务文件加载任务列表。</summary>
    Task<IReadOnlyList<TaskItem>> LoadTasksAsync();
    /// <summary>为全部启用账号领取每日积分，并写入台账与报告。</summary>
    /// <param name="force">true 表示忽略当日已领取记录强制再领。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CreditsClaimSummary> ClaimDailyCreditsAsync(bool force = true, CancellationToken cancellationToken = default);
    /// <summary>执行批前预检并返回预检报告。</summary>
    Task<PreflightReport> PreflightAsync();
    /// <summary>启动批处理并返回批次报告。</summary>
    /// <param name="waitIfUnavailable">当前不可运行时是否等待可用（false 则立即返回）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<BatchReport> StartAsync(bool waitIfUnavailable, CancellationToken cancellationToken = default);
    /// <summary>请求终止批处理。</summary>
    /// <param name="waitCurrentTask">true 表示等当前任务结束后再停。</param>
    Task TerminateAsync(bool waitCurrentTask = false);
    /// <summary>取消等待中的批次。</summary>
    void CancelWaiting();
    /// <summary>通知引擎配置已变更。</summary>
    void NotifyConfigurationChanged();
    /// <summary>清空全部账号的冷却记录。</summary>
    void ResetAllCooldowns();
}
