using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>「账号 × 模型」组合。模型项取自该账号自有候选池（v1.12），时段/单价随组合独立。</summary>
public sealed record AgentCombo(AccountConfig Account, ModelCandidate Model)
{
    public string Key => $"{Account.Id}::{Model.Name}";

    public override string ToString() => $"{Account.Label} + {Model.Name}";
}

/// <summary>一次选点结果。</summary>
public sealed class ComboSelection
{
    public AgentCombo? Combo { get; init; }

    /// <summary>本次决策中被跳过的组合及原因（用于埋点/toast/报告）。</summary>
    public IReadOnlyList<ComboAvailability> Skipped { get; init; } = Array.Empty<ComboAvailability>();

    public bool Found => Combo is not null;
}

/// <summary>最早可恢复的组合信息（waiting 目标）。</summary>
public sealed record RestoreCandidate(AgentCombo Combo, DateTimeOffset RestoreAt, WaitKind WaitKind);

/// <summary>
/// 降级矩阵：各启用账号 × 其<b>自有</b>候选模型池的有序遍历（REQ-004，v1.12）。
/// 账号未配置自有池时回退全局共享列表（向后兼容）；同名模型在不同账号下是不同组合。
/// </summary>
public sealed class DegradationMatrix
{
    private readonly List<AgentCombo> _ordered;

    public DegradationMatrix(
        IEnumerable<AccountConfig> accounts,
        IEnumerable<ModelCandidate> globalModelPool,
        DegradationStrategy strategy = DegradationStrategy.ModelFirst)
    {
        Strategy = strategy;
        Accounts = accounts.Where(a => a is not null).ToList();
        GlobalPool = globalModelPool.Where(m => m is not null).ToList();

        var combos = new List<AgentCombo>();
        // 每个账号生效的候选池：自有池优先，未配置（null 或空）回退全局共享列表
        var pools = Accounts.ToDictionary(
            a => a,
            a => a.ModelCandidates is { Count: > 0 } own ? own.ToList() : GlobalPool.ToList());

        if (Strategy == DegradationStrategy.ModelFirst)
        {
            // 模型优先（默认）：按账号顺序，逐账号遍历其自有候选池
            foreach (var a in Accounts)
            {
                foreach (var m in pools[a])
                {
                    combos.Add(new AgentCombo(a, m));
                }
            }
        }
        else
        {
            // 账号优先：按「模型名」聚合——同一模型名先在所有配置了它的账号上轮完，再换下一个模型名
            //（模型名按各账号候选池中的首次出现顺序；组合使用该账号自己的模型项，时段/单价独立）
            var namesInOrder = new List<string>();
            foreach (var a in Accounts)
            {
                foreach (var m in pools[a])
                {
                    if (!namesInOrder.Contains(m.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        namesInOrder.Add(m.Name);
                    }
                }
            }

            foreach (var name in namesInOrder)
            {
                foreach (var a in Accounts)
                {
                    var m = pools[a].FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (m is not null)
                    {
                        combos.Add(new AgentCombo(a, m));
                    }
                }
            }
        }

        _ordered = combos;
    }

    public DegradationStrategy Strategy { get; }

    public IReadOnlyList<AccountConfig> Accounts { get; }

    /// <summary>全局共享候选模型列表（仅对未配置自有池的账号生效，v1.12 兼容字段）。</summary>
    public IReadOnlyList<ModelCandidate> GlobalPool { get; }

    /// <summary>全组合遍历顺序。</summary>
    public IReadOnlyList<AgentCombo> Ordered => _ordered;

    /// <summary>首选组合（首选账号 + 首选模型）。</summary>
    public AgentCombo? Preferred => _ordered.Count > 0 ? _ordered[0] : null;

    public bool IsEmpty => _ordered.Count == 0;

    /// <summary>
    /// 从 <paramref name="from"/> 之后寻找第一个可用组合；找不到则回到序列头部再找一轮
    /// （避免矩阵后段耗尽但前段已恢复时过早 blocked）。
    /// </summary>
    public ComboSelection SelectNext(AgentCombo? from, DateTimeOffset now, AvailabilityEvaluator evaluator)
    {
        var skipped = new List<ComboAvailability>();
        if (_ordered.Count == 0)
        {
            return new ComboSelection { Skipped = skipped };
        }

        var start = 0;
        if (from is not null)
        {
            var idx = _ordered.FindIndex(c => string.Equals(c.Key, from.Key, StringComparison.OrdinalIgnoreCase));
            start = idx >= 0 ? idx + 1 : 0;
        }

        for (var i = 0; i < _ordered.Count; i++)
        {
            var combo = _ordered[(start + i) % _ordered.Count];
            var availability = evaluator.Evaluate(combo, now);
            if (availability.Available)
            {
                return new ComboSelection { Combo = combo, Skipped = skipped };
            }

            skipped.Add(availability);
        }

        return new ComboSelection { Skipped = skipped };
    }

    /// <summary>首选可用组合（新任务起点）。</summary>
    public ComboSelection SelectFirst(DateTimeOffset now, AvailabilityEvaluator evaluator) =>
        SelectNext(null, now, evaluator);

    /// <summary>
    /// 全部组合中最早的恢复时间点；并列时按遍历顺序取第一个（REQ-003b）。
    /// </summary>
    public RestoreCandidate? EarliestRestore(DateTimeOffset now, AvailabilityEvaluator evaluator)
    {
        RestoreCandidate? best = null;
        foreach (var combo in _ordered)
        {
            var a = evaluator.Evaluate(combo, now);
            if (a.Available || a.RestoreAt is null)
            {
                continue;
            }

            // 严格小于：并列时保持先遍历到的组合（= 遍历顺序在前者，REQ-003b）
            if (best is null || a.RestoreAt.Value < best.RestoreAt)
            {
                best = new RestoreCandidate(combo, a.RestoreAt.Value, a.WaitKind);
            }
        }

        return best;
    }

    /// <summary>是否所有组合当前都不可用。</summary>
    public bool AllUnavailable(DateTimeOffset now, AvailabilityEvaluator evaluator) =>
        _ordered.All(c => !evaluator.Evaluate(c, now).Available);

    /// <summary>
    /// 是否「所有组合均因额度耗尽（冷却）而不可用」：任一组合可用、或因账号停用 / 免费时段外（含混合）而不可用的，均不算。
    /// 仅此情形才对额度重置无限等待（DESIGN-额度耗尽暂停续跑）：其它阻塞原因不会因额度重置而消失，无限等待无意义。
    /// </summary>
    public bool AllUnavailablePurelyByQuota(DateTimeOffset now, AvailabilityEvaluator evaluator)
    {
        if (_ordered.Count == 0)
        {
            return false;
        }

        foreach (var combo in _ordered)
        {
            var a = evaluator.Evaluate(combo, now);
            if (a.Available)
            {
                return false;
            }

            if (a.Reason == ComboSkipReason.AccountDisabled)
            {
                return false;
            }

            // 任何免费时段外（含时段+额度混合）都不属于「纯额度耗尽」
            if (a.NextWindowStart.HasValue)
            {
                return false;
            }
        }

        return true;
    }
}
