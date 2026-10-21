using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>⑥ 并发 / 续跑 / 失败即停（P1）。</summary>
public sealed partial class RunOptions
{
    [JsonPropertyName("concurrency")]
    public int Concurrency { get; set; } = 1;

    [JsonPropertyName("force_rerun")]
    public bool ForceRerun { get; set; }

    [JsonPropertyName("stop_on_failure")]
    public bool StopOnFailure { get; set; }

    /// <summary>终止时是否等待当前任务完成后再终止（PRD 用例 4，默认立即中断）。</summary>
    [JsonPropertyName("terminate_after_current_task")]
    public bool TerminateAfterCurrentTask { get; set; }
}
