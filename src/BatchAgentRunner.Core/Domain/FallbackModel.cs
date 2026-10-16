using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 账号自有的兜底模型项（REQ-004b，v1.12：兜底模型池挂在账号下）。
/// 不参与免费时段过滤（兜底的目的是保证当前任务能收尾，R-3）。
/// </summary>
public sealed class FallbackModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 预估单次调用单价（元）。仅当 CLI 不返回用量/费用数据时用于「调用次数 × 预估单价」估算口径（REQ-004b）。
    /// </summary>
    [JsonPropertyName("estimated_unit_price")]
    public decimal? EstimatedUnitPrice { get; set; }

    /// <summary>备注：仅供用户自解释，不参与任何调度逻辑。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    public override string ToString() => Name;
}
