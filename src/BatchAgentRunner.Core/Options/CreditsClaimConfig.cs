using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>
/// 每日积分领取配置。
/// 端点 2026-10-16 已抓包实测确认（契约见 <see cref="Infrastructure.Http.CodeBuddyCreditsApiClient"/>）：
/// 全部为 POST + 空 JSON 体 + Bearer accessToken 鉴权；服务端如调整接口仅需改这里的路径。
/// </summary>
public sealed class CreditsClaimConfig
{
    /// <summary>总开关：关闭后「领取每日积分」入口不可用。默认开启。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>每次批量任务开始前自动为所有账号领取（默认关闭，只提供手动一键入口）。</summary>
    [JsonPropertyName("auto_before_batch")]
    public bool AutoBeforeBatch { get; set; }

    /// <summary>
    /// 强制领取（默认 <c>true</c>）：true 时忽略台账去重，每次都去调领取接口（即「允许重复领取」；
    /// 服务端若判定今日已领过会回「今日已领取过」，但不会在本地因去重而跳过）。
    /// false 时恢复「同账号每日一次」去重语义：今日已领过的账号会被跳过。
    /// </summary>
    [JsonPropertyName("force")]
    public bool Force { get; set; } = true;

    /// <summary>积分服务端 API 基址（实测值，见 <see cref="CodeBuddyApiConstants.ProductionApiBase"/>）。</summary>
    [JsonPropertyName("api_base")]
    public string ApiBase { get; set; } = CodeBuddyApiConstants.ProductionApiBase;

    /// <summary>余额查询路径（POST，资源包剩余 credits 求和；实测值，见 <see cref="CodeBuddyApiConstants.BalancePath"/>）。</summary>
    [JsonPropertyName("balance_path")]
    public string BalancePath { get; set; } = CodeBuddyApiConstants.BalancePath;

    /// <summary>每日领取路径（POST；实测值，见 <see cref="CodeBuddyApiConstants.ClaimPath"/>）。</summary>
    [JsonPropertyName("claim_path")]
    public string ClaimPath { get; set; } = CodeBuddyApiConstants.ClaimPath;

    /// <summary>单次 HTTP 调用超时（秒）。</summary>
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 20;
}
