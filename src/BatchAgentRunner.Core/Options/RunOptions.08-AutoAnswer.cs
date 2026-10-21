using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>⑨ 交互自动应答（REQ-002a / REQ-002b）。</summary>
public sealed partial class RunOptions
{
    [JsonPropertyName("auto_answer")]
    public AutoAnswerPolicy AutoAnswer { get; set; } = AutoAnswerPolicy.CreateDefault();
}
