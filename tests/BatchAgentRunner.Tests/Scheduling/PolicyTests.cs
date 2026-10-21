using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Tests.Scheduling;

/// <summary>
/// P-03 纯函数式策略对象单测：<see cref="DegradationPolicy"/> / <see cref="CooldownPolicy"/> /
/// <see cref="FreeTimeWindowPolicy"/> / <see cref="FallbackPolicy"/> 四类决策层契约。
/// 它们从 <c>TaskRunner</c> 拆出后独立于状态机承载降级 / 冷却 / 兜底 / 免费时段判定，
/// 此处把「一眼看不出是错的」决策分支锁死在策略契约上（§3.3 纯函数式策略对象）。
/// 矩阵/评估器等底层遍历已在 <see cref="DegradationTests"/> 覆盖，本文件只断言「决策层」输出。
/// </summary>
[Trait("Category", "Invariant")]
public class PolicyTests
{
    // ---------------- 公共构造辅助 ----------------

    private static RunOptions BaseOptions() => new()
    {
        AgentKind = AgentKind.Mock,
        RateLimitBackoffRetries = 1,
        MaxRetriesPerTask = 1,
        StopOnFailure = false,
        TaskTimeoutMinutes = 10,
        MaxWaitMinutes = 120,
        WaitForQuotaReset = true,
        Accounts = new List<AccountConfig> { new() { Id = "acc1", DisplayName = "账号 1" } },
        ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" }, new() { Name = "m2" } }
    };

    /// <summary>构建矩阵 + 评估器：按 (账号, 模型, 是否冷却) 规格展开组合，冷却项重置时间视为 now+30m。</summary>
    private static (DegradationMatrix Matrix, AvailabilityEvaluator Evaluator) BuildMatrix(
        FakeClock clock, params (string Acc, string Model, bool Cool)[] spec)
    {
        var accounts = new List<AccountConfig>();
        foreach (var g in spec.GroupBy(s => s.Acc))
        {
            var a = new AccountConfig { Id = g.Key, DisplayName = g.Key };
            a.ModelCandidates = g.Select(s => new ModelCandidate { Name = s.Model }).ToList();
            accounts.Add(a);
        }

        var globalModels = spec.Select(s => new ModelCandidate { Name = s.Model })
            .DistinctBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var matrix = new DegradationMatrix(accounts, globalModels);
        var registry = new QuotaCooldownRegistry(clock, TimeSpan.FromMinutes(60));
        foreach (var (acc, model, cool) in spec)
        {
            if (cool)
            {
                registry.Record(acc, model, clock.Now.AddMinutes(30), clock.Now);
            }
        }

        return (matrix, new AvailabilityEvaluator(registry));
    }

    // =====================================================================
    // DegradationPolicy
    // =====================================================================

    public class DegradationPolicyTests
    {
        private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

        private static DegradationPolicy Build(RunOptions options, DegradationMatrix matrix, AvailabilityEvaluator evaluator) =>
            new(() => matrix, evaluator, options);

        [Fact]
        public void DecideNext_重试同一组合且仍可用_返回RetrySame()
        {
            var clock = new FakeClock(Noon);
            var (matrix, evaluator) = BuildMatrix(clock, ("acc1", "m1", false), ("acc1", "m2", false));
            var policy = Build(BaseOptions(), matrix, evaluator);
            var current = matrix.Ordered[0];

            var decision = policy.DecideNext(current, retrySameCombo: true, Noon);

            Assert.True(decision.HasSelection);
            Assert.True(decision.IsRetrySame);
            Assert.Same(current, decision.NextCombo);
        }

        [Fact]
        public void DecideNext_不重试同一组合_矩阵选择下一个可用()
        {
            var clock = new FakeClock(Noon);
            var (matrix, evaluator) = BuildMatrix(clock, ("acc1", "m1", false), ("acc1", "m2", false));
            var policy = Build(BaseOptions(), matrix, evaluator);
            var current = matrix.Ordered[0];

            var decision = policy.DecideNext(current, retrySameCombo: false, Noon);

            Assert.True(decision.HasSelection);
            Assert.False(decision.IsRetrySame);
            Assert.Equal("m2", decision.NextCombo!.Model.Name);
        }

        [Fact]
        public void DecideNext_全部不可用_返回None并携带跳过列表()
        {
            var clock = new FakeClock(Noon);
            var (matrix, evaluator) = BuildMatrix(clock, ("acc1", "m1", true), ("acc1", "m2", true));
            var policy = Build(BaseOptions(), matrix, evaluator);
            var current = matrix.Ordered[0];

            var decision = policy.DecideNext(current, retrySameCombo: false, Noon);

            Assert.False(decision.HasSelection);
            Assert.False(decision.IsRetrySame);
            Assert.Equal(2, decision.Skipped.Count);
        }

        [Fact]
        public void DecideFailure_限流连击未达上限_重试同一组合且连击加一()
        {
            var options = BaseOptions(); // RateLimitBackoffRetries = 1
            var policy = Build(options, new DegradationMatrix(options.Accounts, options.ModelCandidates), new AvailabilityEvaluator(new QuotaCooldownRegistry(new FakeClock(Noon))));

            var decision = policy.DecideFailure(new AgentFailure(AgentFailureKind.RateLimited, "429"), rateLimitStreak: 0, normalRetries: 0);

            Assert.Equal(FailureKind.RetrySame, decision.Kind);
            Assert.Equal(1, decision.RateLimitStreak);
        }

        [Fact]
        public void DecideFailure_限流连击达上限_升级为记录冷却()
        {
            var options = BaseOptions(); // RateLimitBackoffRetries = 1
            var policy = Build(options, new DegradationMatrix(options.Accounts, options.ModelCandidates), new AvailabilityEvaluator(new QuotaCooldownRegistry(new FakeClock(Noon))));

            // REQ-005：退避连击达上限 → 限流退避升级为额度耗尽式冷却（记录冷却）
            var decision = policy.DecideFailure(new AgentFailure(AgentFailureKind.RateLimited, "429"), rateLimitStreak: 1, normalRetries: 0);

            Assert.Equal(FailureKind.RecordCooldown, decision.Kind);
        }

        [Fact]
        public void DecideFailure_额度耗尽_记录冷却()
        {
            var options = BaseOptions();
            var policy = Build(options, new DegradationMatrix(options.Accounts, options.ModelCandidates), new AvailabilityEvaluator(new QuotaCooldownRegistry(new FakeClock(Noon))));

            var decision = policy.DecideFailure(new AgentFailure(AgentFailureKind.QuotaExhausted, "额度用尽"), rateLimitStreak: 0, normalRetries: 0);

            Assert.Equal(FailureKind.RecordCooldown, decision.Kind);
        }

        [Fact]
        public void DecideFailure_凭据失效_停用账号()
        {
            var options = BaseOptions();
            var policy = Build(options, new DegradationMatrix(options.Accounts, options.ModelCandidates), new AvailabilityEvaluator(new QuotaCooldownRegistry(new FakeClock(Noon))));

            var decision = policy.DecideFailure(new AgentFailure(AgentFailureKind.AuthExpired, "未登录"), rateLimitStreak: 0, normalRetries: 0);

            Assert.Equal(FailureKind.DisableAccount, decision.Kind);
        }

        [Fact]
        public void DecideFailure_危险操作_拒绝自动批准并中断()
        {
            var options = BaseOptions();
            var policy = Build(options, new DegradationMatrix(options.Accounts, options.ModelCandidates), new AvailabilityEvaluator(new QuotaCooldownRegistry(new FakeClock(Noon))));

            var decision = policy.DecideFailure(new AgentFailure(AgentFailureKind.DangerousOperation, "rm -rf"), rateLimitStreak: 0, normalRetries: 0);

            Assert.Equal(FailureKind.FailTaskDangerous, decision.Kind);
            Assert.Equal("rm -rf", decision.Message);
        }

        [Fact]
        public void DecideFailure_超时_判失败并附时长文案()
        {
            var options = BaseOptions(); // TaskTimeoutMinutes = 10
            var policy = Build(options, new DegradationMatrix(options.Accounts, options.ModelCandidates), new AvailabilityEvaluator(new QuotaCooldownRegistry(new FakeClock(Noon))));

            var decision = policy.DecideFailure(new AgentFailure(AgentFailureKind.Timeout, "超时"), rateLimitStreak: 0, normalRetries: 0);

            Assert.Equal(FailureKind.FailTask, decision.Kind);
            Assert.Contains("10", decision.Message!);
        }

        [Fact]
        public void DecideFailure_普通错误未达重试上限_重试任务且计数加一()
        {
            var options = BaseOptions(); // MaxRetriesPerTask = 1
            var policy = Build(options, new DegradationMatrix(options.Accounts, options.ModelCandidates), new AvailabilityEvaluator(new QuotaCooldownRegistry(new FakeClock(Noon))));

            var decision = policy.DecideFailure(new AgentFailure(AgentFailureKind.TaskError, "报错"), rateLimitStreak: 0, normalRetries: 0);

            Assert.Equal(FailureKind.RetryTask, decision.Kind);
            Assert.Equal(1, decision.NormalRetries);
        }

        [Fact]
        public void DecideFailure_普通错误达重试上限_失败即停开启时判失败停批()
        {
            var options = BaseOptions(); // MaxRetriesPerTask = 1
            options.StopOnFailure = true;
            var policy = Build(options, new DegradationMatrix(options.Accounts, options.ModelCandidates), new AvailabilityEvaluator(new QuotaCooldownRegistry(new FakeClock(Noon))));

            var decision = policy.DecideFailure(new AgentFailure(AgentFailureKind.TaskError, "又报错"), rateLimitStreak: 0, normalRetries: 1);

            Assert.Equal(FailureKind.FailTaskStopBatch, decision.Kind);
        }

        [Fact]
        public void DecideFailure_普通错误达重试上限_失败即停关闭时仅判失败()
        {
            var options = BaseOptions(); // MaxRetriesPerTask = 1, StopOnFailure = false
            var policy = Build(options, new DegradationMatrix(options.Accounts, options.ModelCandidates), new AvailabilityEvaluator(new QuotaCooldownRegistry(new FakeClock(Noon))));

            var decision = policy.DecideFailure(new AgentFailure(AgentFailureKind.TaskError, "又报错"), rateLimitStreak: 0, normalRetries: 1);

            Assert.Equal(FailureKind.FailTask, decision.Kind);
        }
    }

    // =====================================================================
    // CooldownPolicy
    // =====================================================================

    public class CooldownPolicyTests
    {
        private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

        [Fact]
        public void RecordAndPurge_CLI重置时间可靠_记录为CLI解析来源()
        {
            var clock = new FakeClock(Noon);
            var registry = new QuotaCooldownRegistry(clock, TimeSpan.FromMinutes(60));
            var policy = new CooldownPolicy(registry);

            var record = policy.RecordAndPurge("acc1", "m1", Noon.AddMinutes(30), Noon, "raw-cli");

            Assert.Equal(CooldownSource.CliParsed, record.Source);
            Assert.Equal(Noon.AddMinutes(30), record.ResetAt);
            Assert.True(registry.IsCooling("acc1", "m1", Noon.AddMinutes(10), out _));
        }

        [Fact]
        public void RecordAndPurge_CLI重置时间不可靠_降级默认冷却()
        {
            var clock = new FakeClock(Noon);
            var registry = new QuotaCooldownRegistry(clock, TimeSpan.FromMinutes(60));
            var policy = new CooldownPolicy(registry);

            // 重置时间早于当前 → 不可靠，降级为默认冷却（now + 60m），来源标注 DefaultFallback
            var record = policy.RecordAndPurge("acc1", "m1", Noon.AddMinutes(-10), Noon, "bad");

            Assert.Equal(CooldownSource.DefaultFallback, record.Source);
            Assert.Equal(Noon.AddMinutes(60), record.ResetAt);
        }

        [Fact]
        public void RecordAndPurge_清掉已过期记录只保留新记录()
        {
            var clock = new FakeClock(Noon);
            var registry = new QuotaCooldownRegistry(clock, TimeSpan.FromMinutes(60));
            registry.Record("acc2", "m2", Noon.AddMinutes(5), Noon); // 过期残留（相对 later now 已过期）
            var policy = new CooldownPolicy(registry);

            var later = Noon.AddMinutes(10);
            policy.RecordAndPurge("acc1", "m1", later.AddMinutes(30), later, "raw");

            var snapshot = registry.Snapshot();
            Assert.Single(snapshot);
            Assert.Equal("acc1::m1", snapshot[0].Key);
        }
    }

    // =====================================================================
    // FreeTimeWindowPolicy
    // =====================================================================

    public class FreeTimeWindowPolicyTests
    {
        private static readonly DateTimeOffset Noon = At.Local(2026, 10, 15, 12, 0);

        [Fact]
        public void DecideWait_全部纯额度耗尽且开启等待重置_无限等待()
        {
            var clock = new FakeClock(Noon);
            // 唯一组合处于冷却（无免费时段）→ 矩阵纯由额度导致不可用 → AllUnavailablePurelyByQuota = true
            var (matrix, evaluator) = BuildMatrix(clock, ("acc1", "m1", true));
            var options = BaseOptions(); // WaitForQuotaReset = true, MaxWaitMinutes = 120
            var earliest = matrix.EarliestRestore(Noon, evaluator); // QuotaReset 型，now+30
            var policy = new FreeTimeWindowPolicy(options, () => matrix, evaluator);

            var decision = policy.DecideWait(earliest, Noon);

            Assert.True(decision.ShouldWait);
            Assert.True(decision.UnboundedQuota);
        }

        [Fact]
        public void DecideWait_额度耗尽但关闭等待重置_落在窗口内仍等待但非无限()
        {
            var clock = new FakeClock(Noon);
            var (matrix, evaluator) = BuildMatrix(clock, ("acc1", "m1", true));
            var options = BaseOptions();
            options.WaitForQuotaReset = false; // 关闭无限等待
            var earliest = matrix.EarliestRestore(Noon, evaluator); // now+30
            var policy = new FreeTimeWindowPolicy(options, () => matrix, evaluator);

            var decision = policy.DecideWait(earliest, Noon);

            Assert.True(decision.ShouldWait);
            Assert.False(decision.UnboundedQuota); // 非无限等待，但 30m ≤ 120m 窗口 → 仍等待
        }

        [Fact]
        public void DecideWait_无任何可用候选_直接阻塞()
        {
            var clock = new FakeClock(Noon);
            var (matrix, evaluator) = BuildMatrix(clock, ("acc1", "m1", true));
            var options = BaseOptions();
            var policy = new FreeTimeWindowPolicy(options, () => matrix, evaluator);

            var decision = policy.DecideWait(null, Noon);

            Assert.False(decision.ShouldWait);
            Assert.False(string.IsNullOrEmpty(decision.BlockReason));
        }

        [Fact]
        public void DecideWait_恢复时间超出最长等待且非无限等待_阻塞并透传恢复点()
        {
            var clock = new FakeClock(Noon);
            // 冷却 240m（> MaxWait 120m）且关闭无限等待 → 阻塞
            var registry = new QuotaCooldownRegistry(clock, TimeSpan.FromMinutes(240));
            registry.Record("acc1", "m1", Noon.AddMinutes(240), Noon);
            var matrix = new DegradationMatrix(
                new[] { new AccountConfig { Id = "acc1", DisplayName = "a" } },
                new[] { new ModelCandidate { Name = "m1" } });
            var evaluator = new AvailabilityEvaluator(registry);
            var options = BaseOptions();
            options.WaitForQuotaReset = false;
            var earliest = matrix.EarliestRestore(Noon, evaluator); // now+240, QuotaReset
            var policy = new FreeTimeWindowPolicy(options, () => matrix, evaluator);

            var decision = policy.DecideWait(earliest, Noon);

            Assert.False(decision.ShouldWait);
            Assert.Equal(Noon.AddMinutes(240), decision.RestoreAt);
            Assert.Contains("最长可等待时长", decision.BlockReason!);
        }
    }

    // =====================================================================
    // FallbackPolicy（薄封装：仅 ShouldAttempt 含 invested && enabled 准入逻辑）
    // =====================================================================

    public class FallbackPolicyTests
    {
        private sealed class FakeFallback : IFallbackBilling
        {
            public bool EnabledFlag;
            public bool Enabled => EnabledFlag;
            public string? LastSelectTask;
            public FallbackSelection? SelectResult;
            public string? LastSelectForAccountTask;
            public string? LastSelectForAccountId;
            public FallbackSelection? SelectForAccountResult;
            public decimal CapCostArg;
            public decimal CapCostResult;

            public FallbackSelection? TrySelect(string taskId)
            {
                LastSelectTask = taskId;
                return SelectResult;
            }

            public FallbackSelection? TrySelectForAccount(string taskId, string accountId)
            {
                LastSelectForAccountTask = taskId;
                LastSelectForAccountId = accountId;
                return SelectForAccountResult;
            }

            public decimal CapCost(decimal cost)
            {
                CapCostArg = cost;
                return CapCostResult;
            }

            public void RecordModelUsed(string modelName) { }
            public void RecordSuccess(decimal cost, CostBasis basis) { }
            public void RecordFailure(decimal cost, CostBasis basis) { }
            public FallbackSummary BuildSummary() => new();
        }

        [Fact]
        public void Enabled_透传兜底开关()
        {
            var fake = new FakeFallback { EnabledFlag = true };
            var policy = new FallbackPolicy(fake);

            Assert.True(policy.Enabled);
        }

        [Fact]
        public void ShouldAttempt_已投入且启用_允许兜底()
        {
            var fake = new FakeFallback { EnabledFlag = true };
            var policy = new FallbackPolicy(fake);

            Assert.True(policy.ShouldAttempt(invested: true));
        }

        [Fact]
        public void ShouldAttempt_未投入_即使启用也不允许()
        {
            var fake = new FakeFallback { EnabledFlag = true };
            var policy = new FallbackPolicy(fake);

            Assert.False(policy.ShouldAttempt(invested: false));
        }

        [Fact]
        public void ShouldAttempt_已投入但兜底关闭_不允许()
        {
            var fake = new FakeFallback { EnabledFlag = false };
            var policy = new FallbackPolicy(fake);

            Assert.False(policy.ShouldAttempt(invested: true));
        }

        [Fact]
        public void Select_透传taskId并回传选取结果()
        {
            var selection = new FallbackSelection(new AccountConfig { Id = "acc1" }, new FallbackModel { Name = "paid" }, 0.1m);
            var fake = new FakeFallback { EnabledFlag = true, SelectResult = selection };
            var policy = new FallbackPolicy(fake);

            var result = policy.Select("task-9");

            Assert.Same(selection, result);
            Assert.Equal("task-9", fake.LastSelectTask);
        }

        [Fact]
        public void SelectForAccount_透传taskId与账号并回传结果()
        {
            var selection = new FallbackSelection(new AccountConfig { Id = "acc1" }, new FallbackModel { Name = "paid" }, 0.1m);
            var fake = new FakeFallback { EnabledFlag = true, SelectForAccountResult = selection };
            var policy = new FallbackPolicy(fake);

            var result = policy.SelectForAccount("task-9", "acc1");

            Assert.Same(selection, result);
            Assert.Equal("task-9", fake.LastSelectForAccountTask);
            Assert.Equal("acc1", fake.LastSelectForAccountId);
        }

        [Fact]
        public void CapCost_透传成本并回传截断结果()
        {
            var fake = new FakeFallback { EnabledFlag = true, CapCostResult = 1m };
            var policy = new FallbackPolicy(fake);

            var capped = policy.CapCost(3.5m);

            Assert.Equal(3.5m, fake.CapCostArg);
            Assert.Equal(1m, capped);
        }
    }
}
