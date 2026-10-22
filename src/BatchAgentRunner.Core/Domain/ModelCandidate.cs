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
    /// <summary>模型名称（CLI 模型标识，调度与展示共用）。</summary>
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

    /// <summary>是否全天可用（未配置免费时段时恒为 true）。</summary>
    [JsonIgnore]
    public bool IsAlwaysAvailable => FreeTimeWindow is null;

    /// <summary>给定时间是否落在免费时段内；未配置时段视为恒真。</summary>
    /// <param name="now">待判定的时刻（调度决策的时间基准）。</param>
    public bool IsInFreeWindow(DateTimeOffset now) => FreeTimeWindow is null || FreeTimeWindow.Contains(now);

    /// <summary>下一免费时段开始时间（晚于 now 的下一段开始；未配置时段返回 null）。</summary>
    /// <param name="now">基准时刻，返回严格晚于此刻的时段起点。</param>
    public DateTimeOffset? NextWindowStart(DateTimeOffset now) => FreeTimeWindow?.NextStartAfter(now);

    /// <summary>返回模型名称。</summary>
    public override string ToString() => Name;
}
