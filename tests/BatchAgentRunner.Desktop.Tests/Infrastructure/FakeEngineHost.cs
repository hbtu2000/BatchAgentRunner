using System.ComponentModel;
using System.IO;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Services;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Desktop.Tests.Infrastructure;

/// <summary>
/// P1-19：<see cref="IEngineHost"/> 测试替身。
/// 内部构造真实（sealed）<see cref="CredentialVaultHost"/>——但用 3 个微型桩替换其 Vault/Guard/Launcher，
/// 避免任何真实文件/进程副作用。
/// <see cref="Options"/> 为单一可变实例：<c>ConfigViewModel.SaveAsync</c> 原地改写它，测试可直接断言往返。
/// </summary>
public sealed class FakeEngineHost : IEngineHost
{
    private readonly CredentialVaultHost _vaultHost;
    private int _saveCount;
    private int _rebuildCount;
    private IReadOnlyList<ModelCatalogEntry> _fetchModelsResult = Array.Empty<ModelCatalogEntry>();
    private EventHandler<BatchEvent>? _eventRaised;
    private EventHandler? _tasksChanged;
    private EventHandler<CreditsClaimProgress>? _creditsClaimProgressRaised;
    private PropertyChangedEventHandler? _propertyChanged;

    public FakeEngineHost(RunOptions? options = null)
    {
        var registry = new CredentialLocationRegistry();
        var vault = new FakeCredentialVault(this);
        var guard = new FakeCliProcessGuard();
        var launcher = new FakeCliLoginLauncher();
        _vaultHost = new CredentialVaultHost(
            vault,
            new GuidedLoginOrchestrator(vault, guard, launcher, registry: registry),
            registry);
        Options = options ?? new RunOptions();
    }

    /// <summary>单一可变配置实例（SaveAsync 原地改写，便于断言往返）。</summary>
    public RunOptions Options { get; }

    public IBatchEngine? Engine => null;
    public BatchStatus Status => BatchStatus.Idle;
    public bool IsRunning => false;
    public string? LogFilePath => null;
    public BatchReport? LastReport { get; set; }
    public string? LastReportPath { get; set; }
    public string? LastCreditsClaimReportPath => null;
    public string ConfigDirectory => Path.GetTempPath();
    public CredentialVaultHost VaultHost => _vaultHost;

    /// <summary>模板库零 IO 桩（P1-4/P1-5 收口：VM 不再自行 new FileWorkflowTemplateStore，替身经接口注入）。</summary>
    public IWorkflowTemplateStore TemplateStore { get; } = new FakeTemplateStore();

    public event EventHandler<BatchEvent>? EventRaised
    {
        add { _eventRaised += value; }
        remove { _eventRaised -= value; }
    }
    public event EventHandler? TasksChanged
    {
        add { _tasksChanged += value; }
        remove { _tasksChanged -= value; }
    }
    public event EventHandler<CreditsClaimProgress>? CreditsClaimProgressRaised
    {
        add { _creditsClaimProgressRaised += value; }
        remove { _creditsClaimProgressRaised -= value; }
    }
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { _propertyChanged += value; }
        remove { _propertyChanged -= value; }
    }

    /// <summary>P-06：按引擎广播语义触发 <see cref="EventRaised"/>（调用方负责在 UI 线程触发，与真实宿主一致）。</summary>
    public void RaiseEvent(BatchEvent ev) => _eventRaised?.Invoke(this, ev);

    /// <summary>P-06：触发 <see cref="TasksChanged"/>（调用方负责在 UI 线程触发，与真实宿主一致）。</summary>
    public void RaiseTasksChanged() => _tasksChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>P-06：触发 <see cref="CreditsClaimProgressRaised"/>（Login 页领取进度条契约）。</summary>
    public void RaiseCreditsClaimProgress(CreditsClaimProgress progress) =>
        _creditsClaimProgressRaised?.Invoke(this, progress);

    /// <summary>探针：SaveConfigAsync 被调用次数（断言 SaveAsync 确实落盘）。</summary>
    public int SaveConfigAsyncCallCount => _saveCount;

    /// <summary>探针：RebuildEngineAsync 被调用次数。</summary>
    public int RebuildEngineCallCount => _rebuildCount;

    /// <summary>探针：设置 FetchModelsAsync 返回的模型目录（默认空清单，保持零 IO 桩语义）。</summary>
    public void SetFetchModelsResult(IReadOnlyList<ModelCatalogEntry> entries) => _fetchModelsResult = entries;

    /// <summary>P-06：设置 ClaimDailyCreditsAsync 返回的汇总（默认空汇总，走「没有可领取的账号」分支）。</summary>
    public CreditsClaimSummary? ClaimResult { get; set; }

    /// <summary>P-06：设置后 Vault 的 Switch/DeleteSnapshot 操作抛出该异常（错误翻译契约）。</summary>
    public Exception? VaultFailure { get; set; }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task RebuildEngineAsync(CancellationToken cancellationToken = default)
    {
        _rebuildCount++;
        return Task.CompletedTask;
    }
    public Task SaveConfigAsync() { _saveCount++; return Task.CompletedTask; }

    public Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(AccountConfig? account = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_fetchModelsResult);

    public Task<IReadOnlyList<TaskItem>> LoadTasksAsync() =>
        Task.FromResult<IReadOnlyList<TaskItem>>(Array.Empty<TaskItem>());

    public Task<CreditsClaimSummary> ClaimDailyCreditsAsync(bool force = true,
        CancellationToken cancellationToken = default) => Task.FromResult(ClaimResult ?? new CreditsClaimSummary());

    public Task<PreflightReport> PreflightAsync() => Task.FromResult(new PreflightReport());

    public Task<BatchReport> StartAsync(bool waitIfUnavailable, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BatchReport());

    public Task TerminateAsync(bool waitCurrentTask = false) => Task.CompletedTask;
    public void CancelWaiting() { }
    public void NotifyConfigurationChanged() { }
    public void ResetAllCooldowns() { }
    public void Dispose() { }

    // ---------------- 微型桩（仅满足 CredentialVaultHost 构造，无副作用） ----------------

    /// <summary>模板库桩：空清单 / 空保存，保证 VM 测试全程零文件 IO。</summary>
    private sealed class FakeTemplateStore : IWorkflowTemplateStore
    {
        public Task<IReadOnlyList<TemplateSpec>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TemplateSpec>>(Array.Empty<TemplateSpec>());

        public Task<TemplateSpec?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<TemplateSpec?>(null);

        public Task SaveUserTemplateAsync(TemplateSpec spec, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeCredentialVault : ICredentialVault
    {
        private readonly FakeEngineHost _owner;

        public FakeCredentialVault(FakeEngineHost owner) => _owner = owner;

        public string? CurrentLiveAccount(AgentKind cli) => null;
        public bool SnapshotExists(AgentKind cli, string accountId) => false;
        public CredentialSnapshotInfo? GetSnapshot(AgentKind cli, string accountId) => null;
        public Task<CredentialSnapshotInfo> BackupFromLiveAsync(AgentKind cli, string accountId,
            string? displayName = null, string? site = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(default(CredentialSnapshotInfo)!);
        public Task SwitchToAsync(AgentKind cli, string accountId, string reason,
            CancellationToken cancellationToken = default)
        {
            if (_owner.VaultFailure is { } failure)
            {
                throw failure;
            }

            return Task.CompletedTask;
        }
        public Task MarkVerifiedAsync(AgentKind cli, string accountId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSnapshotAsync(AgentKind cli, string accountId,
            CancellationToken cancellationToken = default)
        {
            if (_owner.VaultFailure is { } failure)
            {
                throw failure;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeCliProcessGuard : ICliProcessGuard
    {
        public bool IsCliRunning(AgentKind cli) => false;
    }

    private sealed class FakeCliLoginLauncher : ICliLoginLauncher
    {
        public Task LaunchAsync(AgentKind cli, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
