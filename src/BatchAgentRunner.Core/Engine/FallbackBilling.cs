using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Engine;

/// <summary>兜底组合选取结果（账号 × 兜底模型 + 单价）。</summary>
public sealed record FallbackSelection(AccountConfig Account, FallbackModel Fallback, decimal UnitPrice)
{
    /// <summary>兜底模型不参与免费时段过滤（R-3）：FreeTimeWindow 恒为 null。</summary>
    public ModelCandidate ToModelCandidate() => new()
    {
        Name = Fallback.Name,
        FreeTimeWindow = null,
        EstimatedUnitPrice = Fallback.EstimatedUnitPrice
    };
}

/// <summary>
/// 兜底模型选择与计费（P1-1 拆分产物 ③，§3.4 <c>IFallbackBilling</c>）：
/// 从 <see cref="BatchEngine"/> 抽出的自包含协作者，独占兜底成本/用量状态，
/// 承载 P0-1（单任务成本按 <c>MaxCostPerTask</c> 真实截断）与
/// P0-5（跨任务「检查上限 → 累加」check-then-act 同锁）两条成本不变量。
/// 事件经构造期注入的 <see cref="Action{T}"/> 广播，本类型不依赖引擎，可独立单测。
/// </summary>
public interface IFallbackBilling
{
    /// <summary>兜底能力是否启用（配置开关）。</summary>
    bool Enabled { get; }

    /// <summary>
    /// 按「账号顺序 × 该账号兜底池顺序」选取第一个可用兜底组合（账号仅需启用）。
    /// 未启用 / 无兜底池 / 已被上限停用（P0-5）时返回 null；停用原因以 Warning 事件广播。
    /// </summary>
    FallbackSelection? TrySelect(string taskId);

    /// <summary>
    /// 仅从指定账号的兜底池选取（REQ-021）：并发冻结任务的兜底范围被约束为「原启动账号自身的兜底池、
    /// 不跨账号」——否则将违背「不切换 Live 凭证」，造成并行下账号混用。其余上限/停用逻辑同 <see cref="TrySelect"/>。
    /// </summary>
    FallbackSelection? TrySelectForAccount(string taskId, string accountId);

    /// <summary>P0-1 不变量：按 <c>Fallback.MaxCostPerTask</c> 截断实测/估算成本（原 no-op 恒等式已修复）。</summary>
    decimal CapCost(decimal cost);

    /// <summary>记录本次使用的兜底模型名（去重，供报告展示）。</summary>
    void RecordModelUsed(string modelName);

    /// <summary>兜底成功：累加成本与用量（P0-5 同锁 check-then-act）。</summary>
    void RecordSuccess(decimal cost, CostBasis basis);

    /// <summary>兜底失败：仅累加成本（不增用量）。</summary>
    void RecordFailure(decimal cost, CostBasis basis);

    /// <summary>汇总本批兜底用量（报告用；内部同锁读取）。</summary>
    FallbackSummary BuildSummary();
}

/// <summary>默认实现：状态随实例走（原为 BatchEngine 私有字段 + _fallbackLock）。</summary>
public sealed class FallbackBilling : IFallbackBilling
{
    private readonly RunOptions _options;
    private readonly Func<DegradationMatrix> _matrixProvider;
    private readonly IClock _clock;
    private readonly Action<BatchEvent> _raise;
    private readonly object _lock = new();
    private decimal _batchCost;
    private int _usedCount;
    private CostBasis _costBasis = CostBasis.Estimated;
    private bool _stoppedByLimit;
    private readonly List<string> _modelsUsed = new();

    /// <summary>构造兜底计费协作者（成本 / 用量状态随实例走）。</summary>
    public FallbackBilling(RunOptions options, Func<DegradationMatrix> matrixProvider,
        IClock clock, Action<BatchEvent> raise)
    {
        _options = options;
        _matrixProvider = matrixProvider;
        _clock = clock;
        _raise = raise;
    }

    /// <summary>兜底能力是否启用（透传配置开关，未启用时各选取方法直接返回 null）。</summary>
    /// <inheritdoc />
    public bool Enabled => _options.Fallback.Enabled;

    /// <summary>全池兜底选取：按账号顺序 × 兜底池顺序取首个可用项，超限则告警并停用。</summary>
    /// <inheritdoc />
    public FallbackSelection? TrySelect(string taskId)
    {
        if (!_options.Fallback.Enabled)
        {
            return null;
        }

        // 兜底模型池挂在账号下（REQ-004b，v1.12）：
        // 按「账号顺序 × 该账号兜底池顺序」选取第一个可用兜底组合；账号仅需启用。
        var (account, fallback) = _matrixProvider().Accounts
            .Where(a => a.Enabled)
            .SelectMany(a => (a.FallbackModels ?? new List<FallbackModel>()).Select(fb => (Account: a, Fallback: fb)))
            .FirstOrDefault();

        if (fallback is null)
        {
            // 没有任何启用账号配置兜底模型池 → 视为未具备兜底能力（REQ-004b）
            return null;
        }

        var unitPrice = fallback.EstimatedUnitPrice ?? 0m;

        // P0-5：兜底成本/用量是跨任务共享状态（Concurrency>1 时并发累加），且「检查上限 → 累加」
        // 属 check-then-act，必须与累加共用同一把锁；事件广播在锁外进行（避免持锁调用订阅方）。
        string? limitReason = null;
        lock (_lock)
        {
            if (_batchCost + unitPrice > _options.Fallback.MaxCostPerBatch)
            {
                limitReason = $"兜底成本已用尽（单批上限 ¥{_options.Fallback.MaxCostPerBatch}），已停用兜底并转为阻塞";
            }
            else if (unitPrice > _options.Fallback.MaxCostPerTask)
            {
                limitReason = $"兜底单价 ¥{unitPrice} 超出单任务上限 ¥{_options.Fallback.MaxCostPerTask}，已跳过兜底";
            }

            if (limitReason is not null)
            {
                _stoppedByLimit = true;
            }
        }

        if (limitReason is not null)
        {
            _raise(new BatchEvent(BatchEventKind.Warning, _clock.Now, limitReason, taskId));
            return null;
        }

        return new FallbackSelection(account, fallback, unitPrice);
    }

    /// <summary>限定账号兜底选取（REQ-021）：只在原启动账号自己的兜底池内挑，不跨账号。</summary>
    /// <inheritdoc />
    public FallbackSelection? TrySelectForAccount(string taskId, string accountId)
    {
        if (!_options.Fallback.Enabled)
        {
            return null;
        }

        // REQ-021：仅从「原启动账号自身」的兜底池选取，不得跨账号。
        var (account, fallback) = _matrixProvider().Accounts
            .Where(a => a.Enabled && string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => (a.FallbackModels ?? new List<FallbackModel>()).Select(fb => (Account: a, Fallback: fb)))
            .FirstOrDefault();

        if (fallback is null)
        {
            return null;
        }

        var unitPrice = fallback.EstimatedUnitPrice ?? 0m;

        string? limitReason = null;
        lock (_lock)
        {
            if (_batchCost + unitPrice > _options.Fallback.MaxCostPerBatch)
            {
                limitReason = $"兜底成本已用尽（单批上限 ¥{_options.Fallback.MaxCostPerBatch}），已停用兜底并转为阻塞";
            }
            else if (unitPrice > _options.Fallback.MaxCostPerTask)
            {
                limitReason = $"兜底单价 ¥{unitPrice} 超出单任务上限 ¥{_options.Fallback.MaxCostPerTask}，已跳过兜底";
            }

            if (limitReason is not null)
            {
                _stoppedByLimit = true;
            }
        }

        if (limitReason is not null)
        {
            _raise(new BatchEvent(BatchEventKind.Warning, _clock.Now, limitReason, taskId));
            return null;
        }

        return new FallbackSelection(account, fallback, unitPrice);
    }

    /// <summary>P0-1 不变量：按单任务成本上限截断，保证兜底记账不越界。</summary>
    /// <param name="cost">待截断的实测或估算成本，负值按 0 处理。</param>
    /// <inheritdoc />
    public decimal CapCost(decimal cost) => Math.Min(cost, Math.Max(0m, _options.Fallback.MaxCostPerTask));

    /// <summary>登记本次用到的兜底模型名（同锁去重，供报告展示）。</summary>
    /// <inheritdoc />
    public void RecordModelUsed(string modelName)
    {
        lock (_lock)
        {
            if (!_modelsUsed.Contains(modelName, StringComparer.Ordinal))
            {
                _modelsUsed.Add(modelName);
            }
        }
    }

    /// <summary>兜底成功：累加批成本与用量计数（同锁，供上限判定）。</summary>
    /// <inheritdoc />
    public void RecordSuccess(decimal cost, CostBasis basis)
    {
        lock (_lock)
        {
            _batchCost += cost;
            _usedCount++;
            _costBasis = basis;
        }
    }

    /// <summary>兜底失败：仅累加成本，不增用量计数。</summary>
    /// <inheritdoc />
    public void RecordFailure(decimal cost, CostBasis basis)
    {
        lock (_lock)
        {
            _batchCost += cost;
            _costBasis = basis;
        }
    }

    /// <inheritdoc />
    public FallbackSummary BuildSummary()
    {
        lock (_lock)
        {
            return new FallbackSummary
            {
                UsedCount = _usedCount,
                Model = _modelsUsed.Count > 0 ? string.Join("、", _modelsUsed) : null,
                TotalCost = _batchCost,
                CostBasis = _costBasis,
                StoppedByLimit = _stoppedByLimit
            };
        }
    }
}
