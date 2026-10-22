using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Options;

/// <summary>③ 账号池 × 候选模型（REQ-003）。</summary>
public sealed partial class RunOptions
{
    /// <summary>账号池（按配置顺序选择；<c>enabled=false</c> 的账号不参与调度）。</summary>
    [JsonPropertyName("accounts")]
    public List<AccountConfig> Accounts { get; set; } = new();

    /// <summary>
    /// 全局共享候选模型列表（v1.12 起为<b>兼容字段</b>）：仅对未配置自有模型池
    /// （<see cref="Domain.AccountConfig.ModelCandidates"/> 为空）的账号生效。
    /// </summary>
    [JsonPropertyName("model_candidates")]
    public List<ModelCandidate> ModelCandidates { get; set; } = new();

    /// <summary>
    /// 额度 / 模型不可用时的降级策略（默认 <c>ModelFirst</c>：同账号先轮换候选模型，该账号模型全耗尽再换账号）。
    /// </summary>
    [JsonPropertyName("degradation_strategy")]
    public DegradationStrategy DegradationStrategy { get; set; } = DegradationStrategy.ModelFirst;
}
