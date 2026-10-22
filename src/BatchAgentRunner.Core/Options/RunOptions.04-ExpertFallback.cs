using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Options;

/// <summary>④ 专家（REQ-004a）与兜底（REQ-004b）。</summary>
public sealed partial class RunOptions
{
    /// <summary>专家 / 专家团引用（REQ-004a；默认未指定，使用默认智能体）。</summary>
    [JsonPropertyName("expert")]
    public ExpertRef Expert { get; set; } = ExpertRef.Default();

    /// <summary>特殊模型兜底配置（REQ-004b；默认关闭）。</summary>
    [JsonPropertyName("fallback")]
    public FallbackConfig Fallback { get; set; } = new();
}
