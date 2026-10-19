using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Infrastructure.CredentialVault;

namespace BatchAgentRunner.Desktop.Services;

/// <summary>
/// 凭证 Vault 宿主（DESIGN-账号凭证切换-v1.0 §7）：装配 Vault 与引导登录编排器，
/// 向配置页提供「引导登录 / 切换到此账号 / 快照状态」能力。
/// </summary>
/// <remarks>
/// CA1001（P0-12）：Vault 实现现为 <see cref="IDisposable"/>（SemaphoreSlim 门），
/// 宿主负责在用毕时级联释放；EngineHost 再级联释放本宿主。
/// <para>
/// P1-4/P1-5 收口：装配（DPAPI Vault + 进程守卫 + CLI 登录启动器 + 引导登录编排器）
/// 已上移至组合根 <c>Desktop/Composition/EngineFactory.CreateVaultHost</c>，
/// 本类只经构造注入持依赖，不再自行 <c>new</c> Infrastructure 具体类型（§4.3）。
/// </para>
/// </remarks>
public sealed class CredentialVaultHost : IDisposable
{
    public ICredentialVault Vault { get; }

    /// <summary>引导登录编排器（Infrastructure 具体类型，仅内部使用；P1-4 收敛可见性避免经宿主泄漏到 UI 层）。</summary>
    private GuidedLoginOrchestrator Orchestrator { get; }

    /// <summary>凭证位置注册表（P1-16：宿主持有唯一实例，引导登录链路显式共用同一份覆盖状态）。</summary>
    public ICredentialLocationRegistry Locations { get; }

    public CredentialVaultHost(ICredentialVault vault, GuidedLoginOrchestrator orchestrator,
        ICredentialLocationRegistry locations)
    {
        Vault = vault;
        Orchestrator = orchestrator;
        Locations = locations ?? throw new ArgumentNullException(nameof(locations));
    }

    /// <summary>快照状态的人话描述（配置页账号卡展示，DESIGN §7「快照状态」）。</summary>
    public string DescribeSnapshot(AgentKind kind, string accountId)
    {
        try
        {
            var info = Vault.GetSnapshot(kind, accountId);
            return info is null
                ? "快照：尚未备份（无快照）"
                : $"快照：{info.BackedUpAt:MM-dd HH:mm} 备份" +
                  (info.LastVerifiedAt.HasValue ? $" / {info.LastVerifiedAt.Value:MM-dd HH:mm} 验证通过" : " / 未验证");
        }
        catch (Exception ex)
        {
            return $"快照：状态未知（{ex.Message}）";
        }
    }

    /// <summary>
    /// 引导登录全流程（DESIGN §5）。校验用临时适配器的极简请求（REQ-010 预检逻辑）。
    /// </summary>
    public async Task<CredentialSnapshotInfo> GuidedLoginAsync(AgentKind kind, AccountConfig account,
        Action<GuidedLoginProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var adapter = AgentAdapterFactory.Create(new RunOptions { AgentKind = kind }, registry: Locations);
        return await Orchestrator.RunAsync(kind, account.Id, account.DisplayName,
            validator: (id, token) => adapter.ValidateAccountAsync(
                new AccountConfig { Id = id, DisplayName = account.DisplayName }, token),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>手动切换：把指定账号的快照复制回 Live（DESIGN §6）。</summary>
    public Task SwitchAsync(AgentKind kind, string accountId, CancellationToken cancellationToken = default) =>
        Vault.SwitchToAsync(kind, accountId, "manual", cancellationToken);

    /// <summary>删除账号快照（界面层负责二次确认，DESIGN §8.5）。</summary>
    public Task DeleteSnapshotAsync(AgentKind kind, string accountId, CancellationToken cancellationToken = default) =>
        Vault.DeleteSnapshotAsync(kind, accountId, cancellationToken);

    /// <summary>级联释放 Vault（CA1001：FileCredentialVault 持 SemaphoreSlim 门）。</summary>
    public void Dispose()
    {
        (Vault as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
