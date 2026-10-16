using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.CredentialVault;
using BatchAgentRunner.Infrastructure.Http;
using BatchAgentRunner.Infrastructure.Logging;
using BatchAgentRunner.Infrastructure.Persistence;
using BatchAgentRunner.Infrastructure.Reporting;
using BatchAgentRunner.Infrastructure.Platform;
using BatchAgentRunner.Infrastructure.TaskFiles;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace BatchAgentRunner.Desktop.Services;

/// <summary>
/// 引擎宿主：组装配置 / 适配器 / 存储 / 日志，并把引擎事件广播给界面。
/// 界面只通过本类与引擎交互，保证「引擎与 UI 解耦、UI 线程安全」。
/// </summary>
public sealed class EngineHost : ObservableObject
{
    private readonly IConfigStore _configStore = new JsonConfigStore();
    private readonly ICooldownStore _cooldownStore = new JsonCooldownStore();
    private readonly ITaskFileStore _taskFileStore = new TaskFileStore();
    private readonly IReportWriter _reportWriter = new ReportWriter();
    private readonly IShutdownService _shutdownService = new ShutdownService();

    /// <summary>凭证 Vault 宿主（DESIGN-账号凭证切换-v1.0）：Vault 本体随引擎生命周期复用。</summary>
    public CredentialVaultHost VaultHost { get; } = CreateVaultHost();

    private BatchEngine? _engine;
    private FileRunLogger? _logger;
    private BatchStatus _status = BatchStatus.Idle;
    private bool _running;

    public RunOptions Options { get; private set; } = JsonConfigStore.CreateSample();

    public BatchEngine? Engine => _engine;

    public BatchStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsRunning
    {
        get => _running;
        private set => SetProperty(ref _running, value);
    }

    public string? LogFilePath => _logger?.LogFilePath;

    public BatchReport? LastReport { get; private set; }

    public string? LastReportPath { get; private set; }

    /// <summary>最近一次「领取每日积分」报告的 Markdown 路径（本次会话内）。</summary>
    public string? LastCreditsClaimReportPath { get; private set; }

    /// <summary>引擎事件（已确保不跨线程直接触达 UI 集合，订阅方自行切线程）。</summary>
    public event EventHandler<BatchEvent>? EventRaised;

    public event EventHandler? TasksChanged;

    /// <summary>每日积分领取进度（订阅方自行切线程）。</summary>
    public event EventHandler<CreditsClaimProgress>? CreditsClaimProgressRaised;

    public string ConfigDirectory => _configStore.ConfigDirectory;

    public async Task InitializeAsync()
    {
        Options = await _configStore.LoadAsync().ConfigureAwait(false);
        _engine = CreateEngine();
        await _engine.InitializeAsync().ConfigureAwait(false);
        Status = _engine.Status;
    }

    /// <summary>
    /// 默认 Vault 装配：DPAPI 静态加密 + 真实进程守卫；Mock 适配器把 Live 指到工具私有目录，
    /// 保证「MockAdapter 模拟同一套 Vault 流程，全链路可先行联调」（DESIGN §7）。
    /// </summary>
    private static CredentialVaultHost CreateVaultHost()
    {
        var mockLiveRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BatchAgentRunner", "mock-live");
        CredentialLocationRegistry.SetLiveRootOverride(AgentKind.Mock, mockLiveRoot);
        return CredentialVaultHost.CreateDefault();
    }

    private BatchEngine CreateEngine()
    {
        _logger?.Dispose();
        _logger = new FileRunLogger(Options.TaskFilePath);
        var adapter = AgentAdapterFactory.Create(Options, msg => _logger.Write("INFO", msg));
        if (adapter is MockAdapter mock)
        {
            // Mock 的「Live 凭证文件」与 Vault 共用同一位置，保活回写/切换全链路可用 Mock 联调
            mock.MockLiveCredentialPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BatchAgentRunner", "mock-live", ".credentials.json");
        }

        var engine = new BatchEngine(Options, adapter, _taskFileStore, _cooldownStore, _logger,
            credentialVault: VaultHost.Vault);
        engine.EventRaised += (_, ev) =>
        {
            if (ev.BatchStatus.HasValue)
            {
                Status = ev.BatchStatus.Value;
                IsRunning = ev.BatchStatus.Value is BatchStatus.Running or BatchStatus.Waiting;
            }

            EventRaised?.Invoke(this, ev);
        };
        engine.TasksChanged += (_, _) => TasksChanged?.Invoke(this, EventArgs.Empty);
        return engine;
    }

    /// <summary>配置变更后重建引擎（账号/模型/时段等即时生效）。</summary>
    public void RebuildEngine()
    {
        _engine = CreateEngine();
        _engine.InitializeAsync().GetAwaiter().GetResult();
        Status = _engine.Status;
        TasksChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveConfigAsync()
    {
        await _configStore.SaveAsync(Options).ConfigureAwait(false);
    }

    /// <summary>
    /// 实时读取某账号可用的模型列表（配置页「拉取可用模型」用）。
    /// 底层按当前执行工具创建适配器并以该账号的凭据环境运行 CLI，因此结果是「按账号视角」的可用模型。
    /// 读取失败或当前 CLI 不支持时返回空列表，调用方降级为手填。Mock 返回样例清单供联调。
    /// </summary>
    /// <summary>拉取模型目录（模型名 + 官方倍率）。倍率来自官方产品配置缓存，拉不到时倍率为 null。</summary>
    public async Task<IReadOnlyList<ModelCatalogEntry>> FetchModelsAsync(AccountConfig? account = null,
        CancellationToken cancellationToken = default)
    {
        var adapter = AgentAdapterFactory.Create(Options);
        return await adapter.ListModelCatalogAsync(account ?? new AccountConfig(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BatchAgentRunner.Core.Domain.TaskItem>> LoadTasksAsync()
    {
        var engine = _engine ??= CreateEngine();
        var tasks = await engine.LoadTasksAsync().ConfigureAwait(false);
        TasksChanged?.Invoke(this, EventArgs.Empty);
        return tasks;
    }

    /// <summary>
    /// 一键领取所有启用账号的每日积分：逐账号「切换凭证 → 读领取前积分 → 领取 → 读领取后积分 → 记录」。
    /// 结果写入台账（配置目录 credits-claims.json，跨日去重）与报告文件（credits-claim-*.md/.json）。
    /// Mock 执行工具使用仿真积分客户端，供无服务端环境演练完整流程。
    /// </summary>
    public async Task<CreditsClaimSummary> ClaimDailyCreditsAsync(bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!Options.CreditsClaim.Enabled)
        {
            throw new InvalidOperationException("每日积分领取已在配置中关闭（credits_claim.enabled = false）");
        }

        var cli = Options.AgentKind;
        ICreditsApiClient api = cli == AgentKind.Mock
            ? new MockCreditsApiClient()
            : new CodeBuddyCreditsApiClient(Options.CreditsClaim);

        var orchestrator = new CreditsClaimOrchestrator(
            VaultHost.Vault, api, new JsonCreditsClaimStore(ConfigDirectory),
            logger: _logger,
            progress: p => CreditsClaimProgressRaised?.Invoke(this, p));

        var accounts = Options.Accounts.Where(a => a.Enabled).ToList();
        var summary = await orchestrator.RunAllAsync(cli, accounts, force, cancellationToken).ConfigureAwait(false);

        if (summary.Records.Count > 0)
        {
            var directory = string.IsNullOrWhiteSpace(Options.TaskFilePath)
                ? ConfigDirectory
                : Path.GetDirectoryName(Path.GetFullPath(Options.TaskFilePath)) ?? ConfigDirectory;
            LastCreditsClaimReportPath = await CreditsClaimReportWriter
                .WriteAsync(summary, directory, cancellationToken).ConfigureAwait(false);
        }

        return summary;
    }

    public async Task<PreflightReport> PreflightAsync()
    {
        var engine = _engine ??= CreateEngine();
        return await engine.PreflightAsync().ConfigureAwait(false);
    }

    public async Task<BatchReport> StartAsync(bool waitIfUnavailable, CancellationToken cancellationToken = default)
    {
        var engine = _engine ??= CreateEngine();
        IsRunning = true;
        try
        {
            // 批前自动领取（credits_claim.auto_before_batch，默认关闭）：失败不阻断批处理
            if (Options.CreditsClaim is { Enabled: true, AutoBeforeBatch: true })
            {
                try
                {
                    await ClaimDailyCreditsAsync(force: false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Write("WARN", $"[积分领取] 批前自动领取未完成：{ex.Message}");
                }
            }

            var report = await engine.StartAsync(waitIfUnavailable, cancellationToken).ConfigureAwait(false);
            LastReport = report;
            LastReportPath = await _reportWriter.WriteAsync(report, Options.TaskFilePath, cancellationToken)
                .ConfigureAwait(false);
            Status = report.BatchStatus;
            await MaybeShutdownAsync(report).ConfigureAwait(false);
            return report;
        }
        finally
        {
            IsRunning = false;
            TasksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task TerminateAsync(bool waitCurrentTask = false)
    {
        _engine?.TerminateAsync(waitCurrentTask);
        return Task.CompletedTask;
    }

    public void CancelWaiting() => _engine?.CancelWaiting();

    public void NotifyConfigurationChanged() => _engine?.NotifyConfigurationChanged();

    public void ResetAllCooldowns() => _engine?.ResetAllCooldowns();

    /// <summary>自动关机（REQ-015）：正常完成 + 落盘完成 + 可撤销倒计时。</summary>
    private async Task MaybeShutdownAsync(BatchReport report)
    {
        if (!Options.AutoShutdown.Enabled)
        {
            return;
        }

        if (report.BatchStatus != BatchStatus.Completed)
        {
            report.Shutdown = new ShutdownOutcome
            {
                Requested = true, Executed = false,
                Reason = $"批级状态为 {report.BatchStatus}，不满足关机触发条件"
            };
            return;
        }

        if (Options.AutoShutdown.OnlyWhenZeroFailure && report.Failed > 0)
        {
            report.Shutdown = new ShutdownOutcome
            {
                Requested = true, Executed = false,
                Reason = $"存在 {report.Failed} 条失败任务，已勾选「仅失败率为 0 时关机」"
            };
            return;
        }

        if (!_shutdownService.IsSupported)
        {
            report.Shutdown = new ShutdownOutcome
            {
                Requested = true, Executed = false, Reason = "当前平台不支持自动关机，请手动关机"
            };
            return;
        }

        // 结果已 100% 落盘后才关机（硬性顺序约束）
        var executed = await _shutdownService.TryShutdownAsync().ConfigureAwait(false);
        report.Shutdown = new ShutdownOutcome
        {
            Requested = true, Executed = executed,
            Reason = executed ? "批处理正常完成，已触发关机" : "权限不足，已降级为提示手动关机"
        };
    }
}
