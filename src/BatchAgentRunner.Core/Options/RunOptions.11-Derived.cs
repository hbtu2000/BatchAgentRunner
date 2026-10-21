using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>
/// ⑫ 派生属性：由上面的分钟/布尔字段换算得出，一律 <c>[JsonIgnore]</c> 不落盘
/// （契约由 <c>RunOptionsSerializationTests</c> 的「派生属性不写入配置文件」锁死）。
/// </summary>
public sealed partial class RunOptions
{
    /// <summary>单任务超时（派生自 <see cref="TaskTimeoutMinutes"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public TimeSpan TaskTimeout => TimeSpan.FromMinutes(Math.Max(1, TaskTimeoutMinutes));

    /// <summary>单任务总时长上限（派生自 <see cref="MaxTotalMinutesPerTask"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public TimeSpan MaxTaskDuration => TimeSpan.FromMinutes(Math.Max(1, MaxTotalMinutesPerTask));

    /// <summary>最长可等待时长（派生自 <see cref="MaxWaitMinutes"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public TimeSpan MaxWait => TimeSpan.FromMinutes(Math.Max(0, MaxWaitMinutes));

    /// <summary>额度耗尽是否无限等待重置（派生自 <see cref="WaitForQuotaReset"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public bool WaitForQuotaResetEnabled => WaitForQuotaReset;

    /// <summary>默认冷却时长（派生自 <see cref="DefaultCooldownMinutes"/>，不单独持久化）。</summary>
    [JsonIgnore]
    public TimeSpan DefaultCooldown => TimeSpan.FromMinutes(Math.Max(1, DefaultCooldownMinutes));
}
