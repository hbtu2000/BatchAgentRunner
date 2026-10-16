using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>单个「账号 × 模型」组合在某一时刻的可用性评估结果。</summary>
public sealed record ComboAvailability(
    AgentCombo Combo,
    bool Available,
    ComboSkipReason Reason,
    DateTimeOffset? RestoreAt,
    DateTimeOffset? NextWindowStart,
    DateTimeOffset? ResetAt)
{
    /// <summary>等待类型：时段 / 额度重置 / 两者（组合恢复时间取 max）。</summary>
    public WaitKind WaitKind =>
        NextWindowStart.HasValue && ResetAt.HasValue ? WaitKind.Mixed :
        NextWindowStart.HasValue ? WaitKind.FreeWindow :
        ResetAt.HasValue ? WaitKind.QuotaReset : WaitKind.None;

    public string DescribeSkip()
    {
        if (Available)
        {
            return "可用";
        }

        return Reason switch
        {
            ComboSkipReason.OutsideFreeWindow =>
                $"{Combo.Model.Name} 当前不在免费时段（{Combo.Model.FreeTimeWindow?.Describe()}），已跳过",
            ComboSkipReason.Cooldown =>
                $"{Combo.Model.Name}（{Combo.Account.Label}）额度已耗尽，预计 {ResetAt:HH:mm} 重置恢复，已跳过",
            ComboSkipReason.AccountDisabled =>
                $"账号 {Combo.Account.Label} 已停用，已跳过",
            _ => "不可用"
        };
    }
}

/// <summary>
/// 组合可用性评估：免费时段（时间维度）+ 额度冷却（额度维度）两个<b>独立</b>约束（REQ-003b / REQ-004c）。
/// 恢复时间点 = max(下一时段开始, 额度重置时间)。
/// </summary>
public sealed class AvailabilityEvaluator
{
    private readonly QuotaCooldownRegistry _cooldowns;

    public AvailabilityEvaluator(QuotaCooldownRegistry cooldowns) => _cooldowns = cooldowns;

    public ComboAvailability Evaluate(AgentCombo combo, DateTimeOffset now) =>
        Evaluate(combo.Account, combo.Model, now);

    public ComboAvailability Evaluate(AccountConfig account, ModelCandidate model, DateTimeOffset now)
    {
        var combo = new AgentCombo(account, model);

        if (!account.Enabled)
        {
            return new ComboAvailability(combo, false, ComboSkipReason.AccountDisabled, null, null, null);
        }

        var windowOut = !model.IsInFreeWindow(now);
        DateTimeOffset? nextWindow = windowOut ? model.NextWindowStart(now) : null;

        var cooling = _cooldowns.IsCooling(account.Id, model.Name, now, out var resetAtValue);
        DateTimeOffset? resetAt = cooling ? resetAtValue : null;

        var available = !windowOut && !cooling;

        // 恢复时间点 = max(下一免费时段开始, 额度重置时间)（REQ-004c）
        DateTimeOffset? restoreAt = null;
        if (!available)
        {
            var candidates = new List<DateTimeOffset>();
            if (nextWindow.HasValue)
            {
                candidates.Add(nextWindow.Value);
            }

            if (resetAt.HasValue)
            {
                candidates.Add(resetAt.Value);
            }

            restoreAt = candidates.Count > 0 ? candidates.Max() : now;
        }

        var reason = available
            ? ComboSkipReason.None
            : (!nextWindow.HasValue || (resetAt.HasValue && resetAt.Value >= nextWindow.Value)
                ? ComboSkipReason.Cooldown
                : ComboSkipReason.OutsideFreeWindow);

        return new ComboAvailability(
            combo,
            available,
            reason,
            restoreAt,
            nextWindow,
            resetAt);
    }
}
