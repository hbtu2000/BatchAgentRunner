using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>积分余额查询结果（Balance 为 null 表示服务端响应中未识别出余额字段）。</summary>
public sealed record CreditsBalanceResult(decimal? Balance, string? RawJson);

/// <summary>每日积分领取接口调用结果。</summary>
public sealed record CreditsClaimApiResult(
    bool Success,
    bool AlreadyClaimed,
    /// <summary>服务端响应中识别出的领取后余额（可能为 null）。</summary>
    decimal? BalanceAfter,
    /// <summary>服务端声明的本次发放量（可能为 null）。</summary>
    decimal? Granted,
    string? Message);

/// <summary>
/// 积分服务端 API 端口（每日积分领取 REQ）。
/// 实现方负责鉴权（Bearer accessToken）与响应解析；端点地址由 <c>RunOptions.CreditsClaim</c> 配置。
/// </summary>
public interface ICreditsApiClient
{
    Task<CreditsBalanceResult> GetBalanceAsync(string accessToken, CancellationToken cancellationToken = default);

    Task<CreditsClaimApiResult> ClaimDailyAsync(string accessToken, CancellationToken cancellationToken = default);
}

/// <summary>积分领取台账持久化端口（「每日一次」去重的依据）。</summary>
public interface ICreditsClaimStore
{
    Task<CreditsClaimLedger> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CreditsClaimLedger ledger, CancellationToken cancellationToken = default);
}

/// <summary>
/// Live 凭证登录票据读取端口：从 CLI 当前 Live 凭证文件提取 accessToken（供积分 API 鉴权）。
/// 未读到时返回 null（不抛异常），由编排器决定失败语义；实现不得在返回值/日志中泄露票据内容。
/// </summary>
public interface ICredentialTokenReader
{
    string? TryReadAccessToken(AgentKind cli);
}
