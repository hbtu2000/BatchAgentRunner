using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>⑨ 交互自动应答（REQ-002a / REQ-002b）。</summary>
public sealed partial class RunOptions
{
    /// <summary>交互自动应答策略（REQ-002a / REQ-002b；默认策略由 <c>AutoAnswerPolicy.CreateDefault()</c> 提供）。</summary>
    [JsonPropertyName("auto_answer")]
    public AutoAnswerPolicy AutoAnswer { get; set; } = AutoAnswerPolicy.CreateDefault();
}
