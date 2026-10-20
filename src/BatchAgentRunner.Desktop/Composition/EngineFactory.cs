using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Services;
using BatchAgentRunner.Infrastructure;
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
using BatchAgentRunner.Infrastructure.Workflow;

namespace BatchAgentRunner.Desktop.Composition;

/// <summary>
/// 引擎组合根（阶段 2 P1-4/P1-5）：集中 <c>new</c> 全部 Infrastructure / Agents 具体类型，
/// 仅通过 Core 抽象契约向 UI 层暴露。此类是 Desktop 中唯一允许直接引用 Infrastructure/Agent 具体实现的位置，
/// 由 §4.3 架构断言锁定（其余 Desktop 代码经接口与 <see cref="IEngineFactory"/> / <see cref="IEngineHost"/> 交互）。
/// </summary>
public sealed class EngineFactory : IEngineFactory
{
    /// <summary>配置存储（IConfigStore 接口，Infrastructure 实现）。</summary>
    public IConfigStore ConfigStore { get; } = new JsonConfigStore();

    /// <summary>冷却存储（ICooldownStore 接口，Infrastructure 实现）。</summary>
    public ICooldownStore CooldownStore { get; } = new JsonCooldownStore();

    /// <summary>任务文件存储（ITaskFileStore 接口，Infrastructure 实现）。</summary>
    public ITaskFileStore TaskFileStore { get; } = new TaskFileStore();

    /// <summary>报告写入器（IReportWriter 接口，Infrastructure 实现）。</summary>
    public IReportWriter ReportWriter { get; } = new ReportWriter();

    /// <summary>
    /// 凭证位置注册表（P1-16）：替代原静态全局注册表的唯一显式实例，Mock Live 根在此一次性覆写，
    /// 全部凭证链路（Vault / 引导登录 / CLI 适配器 / 积分领取）显式共用同一实例。
    /// </summary>
    public ICredentialLocationRegistry CredentialLocations { get; } = CreateCredentialLocations();

    /// <summary>凭证 Vault 宿主（DPAPI 加密 + 真实进程守卫；Mock 工具指向工具私有目录）。</summary>
    public CredentialVaultHost VaultHost { get; }

    /// <summary>工作流模板库（内置 S1–S16 播种 + 用户自定义目录；UI 只经此接口访问）。</summary>
    public IWorkflowTemplateStore TemplateStore { get; } = new FileWorkflowTemplateStore();

    /// <summary>出厂示例配置（与 <c>JsonConfigStore.CreateSample()</c> 等价，经工厂暴露以避免 UI 层直引 Infrastructure）。</summary>
    public static RunOptions CreateSampleRunOptions() => JsonConfigStore.CreateSample();

    public EngineFactory()
    {
        VaultHost = CreateVaultHost(CredentialLocations);
    }

    private static CredentialLocationRegistry CreateCredentialLocations()
    {
        var registry = new CredentialLocationRegistry();
        var mockLiveRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BatchAgentRunner", "mock-live");
        registry.SetLiveRootOverride(AgentKind.Mock, mockLiveRoot);
        return registry;
    }

    /// <summary>
    /// 装配凭证 Vault 宿主（P1-4/P1-5 收口）：DPAPI 静态加密 + 真实进程守卫 + CLI 登录启动器。
    /// 原 <c>CredentialVaultHost.CreateDefault</c> 是 Desktop/Services 里的第二处组合根，
    /// 与「组合根唯一」原则冲突，装配逻辑收回本工厂（§4.3 断言唯一豁免位置）。
    /// </summary>
    private static CredentialVaultHost CreateVaultHost(ICredentialLocationRegistry registry)
    {
        var guard = new CliProcessGuard(registry);
        var vault = new FileCredentialVault(processGuard: guard, registry: registry);
        var orchestrator = new GuidedLoginOrchestrator(vault, guard, new CliLoginLauncher(registry),
            registry: registry);
        return new CredentialVaultHost(vault, orchestrator, registry);
    }

    /// <summary>
    /// 构造自动关机协调器（REQ-015 / P0-10）：<see cref="IShutdownService"/> 实现由组合根装配，
    /// Desktop 宿主不再自行 <c>new</c> Infrastructure 具体类型（P1-4/P1-5 收口）。
    /// </summary>
    public static AutoShutdownCoordinator CreateShutdownCoordinator() => new(new ShutdownService());

    /// <inheritdoc />
    public IBatchEngine CreateEngine(
        RunOptions options,
        IRunLogger logger,
        ICredentialVault? credentialVault,
        Action<string, string> raiseToast)
    {
        var adapter = AgentAdapterFactory.Create(options, msg => logger.Write("INFO", msg), CredentialLocations);
        if (adapter is MockAdapter mock)
        {
            // Mock 的「Live 凭证文件」与 Vault 共用同一位置，保活回写/切换全链路可用 Mock 联调
            mock.MockLiveCredentialPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BatchAgentRunner", "mock-live", ".credentials.json");
        }

        IDiffCollector? diffCollector = options.CollectDiffs ? new DiffCollector(logger, options) : null;
        INotificationDispatcher dispatcher = CreateNotificationDispatcher(logger, raiseToast);

        return new BatchEngine(
            options,
            adapter,
            TaskFileStore,
            CooldownStore,
            logger,
            new BatchEngineDependencies
            {
                CredentialVault = credentialVault,
                Notifications = options.Notifications,
                NotificationDispatcher = dispatcher,
                DiffCollector = diffCollector,
                Git = new LocalGitIntegration(),
                MarkdownWriter = new MarkdownInPlaceWriter(),
                ProcessRunner = new RealProcessRunner()
            });
    }

    /// <summary>构造 CLI 适配器（按当前执行工具）。</summary>
    public IAgentCliAdapter CreateAdapter(RunOptions options) =>
        AgentAdapterFactory.Create(options, registry: CredentialLocations);

    /// <summary>构造运行日志（按任务文件路径落盘）。</summary>
    public static IRunLogger CreateLogger(RunOptions options) => new FileRunLogger(options.TaskFilePath);

    /// <summary>构造积分领取 API 客户端（Mock 走仿真，生产走真实端点）。</summary>
    public static ICreditsApiClient CreateApiClient(AgentKind kind, CreditsClaimConfig config)
        => kind == AgentKind.Mock ? new MockCreditsApiClient() : new CodeBuddyCreditsApiClient(config);

    /// <summary>构造积分领取编排器（切换凭证 → 读前 → 领取 → 读后 → 记录）。</summary>
    public CreditsClaimOrchestrator CreateCreditsOrchestrator(
        ICredentialVault vault,
        ICreditsApiClient api,
        IRunLogger? logger,
        string configDirectory,
        Action<CreditsClaimProgress> progress)
        => new CreditsClaimOrchestrator(vault, api, new JsonCreditsClaimStore(configDirectory),
            logger: logger, progress: progress, registry: CredentialLocations);

    /// <summary>写积分领取报告（Markdown / JSON）。</summary>
    public static Task<string> WriteCreditsClaimReportAsync(CreditsClaimSummary summary, string directory, CancellationToken cancellationToken)
        => CreditsClaimReportWriter.WriteAsync(summary, directory, cancellationToken);

    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能", Justification = "接口返回类型是 P1-4/P1-5 DI 目标形态，拒绝向具体类型回退；逃逸阀台账 #8 / 到期日 2027-01-20 / 责任人 工程")]
    private static INotificationDispatcher CreateNotificationDispatcher(IRunLogger logger, Action<string, string> raiseToast)
    {
        var notifiers = new List<INotifier>
        {
            new DesktopNotifier(raiseToast),
            new WebhookNotifier(),
            new SmtpNotifier(logger)
        };
        return new NotificationDispatcher(notifiers);
    }
}
