using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Core.Abstractions;

/// <summary>发送给 Agent CLI 的一次执行请求。</summary>
public sealed record AgentRequest(
    string TaskId,
    string Instruction,
    AccountConfig Account,
    ModelCandidate Model,
    ExpertRef Expert,
    string WorkingDirectory,
    TimeSpan Timeout,
    AutoAnswerPolicy AutoAnswer)
{
    /// <summary>本次是否使用兜底（付费）模型（REQ-004b）。</summary>
    public bool IsFallback { get; init; }

    /// <summary>实时输出流回调（REQ-007 流式追加）。</summary>
    public Action<string>? OnOutputChunk { get; init; }
}

/// <summary>用量/费用数据（REQ-004b / REQ-011；CLI 不可得时按估算口径）。</summary>
public sealed record AgentUsage(long? InputTokens, long? OutputTokens, decimal? Cost, CostBasis Basis)
{
    /// <summary>按估算口径构造用量数据（CLI 不可得时使用）。</summary>
    /// <param name="cost">估算成本；token 数留空，口径标记为估算。</param>
    public static AgentUsage Estimated(decimal cost) => new(null, null, cost, CostBasis.Estimated);

    /// <summary>按实测口径构造用量数据（token 数可选，缺省仅记成本）。</summary>
    /// <param name="cost">实测成本（CLI 回传值）。</param>
    /// <param name="input">输入 token 数，未知传 null。</param>
    /// <param name="output">输出 token 数，未知传 null。</param>
    public static AgentUsage Measured(decimal cost, long? input = null, long? output = null) =>
        new(input, output, cost, CostBasis.Measured);

    /// <summary>无用量数据时的兜底取值（成本未知，口径按估算）。</summary>
    public static AgentUsage Empty { get; } = new(null, null, null, CostBasis.Estimated);
}

/// <summary>一次执行的结果。</summary>
public sealed record AgentExecutionResult(
    bool Succeeded,
    string? Output,
    AgentRequest Request,
    AgentUsage? Usage = null,
    AgentFailure? Failure = null)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="request">原始执行请求（结果需回指请求上下文）。</param>
    /// <param name="output">CLI 标准输出文本，可为空。</param>
    /// <param name="usage">用量与费用数据，未知为 null。</param>
    public static AgentExecutionResult Ok(AgentRequest request, string? output, AgentUsage? usage = null) =>
        new(true, output, request, usage);

    /// <summary>构造失败结果（可附失败前的部分输出）。</summary>
    /// <param name="request">原始执行请求，失败结果据此回溯任务。</param>
    /// <param name="failure">失败分类与错误特征（重试策略依据）。</param>
    /// <param name="partialOutput">失败前已产出的部分输出，可为空。</param>
    public static AgentExecutionResult Fail(AgentRequest request, AgentFailure failure, string? partialOutput = null) =>
        new(false, partialOutput, request, null, failure);
}

/// <summary>预检结果（REQ-010）。</summary>
public sealed record PreflightResult(bool Ok, string Message, string? AccountId = null)
{
    /// <summary>预检通过。</summary>
    /// <param name="message">面向用户的通过说明。</param>
    /// <param name="accountId">命中的账号标识，无账号上下文为 null。</param>
    public static PreflightResult Pass(string message, string? accountId = null) => new(true, message, accountId);

    /// <summary>预检未通过（消息说明原因）。</summary>
    /// <param name="message">未通过原因（写入事件供用户排查）。</param>
    /// <param name="accountId">相关账号标识，与账号无关时为 null。</param>
    public static PreflightResult Fail(string message, string? accountId = null) => new(false, message, accountId);
}

/// <summary>
/// Agent CLI 适配层统一契约（PRD 第 10 章「每家 CLI 一个 adapter」）。
/// 真实参数与错误特征待 M0 验证后填充；仿真适配器 <c>MockAdapter</c> 用于无 CLI 环境自测。
/// </summary>
public interface IAgentCliAdapter
{
    /// <summary>本适配器对应的 CLI 类型。</summary>
    AgentKind Kind { get; }

    /// <summary>CLI 本体可用性校验：已安装 / 可调用（REQ-010，M0 V-1）。</summary>
    Task<PreflightResult> ValidateEnvironmentAsync(CancellationToken cancellationToken = default);

    /// <summary>极简调用校验：账号是否可用（REQ-010 账号池预检）。</summary>
    Task<PreflightResult> ValidateAccountAsync(AccountConfig account, CancellationToken cancellationToken = default);

    /// <summary>执行一条任务指令。</summary>
    Task<AgentExecutionResult> ExecuteAsync(AgentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 实时读取该账号可用的模型列表（避免用户在配置页手填模型名导致输入错误）。
    /// 实现会带上该账号的凭据环境运行 CLI，因此结果是「按账号视角」的可用模型。
    /// 读取失败或当前 CLI 不支持该能力时返回空列表（调用方降级为手填）。
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(AccountConfig account, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取带官方倍率的模型目录（模型 + credits 倍率，如 0.29 = 官方选择器显示的 0.29x）。
    /// 倍率来自官方产品配置缓存（~/.codebuddy/local_storage），CLI 未拉到目录时退化为纯模型名（倍率为 null）。
    /// </summary>
    Task<IReadOnlyList<ModelCatalogEntry>> ListModelCatalogAsync(AccountConfig account, CancellationToken cancellationToken = default);

    /// <summary>
    /// 该 adapter 是否支持恢复指定会话（方案 b，PRD 15.4.6；SessionResumePolicy 默认关，不满足条件一律回退方案 a）。
    /// 默认 <c>false</c>（完整指令重发）。真实 CLI 适配器启用本能力的前置验证（M0 V-8）属计划性延期，
    /// 登记于「TECH-DEBT-技术债务治理方案-v2.0.md §4.7.1 计划性延期台账 #D2」（P2-26）。
    /// </summary>
    bool CanResume(SessionHandle handle) => false;
}
