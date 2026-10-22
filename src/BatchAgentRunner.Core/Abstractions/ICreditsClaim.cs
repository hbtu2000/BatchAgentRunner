using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>积分余额查询结果（Balance 为 null 表示服务端响应中未识别出余额字段）。</summary>
public sealed record CreditsBalanceResult(decimal? Balance, string? RawJson);

/// <summary>
/// 每日积分领取接口调用结果。
/// </summary>
/// <param name="Success">领取是否成功。</param>
/// <param name="AlreadyClaimed">服务端是否反馈「今日已领取过」。</param>
/// <param name="BalanceAfter">服务端响应中识别出的领取后余额（可能为 null）。</param>
/// <param name="Granted">服务端声明的本次发放量（可能为 null）。</param>
/// <param name="Message">服务端提示消息（可能为 null）。</param>
public sealed record CreditsClaimApiResult(
    bool Success,
    bool AlreadyClaimed,
    decimal? BalanceAfter,
    decimal? Granted,
    string? Message);

/// <summary>
/// 积分服务端 API 端口（积分领取 REQ）。
/// 实现方负责鉴权（Bearer accessToken）与响应解析；端点地址由 <c>RunOptions.CreditsClaim</c> 配置。
/// </summary>
public interface ICreditsApiClient
{
    /// <summary>查询积分余额（鉴权后调余额端点）。</summary>
    Task<CreditsBalanceResult> GetBalanceAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>每日领取：调每日领取端点（<c>RunOptions.CreditsClaim.ClaimPath</c>）。</summary>
    Task<CreditsClaimApiResult> ClaimDailyAsync(string accessToken, CancellationToken cancellationToken = default);
}

/// <summary>积分领取台账持久化端口（「每日一次」去重的依据）。</summary>
public interface ICreditsClaimStore
{
    /// <summary>加载台账（文件缺失时返回空台账）。</summary>
    Task<CreditsClaimLedger> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存台账（原子写）。</summary>
    Task SaveAsync(CreditsClaimLedger ledger, CancellationToken cancellationToken = default);
}

/// <summary>
/// Live 凭证登录票据读取端口：从 CLI 当前 Live 凭证文件提取 accessToken（供积分 API 鉴权）。
/// 未读到时返回 null（不抛异常），由编排器决定失败语义；实现不得在返回值/日志中泄露票据内容。
/// </summary>
public interface ICredentialTokenReader
{
    /// <summary>尝试读取该 CLI 的 Live 登录票据 accessToken；未读到返回 null（不抛异常）。</summary>
    string? TryReadAccessToken(AgentKind cli);
}
