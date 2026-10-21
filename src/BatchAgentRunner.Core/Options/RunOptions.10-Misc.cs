using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>⑪ 其它：结果截断与快照保留。</summary>
public sealed partial class RunOptions
{
    /// <summary>单任务结果大小上限（KB，超出截断并提示全量日志位置，PRD 第 8 章）。</summary>
    [JsonPropertyName("result_max_kb")]
    public int ResultMaxKB { get; set; } = 100;

    [JsonPropertyName("backup_retention")]
    public int BackupRetention { get; set; } = 5;
}
