using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using Xunit;

namespace BatchAgentRunner.Tests.Scheduling;

public class DegradationTests
{
    private static AccountConfig Acc(string id) => new() { Id = id, DisplayName = id };

    private static ModelCandidate Model(string name, FreeTimeWindow? window = null) =>
        new() { Name = name, FreeTimeWindow = window };

    [Fact]
    public void 模型优先_同账号先轮完候选模型再换账号()
    {
        var accounts = new[] { Acc("acc1"), Acc("acc2") };
        var models = new[] { Model("m1"), Model("m2") };
        var matrix = new DegradationMatrix(accounts, models, DegradationStrategy.ModelFirst);

        Assert.Equal(
            new[] { "acc1::m1", "acc1::m2", "acc2::m1", "acc2::m2" },
            matrix.Ordered.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void 账号优先_先轮完账号池再换模型()
    {
        var accounts = new[] { Acc("acc1"), Acc("acc2") };
        var models = new[] { Model("m1"), Model("m2") };
        var matrix = new DegradationMatrix(accounts, models, DegradationStrategy.AccountFirst);

        Assert.Equal(
            new[] { "acc1::m1", "acc2::m1", "acc1::m2", "acc2::m2" },
            matrix.Ordered.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void 账号自有候选池_ModelFirst按各账号自有池展开()
    {
        var acc1 = Acc("acc1");
        acc1.ModelCandidates = new List<ModelCandidate> { Model("m1"), Model("m2") };
        var acc2 = Acc("acc2");
        acc2.ModelCandidates = new List<ModelCandidate> { Model("m3") };

        // 全局列表存在但两个账号都配了自有池 → 全局列表不参与
        var matrix = new DegradationMatrix(new[] { acc1, acc2 }, new[] { Model("g1") }, DegradationStrategy.ModelFirst);

        Assert.Equal(
            new[] { "acc1::m1", "acc1::m2", "acc2::m3" },
            matrix.Ordered.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void 未配置自有池的账号回退全局共享列表()
    {
        var acc1 = Acc("acc1");
        acc1.ModelCandidates = new List<ModelCandidate> { Model("m1") };
        var matrix = new DegradationMatrix(new[] { acc1, Acc("acc2") }, new[] { Model("g1"), Model("g2") });

        Assert.Equal(
            new[] { "acc1::m1", "acc2::g1", "acc2::g2" },
            matrix.Ordered.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void 账号优先_按模型名聚合_组合使用该账号自己的模型项()
    {
        var own1 = Model("m1", new FreeTimeWindow(new TimeOnly(0, 0), new TimeOnly(8, 0)));
        var acc1 = Acc("acc1");
        acc1.ModelCandidates = new List<ModelCandidate> { own1, Model("m2") };
        var own2 = Model("m1"); // 同名模型、不同账号、无时段
        var acc2 = Acc("acc2");
        acc2.ModelCandidates = new List<ModelCandidate> { own2 };

        var matrix = new DegradationMatrix(new[] { acc1, acc2 }, Array.Empty<ModelCandidate>(),
            DegradationStrategy.AccountFirst);

        // m1 先在所有配置了它的账号上轮完（按账号顺序），再换 m2
        Assert.Equal(new[] { "acc1::m1", "acc2::m1", "acc1::m2" }, matrix.Ordered.Select(c => c.Key).ToArray());
        // 组合携带的是该账号自己的模型项（时段/单价独立）
        Assert.Same(own2, matrix.Ordered[1].Model);
    }

    [Fact]
    public void 同名模型不同账号时段独立_时段外只跳过对应账号的组合()
    {
        var clock = new FakeClock(At.Local(2026, 10, 15, 10, 0));
        var evaluator = new AvailabilityEvaluator(new QuotaCooldownRegistry(clock));

        // A 账号 Hy4 仅 0:00–8:00 免费；B 账号 Hy4 全天免费
        var accA = Acc("accA");
        accA.ModelCandidates = new List<ModelCandidate>
        {
            Model("Hy4", new FreeTimeWindow(new TimeOnly(0, 0), new TimeOnly(8, 0)))
        };
        var accB = Acc("accB");
        accB.ModelCandidates = new List<ModelCandidate> { Model("Hy4") };

        var matrix = new DegradationMatrix(new[] { accA, accB }, Array.Empty<ModelCandidate>());

        var selection = matrix.SelectFirst(clock.Now, evaluator);

        Assert.True(selection.Found);
        Assert.Equal("accB::Hy4", selection.Combo!.Key);
        Assert.Contains(selection.Skipped,
            s => s.Reason == ComboSkipReason.OutsideFreeWindow && s.Combo.Key == "accA::Hy4");
    }

    [Fact]
    public void 选点_跳过时段外组合_取下一个可用()
    {
        var clock = new FakeClock(At.Local(2026, 10, 15, 10, 0));
        var registry = new QuotaCooldownRegistry(clock, TimeSpan.FromMinutes(60));
        var evaluator = new AvailabilityEvaluator(registry);

        // m1 仅在 22:00–06:00 可用 → 当前时段外
        var matrix = new DegradationMatrix(
            new[] { Acc("acc1") },
            new[] { Model("m1", new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0))), Model("m2") },
            DegradationStrategy.ModelFirst);

        var selection = matrix.SelectFirst(clock.Now, evaluator);

        Assert.True(selection.Found);
        Assert.Equal("m2", selection.Combo!.Model.Name);
        Assert.Contains(selection.Skipped,
            s => s.Reason == ComboSkipReason.OutsideFreeWindow && s.Combo.Model.Name == "m1");
    }

    [Fact]
    public void 选点_跳过冷却中组合_且冷却跳过不计尝试()
    {
        var now = At.Local(2026, 10, 15, 10, 0);
        var clock = new FakeClock(now);
        var registry = new QuotaCooldownRegistry(clock, TimeSpan.FromMinutes(60));
        registry.Record("acc1", "m1", now.AddMinutes(30), now);

        var evaluator = new AvailabilityEvaluator(registry);
        var matrix = new DegradationMatrix(new[] { Acc("acc1") }, new[] { Model("m1"), Model("m2") });

        var selection = matrix.SelectFirst(now, evaluator);

        Assert.True(selection.Found);
        Assert.Equal("m2", selection.Combo!.Model.Name);
        Assert.Contains(selection.Skipped, s => s.Reason == ComboSkipReason.Cooldown);
    }

    [Fact]
    public void 选点_停用的账号被跳过()
    {
        var now = At.Local(2026, 10, 15, 10, 0);
        var clock = new FakeClock(now);
        var evaluator = new AvailabilityEvaluator(new QuotaCooldownRegistry(clock));
        var acc1 = new AccountConfig { Id = "acc1", Enabled = false };
        var matrix = new DegradationMatrix(new[] { acc1, Acc("acc2") }, new[] { Model("m1") });

        var selection = matrix.SelectFirst(now, evaluator);

        Assert.True(selection.Found);
        Assert.Equal("acc2", selection.Combo!.Account.Id);
        Assert.Contains(selection.Skipped, s => s.Reason == ComboSkipReason.AccountDisabled);
    }

    [Fact]
    public void 全部不可用时_最早恢复时间取max_重置时间与下一时段开始()
    {
        var now = At.Local(2026, 10, 15, 10, 0);
        var clock = new FakeClock(now);
        var registry = new QuotaCooldownRegistry(clock, TimeSpan.FromMinutes(60));
        // 组合同时处于时段外（下一时段 22:00）与冷却中（14:30）→ 恢复时间应为 22:00
        registry.Record("acc1", "m1", now.AddHours(4.5), now);

        var evaluator = new AvailabilityEvaluator(registry);
        var matrix = new DegradationMatrix(
            new[] { Acc("acc1") },
            new[] { Model("m1", new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0))) });

        var availability = evaluator.Evaluate(matrix.Ordered[0], now);

        Assert.False(availability.Available);
        Assert.Equal(new TimeSpan(22, 0, 0), availability.RestoreAt!.Value.LocalDateTime.TimeOfDay);
        Assert.Equal(WaitKind.Mixed, availability.WaitKind);
    }

    [Fact]
    public void 冷却记录_重置时间早于当前时降级为默认冷却()
    {
        var now = At.Local(2026, 10, 15, 10, 0);
        var registry = new QuotaCooldownRegistry(new FakeClock(now), TimeSpan.FromMinutes(60));

        var record = registry.Record("acc1", "m1", now.AddMinutes(-10), now);

        Assert.Equal(CooldownSource.DefaultFallback, record.Source);
        Assert.Equal(now.AddMinutes(60), record.ResetAt);
    }

    [Fact]
    public void 冷却记录_恢复后再次耗尽以新记录覆盖()
    {
        var now = At.Local(2026, 10, 15, 10, 0);
        var registry = new QuotaCooldownRegistry(new FakeClock(now), TimeSpan.FromMinutes(60));

        registry.Record("acc1", "m1", now.AddHours(1), now);
        registry.Record("acc1", "m1", now.AddHours(3), now);

        Assert.True(registry.IsCooling("acc1", "m1", now.AddHours(2), out var resetAt));
        Assert.Equal(now.AddHours(3), resetAt);
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void 冷却清理_过期记录可被清除()
    {
        var now = At.Local(2026, 10, 15, 10, 0);
        var registry = new QuotaCooldownRegistry(new FakeClock(now), TimeSpan.FromMinutes(60));
        registry.Record("acc1", "m1", now.AddMinutes(5), now);

        Assert.Equal(0, registry.PurgeExpired(now.AddMinutes(1)));
        Assert.Equal(1, registry.PurgeExpired(now.AddMinutes(10)));
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void 最早恢复候选_并列时按遍历顺序取第一个()
    {
        var now = At.Local(2026, 10, 15, 10, 0);
        var registry = new QuotaCooldownRegistry(new FakeClock(now), TimeSpan.FromMinutes(60));
        registry.Record("acc1", "m1", now.AddMinutes(20), now);
        registry.Record("acc1", "m2", now.AddMinutes(20), now);

        var evaluator = new AvailabilityEvaluator(registry);
        var matrix = new DegradationMatrix(new[] { Acc("acc1") }, new[] { Model("m1"), Model("m2") });

        var earliest = matrix.EarliestRestore(now, evaluator);

        Assert.NotNull(earliest);
        Assert.Equal("m1", earliest!.Combo.Model.Name);
        Assert.Equal(WaitKind.QuotaReset, earliest.WaitKind);
    }
}
