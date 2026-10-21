using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>⑦ 自动关机（REQ-015，P1）、⑧ 每日积分领取。</summary>
public sealed partial class RunOptions
{
    [JsonPropertyName("auto_shutdown")]
    public AutoShutdownConfig AutoShutdown { get; set; } = new();

    [JsonPropertyName("credits_claim")]
    public CreditsClaimConfig CreditsClaim { get; set; } = new();
}
