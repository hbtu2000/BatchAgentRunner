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
using BatchAgentRunner.Infrastructure.Reporting;
using BatchAgentRunner.Infrastructure.TaskFiles;

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

    /// <summary>出厂示例配置（与 <c>JsonConfigStore.CreateSample()</c> 等价，经工厂暴露以避免 UI 层直引 Infrastructure）。</summary>
    public static RunOptions CreateSampleRunOptions() => JsonConfigStore.CreateSample();

    /// <summary>凭证 Vault 宿主（DPAPI 加密 + 真实进程守卫；Mock 工具指向工具私有目录）。</summary>
    public CredentialVaultHost VaultHost { get; } = CreateVaultHost();

    /// <inheritdoc />
    public IBatchEngine CreateEngine(
        RunOptions options,
        IRunLogger logger,
        ICredentialVault? credentialVault,
        Action<string, string> raiseToast)
    {
        var adapter = AgentAdapterFactory.Create(options, msg => logger.Write("INFO", msg));
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
            credentialVault: credentialVault,
            notifications: options.Notifications,
            notificationDispatcher: dispatcher,
            diffCollector: diffCollector,
            git: new LocalGitIntegration(),
            markdownWriter: new MarkdownInPlaceWriter(),
            processRunner: new RealProcessRunner());
    }

    /// <summary>构造 CLI 适配器（按当前执行工具）。</summary>
    public static IAgentCliAdapter CreateAdapter(RunOptions options) => AgentAdapterFactory.Create(options);

    /// <summary>构造运行日志（按任务文件路径落盘）。</summary>
    public static IRunLogger CreateLogger(RunOptions options) => new FileRunLogger(options.TaskFilePath);

    /// <summary>构造积分领取 API 客户端（Mock 走仿真，生产走真实端点）。</summary>
    public static ICreditsApiClient CreateApiClient(AgentKind kind, CreditsClaimConfig config)
        => kind == AgentKind.Mock ? new MockCreditsApiClient() : new CodeBuddyCreditsApiClient(config);

    /// <summary>构造积分领取编排器（切换凭证 → 读前 → 领取 → 读后 → 记录）。</summary>
    public static CreditsClaimOrchestrator CreateCreditsOrchestrator(
        ICredentialVault vault,
        ICreditsApiClient api,
        IRunLogger? logger,
        string configDirectory,
        Action<CreditsClaimProgress> progress)
        => new CreditsClaimOrchestrator(vault, api, new JsonCreditsClaimStore(configDirectory), logger: logger, progress: progress);

    /// <summary>写积分领取报告（Markdown / JSON）。</summary>
    public static Task<string> WriteCreditsClaimReportAsync(CreditsClaimSummary summary, string directory, CancellationToken cancellationToken)
        => CreditsClaimReportWriter.WriteAsync(summary, directory, cancellationToken);

    [SuppressMessage("Performance", "CA1859:使用具体类型以获得性能", Justification = "接口返回类型是 P1-4/P1-5 DI 目标形态，拒绝向具体类型回退")]
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
}
