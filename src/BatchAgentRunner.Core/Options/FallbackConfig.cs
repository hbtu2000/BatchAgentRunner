using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>
/// 特殊模型兜底配置（REQ-004b）。默认关闭。
/// v1.12：兜底模型池挂在账号下（<see cref="Domain.AccountConfig.FallbackModels"/>），
/// 本类只保留全局开关与成本上限；旧字段 <c>fallback.model</c> / <c>fallback.estimated_unit_price</c> 已废弃
/// （反序列化时忽略，配置请迁移到各账号的 fallback_models）。
/// </summary>
public sealed class FallbackConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("max_cost_per_task")]
    public decimal MaxCostPerTask { get; set; } = 1.0m;

    [JsonPropertyName("max_cost_per_batch")]
    public decimal MaxCostPerBatch { get; set; } = 10.0m;
}
