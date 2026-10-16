using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 「账号 × 模型」组合的额度冷却记录（REQ-004c）。随运行日志落盘，重启后仍然有效。
/// </summary>
public sealed class QuotaCooldownRecord
{
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string ModelName { get; set; } = string.Empty;

    [JsonPropertyName("reset_at")]
    public DateTimeOffset ResetAt { get; set; }

    [JsonPropertyName("source")]
    public CooldownSource Source { get; set; } = CooldownSource.CliParsed;

    [JsonPropertyName("raw")]
    public string? RawValue { get; set; }

    [JsonPropertyName("recorded_at")]
    public DateTimeOffset RecordedAt { get; set; }

    [JsonIgnore]
    public string Key => BuildKey(AccountId, ModelName);

    public static string BuildKey(string accountId, string modelName) =>
        $"{accountId}::{modelName}";

    public override string ToString() =>
        $"{AccountId} × {ModelName} → {ResetAt:yyyy-MM-dd HH:mm}（{DescribeSource()}）";

    public string DescribeSource() => Source == CooldownSource.CliParsed ? "CLI 解析" : "默认冷却";
}
