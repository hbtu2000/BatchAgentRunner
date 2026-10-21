using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Options;

/// <summary>④ 专家（REQ-004a）与兜底（REQ-004b）。</summary>
public sealed partial class RunOptions
{
    [JsonPropertyName("expert")]
    public ExpertRef Expert { get; set; } = ExpertRef.Default();

    [JsonPropertyName("fallback")]
    public FallbackConfig Fallback { get; set; } = new();
}
