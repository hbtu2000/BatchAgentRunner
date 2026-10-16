using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 候选模型项（REQ-003 / REQ-003b，v1.12）。
/// 关键语义：候选模型池挂在<b>账号下</b>（<see cref="AccountConfig.ModelCandidates"/>）；
/// 同名模型在不同账号下是不同组合（免费时段 / 单价 / 冷却各自独立）。
/// 顶层全局 <c>model_candidates</c> 仅作为兼容字段：账号未配置自有池时回退使用。
/// </summary>
public sealed class ModelCandidate
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>免费时段，null 表示全天可用（REQ-003b 配置示例字段 free_time_window）。</summary>
    [JsonPropertyName("free_time_window")]
    public FreeTimeWindow? FreeTimeWindow { get; set; }

    /// <summary>
    /// 预估单次调用单价（元）。仅当 CLI 不返回用量/费用数据时用于「调用次数 × 预估单价」估算口径（REQ-004b）。
    /// </summary>
    [JsonPropertyName("estimated_unit_price")]
    public decimal? EstimatedUnitPrice { get; set; }

    /// <summary>
    /// 折扣倍率（如 0.65 = 原价的 65%，界面显示为「0.65x」）。
    /// 仅用于配置页展示与人工对照（对齐模型选择器里的折扣列），不参与调度判定；
    /// 留空 = 不显示折扣。
    /// </summary>
    [JsonPropertyName("discount")]
    public decimal? Discount { get; set; }

    /// <summary>备注：仅供用户自解释，不参与任何调度逻辑。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonIgnore]
    public bool IsAlwaysAvailable => FreeTimeWindow is null;

    public bool IsInFreeWindow(DateTimeOffset now) => FreeTimeWindow is null || FreeTimeWindow.Contains(now);

    public DateTimeOffset? NextWindowStart(DateTimeOffset now) => FreeTimeWindow?.NextStartAfter(now);

    public override string ToString() => Name;
}
