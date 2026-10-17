using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.CredentialVault;
using BatchAgentRunner.Infrastructure.Diff;
using BatchAgentRunner.Infrastructure.Git;
using BatchAgentRunner.Infrastructure.Http;
using BatchAgentRunner.Infrastructure.Logging;
using BatchAgentRunner.Infrastructure.Notification;
using BatchAgentRunner.Infrastructure.Persistence;
using BatchAgentRunner.Infrastructure.Platform;
using BatchAgentRunner.Infrastructure.Reporting;
using BatchAgentRunner.Infrastructure.TaskFiles;
using BatchAgentRunner.Desktop.Views;
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
    /// <summary>
    /// 自动关机协调器（REQ-015 / P0-10）：负责触发条件判定与可撤销倒计时策略，
    /// Desktop 只提供倒计时窗口。策略不再散落在 UI 宿主里。
    /// </summary>
    private readonly AutoShutdownCoordinator _shutdownCoordinator = new(new ShutdownService());

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

    /// <summary>引擎事件（已在 UI 线程广播，订阅方可直接改 UI 集合）。</summary>
    public event EventHandler<BatchEvent>? EventRaised;

    /// <summary>任务列表变更：已在 UI 线程广播。</summary>
    public event EventHandler? TasksChanged;

    /// <summary>每日积分领取进度：已在 UI 线程广播。</summary>
    public event EventHandler<CreditsClaimProgress>? CreditsClaimProgressRaised;

    /// <summary>唯一的事件广播出口（P0-7）：保证订阅方永远在 UI 线程被调用。</summary>
    private void Raise(BatchEvent ev) => UiThread.Post(() => EventRaised?.Invoke(this, ev));

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
            credentialVault: VaultHost.Vault,
            notifications: Options.Notifications,
            notificationDispatcher: CreateNotificationDispatcher(),
            diffCollector: Options.CollectDiffs ? new DiffCollector(_logger, Options) : null,
            git: new LocalGitIntegration(),
            markdownWriter: new MarkdownInPlaceWriter());
        // P0-7：单一收敛点。引擎事件在后台线程触发，这里统一切到 UI 线程后再广播，
        // 订阅方（7 处）不必再各自记得 Dispatcher.UIThread.Post —— 由构造保证，而不是靠约定。
        engine.EventRaised += (_, ev) => UiThread.Post(() =>
        {
            if (ev.BatchStatus.HasValue)
            {
                Status = ev.BatchStatus.Value;
                IsRunning = ev.BatchStatus.Value is BatchStatus.Running or BatchStatus.Waiting;
            }

            EventRaised?.Invoke(this, ev);
        });
        engine.TasksChanged += (_, _) => UiThread.Post(() => TasksChanged?.Invoke(this, EventArgs.Empty));
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

    /// <summary>
    /// 二期通知分发器（REQ-018）：Desktop 通道转为主界面 toast（经引擎事件广播），
    /// Webhook / SMTP 走各自 notifier。仅当配置了通知规则时才需要，无规则时不构造也不分发。
    /// </summary>
    private INotificationDispatcher CreateNotificationDispatcher()
    {
        var notifiers = new List<INotifier>
        {
            new DesktopNotifier((title, message) => Raise(new BatchEvent(BatchEventKind.Info, DateTimeOffset.Now, $"[通知] {title}：{message}"))),
            new WebhookNotifier(),
            new SmtpNotifier(_logger)
        };
        return new NotificationDispatcher(notifiers);
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
            throw new InvalidOperationException("积分领取已在配置中关闭（credits_claim.enabled = false）");
        }

        var cli = Options.AgentKind;
        ICreditsApiClient api = cli == AgentKind.Mock
            ? new MockCreditsApiClient()
            : new CodeBuddyCreditsApiClient(Options.CreditsClaim);

        var orchestrator = new CreditsClaimOrchestrator(
            VaultHost.Vault, api, new JsonCreditsClaimStore(ConfigDirectory),
            logger: _logger,
            progress: p => UiThread.Post(() => CreditsClaimProgressRaised?.Invoke(this, p)));

        var accounts = Options.Accounts.Where(a => a.Enabled).ToList();
        var summary = await orchestrator.RunAllAsync(cli, accounts, force, cancellationToken)
            .ConfigureAwait(false);

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

            BatchReport report;
            try
            {
                report = await engine.StartAsync(waitIfUnavailable, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 引擎未预期异常必须落运行日志（2026-10-16 教训：仅 UI 状态栏展示，日志无记录无法排障）
                _logger?.Write("ERROR", $"批处理执行异常终止：{ex.GetType().Name}：{ex.Message}");
                throw;
            }

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

    /// <summary>
    /// 自动关机（REQ-015）：正常完成 + 落盘完成 + 可撤销倒计时。
    /// P0-10：条件判定与倒计时策略已下沉到 <see cref="AutoShutdownCoordinator"/>，
    /// 本类只提供倒计时窗口；不再出现「判定完直接关机」的强关路径。
    /// </summary>
    private async Task MaybeShutdownAsync(BatchReport report)
    {
        // 结果已 100% 落盘（报告写完后）才进入关机流程（REQ-015 顺序硬约束）
        report.Shutdown = await _shutdownCoordinator
            .RunAsync(Options.AutoShutdown, report, ShowShutdownCountdownAsync)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 关机倒计时窗口（REQ-015 可撤销倒计时）。返回 true = 倒计时走完未被取消。
    /// 窗口弹不出来时返回 false：宁可不关机，也不能让用户在无法撤销的情况下被关机。
    /// </summary>
    private static Task<bool> ShowShutdownCountdownAsync(int countdownSeconds, CancellationToken cancellationToken)
    {
        if (countdownSeconds <= 0)
        {
            return Task.FromResult(true);
        }

        return UiThread.InvokeAsync(async () =>
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner is null)
            {
                return false;
            }

            var window = new ShutdownCountdownWindow(countdownSeconds);
            return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
        });
    }
}
