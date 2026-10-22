using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>⑦ 自动关机（REQ-015，P1）、⑧ 每日积分领取。</summary>
public sealed partial class RunOptions
{
    /// <summary>自动关机配置（REQ-015，默认关闭）。</summary>
    [JsonPropertyName("auto_shutdown")]
    public AutoShutdownConfig AutoShutdown { get; set; } = new();

    /// <summary>每日积分领取配置。</summary>
    [JsonPropertyName("credits_claim")]
    public CreditsClaimConfig CreditsClaim { get; set; } = new();
}
