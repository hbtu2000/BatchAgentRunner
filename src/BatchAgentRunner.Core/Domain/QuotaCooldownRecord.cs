using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 「账号 × 模型」组合的额度冷却记录（REQ-004c）。随运行日志落盘，重启后仍然有效。
/// </summary>
public sealed class QuotaCooldownRecord
{
    /// <summary>
    /// <see cref="RawValue"/> 的持久化上限（字符数）。原始 CLI 输出可能达数十 MB（整段 JSONL 对话轨迹），
    /// 全量落盘会让 cooldowns.json 膨胀、拖慢引擎初始化；仅保留尾部用于排障即可（错误通常靠近输出末尾）。
    /// </summary>
    public const int MaxRawChars = 4096;

    /// <summary>账号 ID。</summary>
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    /// <summary>模型名。</summary>
    [JsonPropertyName("model")]
    public string ModelName { get; set; } = string.Empty;

    /// <summary>额度重置（恢复）时间点。</summary>
    [JsonPropertyName("reset_at")]
    public DateTimeOffset ResetAt { get; set; }

    /// <summary>重置时间来源（CLI 解析 / 默认冷却降级）。</summary>
    [JsonPropertyName("source")]
    public CooldownSource Source { get; set; } = CooldownSource.CliParsed;

    /// <summary>原始 CLI 输出（仅保留尾部 <see cref="MaxRawChars"/> 字符，排障用）。</summary>
    [JsonPropertyName("raw")]
    public string? RawValue { get; set; }

    /// <summary>记录写入时刻。</summary>
    [JsonPropertyName("recorded_at")]
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>组合键（<c>accountId::modelName</c>，不随 JSON 持久化）。</summary>
    [JsonIgnore]
    public string Key => BuildKey(AccountId, ModelName);

    /// <summary>构造「账号 × 模型」组合键。</summary>
    /// <param name="accountId">账号 ID。</param>
    /// <param name="modelName">模型名。</param>
    /// <returns>组合键。</returns>
    public static string BuildKey(string accountId, string modelName) =>
        $"{accountId}::{modelName}";

    /// <summary>人话版描述，如「账号 × 模型 → 重置时间（来源）」。</summary>
    /// <returns>描述文本。</returns>
    public override string ToString() =>
        $"{AccountId} × {ModelName} → {ResetAt:yyyy-MM-dd HH:mm}（{DescribeSource()}）";

    /// <summary>来源的中文描述（CLI 解析 / 默认冷却）。</summary>
    /// <returns>来源描述。</returns>
    public string DescribeSource() => Source == CooldownSource.CliParsed ? "CLI 解析" : "默认冷却";
}
