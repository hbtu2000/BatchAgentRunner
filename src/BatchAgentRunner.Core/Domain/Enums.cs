namespace BatchAgentRunner.Core.Domain;

/// <summary>任务级状态（PRD 第 10 章）。</summary>
public enum TaskStatus
{
    /// <summary>待执行（含断点续跑的 Interrupted 之外的未开始态）。</summary>
    Pending,
    /// <summary>执行中。</summary>
    Running,
    /// <summary>成功完成。</summary>
    Done,
    /// <summary>失败（计入失败率）。</summary>
    Failed,
    /// <summary>被人工/取消中断（续跑时可恢复）。</summary>
    Interrupted,
    /// <summary>二期新增：门禁/修订循环超限，产出遗留问题清单，等待人工介入（不计入失败率）。</summary>
    NeedsAttention,
    /// <summary>二期新增：依赖未满足或依赖失败导致本任务阻塞（区别于批级 Blocked）。</summary>
    Blocked
}

/// <summary>批级运行态（PRD 第 10 章）。Waiting 为独立状态，非 Running 子状态。</summary>
public enum BatchStatus
{
    /// <summary>空闲（未启动）。</summary>
    Idle,
    /// <summary>运行中。</summary>
    Running,
    /// <summary>等待恢复（免费时段/额度重置）。</summary>
    Waiting,
    /// <summary>阻塞（无可用组合且不等待）。</summary>
    Blocked,
    /// <summary>已终止（人工）。</summary>
    Terminated,
    /// <summary>已完成。</summary>
    Completed
}

/// <summary>Agent CLI 种类（一期单选其一，REQ-002）。</summary>
public enum AgentKind
{
    /// <summary>WorkBuddy 品牌 CLI（同 codebuddy 二进制）。</summary>
    WorkBuddy,
    /// <summary>CodeBuddy CLI。</summary>
    CodeBuddy,
    /// <summary>仿真适配器，用于无 CLI 环境下的端到端自测。</summary>
    Mock
}

/// <summary>降级策略（REQ-004 / 附录 B）。</summary>
public enum DegradationStrategy
{
    /// <summary>默认：同账号先轮换候选模型，该账号模型全耗尽再换账号。</summary>
    ModelFirst,

    /// <summary>先轮完账号池，再换模型。</summary>
    AccountFirst
}

/// <summary>「账号 × 模型」组合在调度决策中被跳过的原因。</summary>
public enum ComboSkipReason
{
    /// <summary>未跳过（可用）。</summary>
    None,
    /// <summary>免费时段外（预期调度行为，不计失败、不告警）。</summary>
    OutsideFreeWindow,
    /// <summary>额度冷却中（重置时间未到）。</summary>
    Cooldown,
    /// <summary>账号被停用（失效/未通过预检）。</summary>
    AccountDisabled
}

/// <summary>额度冷却记录的数据来源（REQ-004c）。</summary>
public enum CooldownSource
{
    /// <summary>CLI 错误中解析出的重置时间。</summary>
    CliParsed,

    /// <summary>解析失败后降级为默认冷却时长。</summary>
    DefaultFallback
}

/// <summary>等待恢复的类型（第 11 章 batch_waiting_window）。</summary>
public enum WaitKind
{
    /// <summary>非等待事件。</summary>
    None,
    /// <summary>等待下一免费时段开始。</summary>
    FreeWindow,
    /// <summary>等待额度重置。</summary>
    QuotaReset,
    /// <summary>同时等待两者（组合恢复时间取 max）。</summary>
    Mixed
}

/// <summary>Agent 执行失败类别（由适配层归一化后交给引擎）。</summary>
public enum AgentFailureKind
{
    /// <summary>无失败（成功占位）。</summary>
    None,
    /// <summary>额度耗尽（配额用尽/余额不足/长期限流上限）。</summary>
    QuotaExhausted,
    /// <summary>临时限流（429 等短时性）。</summary>
    RateLimited,
    /// <summary>命中危险操作确认，未自动批准（REQ-002b）。</summary>
    DangerousOperation,
    /// <summary>账号凭据失效/未登录。</summary>
    AuthExpired,
    /// <summary>单任务执行超时。</summary>
    Timeout,
    /// <summary>其它任务级错误。</summary>
    TaskError
}

/// <summary>兜底花费口径（REQ-004b：实测优先，不可得时估算）。</summary>
public enum CostBasis
{
    /// <summary>CLI 实报用量换算。</summary>
    Measured,
    /// <summary>按调用次数 × 预估单价估算。</summary>
    Estimated
}
