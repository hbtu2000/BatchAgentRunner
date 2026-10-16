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
    public static AgentUsage Estimated(decimal cost) => new(null, null, cost, CostBasis.Estimated);

    public static AgentUsage Measured(decimal cost, long? input = null, long? output = null) =>
        new(input, output, cost, CostBasis.Measured);

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
    public static AgentExecutionResult Ok(AgentRequest request, string? output, AgentUsage? usage = null) =>
        new(true, output, request, usage);

    public static AgentExecutionResult Fail(AgentRequest request, AgentFailure failure, string? partialOutput = null) =>
        new(false, partialOutput, request, null, failure);
}

/// <summary>预检结果（REQ-010）。</summary>
public sealed record PreflightResult(bool Ok, string Message, string? AccountId = null)
{
    public static PreflightResult Pass(string message, string? accountId = null) => new(true, message, accountId);

    public static PreflightResult Fail(string message, string? accountId = null) => new(false, message, accountId);
}

/// <summary>
/// Agent CLI 适配层统一契约（PRD 第 10 章「每家 CLI 一个 adapter」）。
/// 真实参数与错误特征待 M0 验证后填充；仿真适配器 <c>MockAdapter</c> 用于无 CLI 环境自测。
/// </summary>
public interface IAgentCliAdapter
{
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
}
