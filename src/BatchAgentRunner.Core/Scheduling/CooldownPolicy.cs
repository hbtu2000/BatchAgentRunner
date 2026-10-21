using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 冷却判定策略对象（A-2 / P-03）：封装「额度耗尽时记录冷却重置并清理过期记录」的纯判定与状态操作。
/// 与 <see cref="QuotaCooldownRegistry"/> 协作——记录/清理本身是状态操作（由注册表持有），
/// 本策略对象固化「何时记录、记录后清理」的时序，可独立单测（§3.3）。
/// </summary>
public sealed class CooldownPolicy
{
    private readonly QuotaCooldownRegistry _cooldowns;

    public CooldownPolicy(QuotaCooldownRegistry cooldowns) => _cooldowns = cooldowns;

    /// <summary>
    /// 额度耗尽时：记录冷却重置点（cli 给出的重置时间不可靠时降级为默认冷却），随后清理已过期记录。
    /// 返回所落地的冷却记录，供上层广播 <see cref="BatchEventKind.QuotaResetRecorded"/> 与日志使用。
    /// </summary>
    public QuotaCooldownRecord RecordAndPurge(
        string accountId,
        string modelName,
        DateTimeOffset? resetAt,
        DateTimeOffset now,
        string? raw = null)
    {
        var record = _cooldowns.Record(accountId, modelName, resetAt, now, raw);
        _cooldowns.PurgeExpired(now);
        return record;
    }
}
