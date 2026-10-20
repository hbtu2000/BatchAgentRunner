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

    Task InitializeAsync();
    Task RebuildEngineAsync(CancellationToken cancellationToken = default);
    Task SaveConfigAsync();
    Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(AccountConfig? account = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> LoadTasksAsync();
    Task<CreditsClaimSummary> ClaimDailyCreditsAsync(bool force = true, CancellationToken cancellationToken = default);
    Task<PreflightReport> PreflightAsync();
    Task<BatchReport> StartAsync(bool waitIfUnavailable, CancellationToken cancellationToken = default);
    Task TerminateAsync(bool waitCurrentTask = false);
    void CancelWaiting();
    void NotifyConfigurationChanged();
    void ResetAllCooldowns();
}
