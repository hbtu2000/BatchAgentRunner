using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>⑥ 并发 / 续跑 / 失败即停（P1）。</summary>
public sealed partial class RunOptions
{
    /// <summary>并发执行任务数（默认 1，串行执行）。</summary>
    [JsonPropertyName("concurrency")]
    public int Concurrency { get; set; } = 1;

    /// <summary>强制重跑：忽略已完成任务的断点状态，本批全部重新执行。</summary>
    [JsonPropertyName("force_rerun")]
    public bool ForceRerun { get; set; }

    /// <summary>任一任务失败即停止整批（默认 false：继续执行其余任务）。</summary>
    [JsonPropertyName("stop_on_failure")]
    public bool StopOnFailure { get; set; }

    /// <summary>终止时是否等待当前任务完成后再终止（PRD 用例 4，默认立即中断）。</summary>
    [JsonPropertyName("terminate_after_current_task")]
    public bool TerminateAfterCurrentTask { get; set; }
}
