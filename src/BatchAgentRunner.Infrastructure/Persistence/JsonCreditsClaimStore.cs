using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Infrastructure.Persistence;

/// <summary>
/// 积分领取台账持久化（配置目录 credits-claims.json，与 config.json 同目录）。
/// 只记结果元数据（账号 id / 状态 / 前后积分），不含任何票据内容。
/// </summary>
public sealed class JsonCreditsClaimStore : JsonStoreBase<CreditsClaimLedger>, ICreditsClaimStore
{
    /// <summary>以指定目录（默认应用配置目录）读写 credits-claims.json 领取台账。</summary>
    public JsonCreditsClaimStore(string? directory = null)
        : base(directory, "credits-claims.json")
    {
    }

    /// <inheritdoc />
    protected override CreditsClaimLedger CreateEmpty() => new();
}
