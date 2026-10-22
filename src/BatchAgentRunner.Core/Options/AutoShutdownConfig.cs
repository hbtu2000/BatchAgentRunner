using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>自动关机配置（REQ-015）。默认关闭。</summary>
public sealed class AutoShutdownConfig
{
    /// <summary>是否启用批处理完成后自动关机（默认 false）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>勾选后：存在 failed 任务则不关机。默认 false（允许带 failed 关机）。</summary>
    [JsonPropertyName("only_when_zero_failure")]
    public bool OnlyWhenZeroFailure { get; set; }

    /// <summary>关机倒计时秒数（默认 60；倒计时期间可取消关机）。</summary>
    [JsonPropertyName("countdown_seconds")]
    public int CountdownSeconds { get; set; } = 60;
}
