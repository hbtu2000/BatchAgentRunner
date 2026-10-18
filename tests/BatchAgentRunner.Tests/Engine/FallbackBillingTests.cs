using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using Xunit;

namespace BatchAgentRunner.Tests.Engine;
/// <summary>
/// 兜底计费协作者单测（P1-1 拆分产物 ③）。原 P0-1/P0-5 不变量只有 BatchEngine 黑箱测试，
/// 拆分后可直接对 <see cref="FallbackBilling"/> 白箱断言，把「一眼看不出是错的」成本规则锁死在协作者契约上。
/// </summary>
[Trait("Category", "Invariant")]
public class FallbackBillingTests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

    private static RunOptions OptionsWithFallback(decimal maxPerTask = 1m, decimal maxPerBatch = 5m)
    {
        var options = new RunOptions
        {
            AgentKind = AgentKind.Mock,
            Fallback = new FallbackConfig
            {
                Enabled = true,
                MaxCostPerTask = maxPerTask,
                MaxCostPerBatch = maxPerBatch
            },
            Accounts = new List<AccountConfig>
            {
                new()
                {
                    Id = "acc1",
                    DisplayName = "账号 1",
                    FallbackModels = new List<FallbackModel>
                    {
                        new() { Name = "paid-x", EstimatedUnitPrice = 0.2m },
                        new() { Name = "paid-y", EstimatedUnitPrice = 0.3m }
                    }
                }
            },
            ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" } }
        };
        return options;
    }

    private static (FallbackBilling Billing, List<BatchEvent> Events) Build(RunOptions options)
    {
        var events = new List<BatchEvent>();
        var clock = new FakeClock(Noon);
        var billing = new FallbackBilling(options,
            () => new DegradationMatrix(options.Accounts, options.ModelCandidates),
            clock, events.Add);
        return (billing, events);
    }

    [Fact]
    public void 未启用兜底_选取返回null()
    {
        var options = OptionsWithFallback();
        options.Fallback.Enabled = false;
        var (billing, _) = Build(options);

        Assert.Null(billing.TrySelect("task-001"));
    }

    [Fact]
    public void 按账号顺序与账号内兜底池顺序选取第一个组合()
    {
        var (billing, _) = Build(OptionsWithFallback());

        var selection = billing.TrySelect("task-001");

        Assert.NotNull(selection);
        Assert.Equal("acc1", selection!.Account.Id);
        Assert.Equal("paid-x", selection.Fallback.Name);
        Assert.Equal(0.2m, selection.UnitPrice);
    }

    [Fact]
    public void 无兜底池_选取返回null()
    {
        var options = OptionsWithFallback();
        options.Accounts[0].FallbackModels = new List<FallbackModel>();
        var (billing, _) = Build(options);

        Assert.Null(billing.TrySelect("task-001"));
    }

    /// <summary>P0-1 不变量：计入累计与任务成本的实测值必须 ≤ MaxCostPerTask。</summary>
    [Fact]
    public void 实测成本超单任务上限_按上限截断()
    {
        var (billing, _) = Build(OptionsWithFallback(maxPerTask: 1m));

        Assert.Equal(1m, billing.CapCost(3.5m));
        Assert.Equal(0.2m, billing.CapCost(0.2m)); // 未超上限时原样通过
        Assert.Equal(-1m, billing.CapCost(-1m)); // 上界钳制（Math.Max(0, cap)），负值原样穿过（MockStep 成本非负，不会出现）
    }

    [Fact]
    public void 截断后的成本才进入累计()
    {
        var (billing, _) = Build(OptionsWithFallback(maxPerTask: 1m, maxPerBatch: 5m));
        billing.RecordSuccess(billing.CapCost(3.5m), CostBasis.Measured);

        var summary = billing.BuildSummary();

        Assert.Equal(1, summary.UsedCount);
        Assert.Equal(1m, summary.TotalCost);
        Assert.Equal(CostBasis.Measured, summary.CostBasis);
    }

    /// <summary>P0-5 不变量：单批上限耗尽后停用兜底并广播原因（check-then-act 同锁）。</summary>
    [Fact]
    public void 单批上限将尽_停用兜底并广播告警()
    {
        var (billing, events) = Build(OptionsWithFallback(maxPerTask: 1m, maxPerBatch: 1m));
        billing.RecordSuccess(1m, CostBasis.Estimated);

        Assert.Null(billing.TrySelect("task-002"));
        Assert.True(billing.BuildSummary().StoppedByLimit);
        Assert.Contains(events, e => e.Kind == BatchEventKind.Warning && e.TaskId == "task-002");
    }

    /// <summary>P0-5 不变量：单价本身超单任务上限时直接跳过（未执行也不累计）。</summary>
    [Fact]
    public void 兜底单价超单任务上限_跳过且不累计()
    {
        var options = OptionsWithFallback(maxPerTask: 0.1m, maxPerBatch: 5m);
        var (billing, events) = Build(options);

        Assert.Null(billing.TrySelect("task-001"));
        Assert.True(billing.BuildSummary().StoppedByLimit);
        Assert.Equal(0m, billing.BuildSummary().TotalCost);
        Assert.Contains(events, e => e.Kind == BatchEventKind.Warning);
    }

    [Fact]
    public void 失败也累计成本但不增用量()
    {
        var (billing, _) = Build(OptionsWithFallback());
        billing.RecordFailure(0.2m, CostBasis.Estimated);

        var summary = billing.BuildSummary();

        Assert.Equal(0, summary.UsedCount);
        Assert.Equal(0.2m, summary.TotalCost);
    }

    [Fact]
    public void 使用的模型名去重后拼接展示()
    {
        var (billing, _) = Build(OptionsWithFallback());
        billing.RecordModelUsed("paid-x");
        billing.RecordModelUsed("paid-x");
        billing.RecordModelUsed("paid-y");

        Assert.Equal("paid-x、paid-y", billing.BuildSummary().Model);
    }

    /// <summary>P0-5 不变量：并发下 RecordSuccess 累计必须准确（原无锁累加会丢更新）。</summary>
    [Fact]
    public void 并发累计_成本与用量准确()
    {
        var (billing, _) = Build(OptionsWithFallback(maxPerTask: 1m, maxPerBatch: 100m));

        Parallel.For(0, 64, _ => billing.RecordSuccess(0.2m, CostBasis.Estimated));

        var summary = billing.BuildSummary();
        Assert.Equal(64, summary.UsedCount);
        Assert.Equal(12.8m, summary.TotalCost);
    }
}
