using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Engine;

/// <summary>
/// 兜底选取策略对象（A-2 / P-03）：封装「兜底」判定与选取路由。
/// 纯函数式地聚合「是否已投入执行 + 兜底开关」的准入判定与「全池 / 指定账号」的选取路由，
/// 具体成本截断与累计由 <see cref="IFallbackBilling"/> 持有（P0-1 / P0-5 不变量），本策略对象不重复其逻辑。
/// </summary>
public sealed class FallbackPolicy
{
    private readonly IFallbackBilling _billing;

    /// <summary>构造兜底选取策略。</summary>
    /// <param name="billing">兜底计费协作者（选取 / 截断 / 累计的实际持有者）。</param>
    public FallbackPolicy(IFallbackBilling billing) => _billing = billing;

    /// <summary>兜底能力是否启用（配置开关透传）。</summary>
    public bool Enabled => _billing.Enabled;

    /// <summary>已投入执行的当前任务才允许兜底（REQ-004b）：<c>invested &amp;&amp; 兜底启用</c>。</summary>
    /// <param name="invested">当前任务是否已投入执行（已产生成本或已开始调用）。</param>
    public bool ShouldAttempt(bool invested) => invested && _billing.Enabled;

    /// <summary>全池兜底选取（按账号顺序 × 该账号兜底池顺序）。</summary>
    /// <param name="taskId">任务标识（用于单任务成本上限判定）。</param>
    public FallbackSelection? Select(string taskId) => _billing.TrySelect(taskId);

    /// <summary>仅指定账号兜底池选取（REQ-021 并发冻结任务，不跨账号）。</summary>
    /// <param name="taskId">任务标识（用于单任务成本上限判定）。</param>
    /// <param name="accountId">限定账号标识，兜底选取不得跨出该账号。</param>
    public FallbackSelection? SelectForAccount(string taskId, string accountId) =>
        _billing.TrySelectForAccount(taskId, accountId);

    /// <summary>P0-1 不变量：按单任务成本上限截断（委托 <see cref="IFallbackBilling.CapCost"/>）。</summary>
    /// <param name="cost">本次兜底调用成本，超过单任务上限时被截断。</param>
    public decimal CapCost(decimal cost) => _billing.CapCost(cost);
}
