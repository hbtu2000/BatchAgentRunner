using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Options;

/// <summary>③ 账号池 × 候选模型（REQ-003）。</summary>
public sealed partial class RunOptions
{
    [JsonPropertyName("accounts")]
    public List<AccountConfig> Accounts { get; set; } = new();

    /// <summary>
    /// 全局共享候选模型列表（v1.12 起为<b>兼容字段</b>）：仅对未配置自有模型池
    /// （<see cref="Domain.AccountConfig.ModelCandidates"/> 为空）的账号生效。
    /// </summary>
    [JsonPropertyName("model_candidates")]
    public List<ModelCandidate> ModelCandidates { get; set; } = new();

    [JsonPropertyName("degradation_strategy")]
    public DegradationStrategy DegradationStrategy { get; set; } = DegradationStrategy.ModelFirst;
}
