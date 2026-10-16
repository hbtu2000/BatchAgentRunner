using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>
/// 凭证 Vault 端口（DESIGN-账号凭证切换-v1.0 §3-§6：Live / Snapshot / Vault 模型）。
/// Live 是 CLI 实际读取的凭证文件，同一时刻每个 CLI 只有一个身份；
/// Snapshot 是按账号命名的凭证文件集副本；Vault 是快照仓库。
/// 安全约定（DESIGN §8）：凭证明文只存在于内存与 Live 位置，Vault 内静态存储必须加密；
/// 审计日志只记账号 id 与文件哈希前 8 位，不记凭证内容。
/// </summary>
public interface ICredentialVault
{
    /// <summary>Live 当前归属的账号（工具自己的记账，从未备份/切换过时为 null）。</summary>
    string? CurrentLiveAccount(AgentKind cli);

    bool SnapshotExists(AgentKind cli, string accountId);

    CredentialSnapshotInfo? GetSnapshot(AgentKind cli, string accountId);

    /// <summary>
    /// 引导登录完成后备份：Live 凭证文件集 → vault\cli\account（DESIGN §5 ⑦）。
    /// 备份的是文件副本，不动 Live；重复备份覆盖旧快照并更新时间戳（保留 last_verified_at）。
    /// </summary>
    Task<CredentialSnapshotInfo> BackupFromLiveAsync(AgentKind cli, string accountId,
        string? displayName = null, string? site = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换 = 复制回填（DESIGN §6）：① 进程互斥检查 → ② 保活回写当前账号（Live 哈希 ≠ 其快照时）
    /// → ③ 原子覆盖（临时文件 + rename，失败自动回滚）→ ④ 记账 + 审计。
    /// 目标快照缺失、CLI 进程运行中时抛 <see cref="CredentialVaultException"/>。
    /// </summary>
    Task SwitchToAsync(AgentKind cli, string accountId, string reason,
        CancellationToken cancellationToken = default);

    /// <summary>预检通过后更新 manifest.last_verified_at（DESIGN §7）。</summary>
    Task MarkVerifiedAsync(AgentKind cli, string accountId, CancellationToken cancellationToken = default);

    /// <summary>删除账号快照（删除账号时调用；界面层负责二次确认，DESIGN §8.5）。</summary>
    Task DeleteSnapshotAsync(AgentKind cli, string accountId, CancellationToken cancellationToken = default);
}

/// <summary>
/// CLI 进程互斥探测（DESIGN §6 ① / §9）：切换与备份前确认该 CLI 没有运行中的进程，
/// 避免凭证文件被运行中的 CLI 覆盖。
/// </summary>
public interface ICliProcessGuard
{
    bool IsCliRunning(AgentKind cli);
}
