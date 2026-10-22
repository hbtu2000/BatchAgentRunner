using System.Collections.Concurrent;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Agents.Adapters;

/// <summary>仿真步骤的结果类型。</summary>
public enum MockOutcome
{
    /// <summary>执行成功。</summary>
    Success,
    /// <summary>触发临时限流（429）。</summary>
    RateLimited,
    /// <summary>额度耗尽，需等待重置或切换账号。</summary>
    QuotaExhausted,
    /// <summary>命中危险操作确认，应拒绝自动批准。</summary>
    DangerousOperation,
    /// <summary>凭据失效或未登录。</summary>
    AuthExpired,
    /// <summary>普通任务失败。</summary>
    TaskError,
    /// <summary>执行超时。</summary>
    Timeout,
}

/// <summary>
/// 仿真步骤：用于在没有真实 CLI 的情况下验证降级 / 冷却 / 等待 / 兜底 / 危险操作等全部逻辑。
/// </summary>
public sealed record MockStep(
    MockOutcome Outcome,
    string? Message = null,
    TimeSpan? ResetIn = null,
    string? Output = null,
    decimal? Cost = null)
{
    /// <summary>构造成功步骤，可指定输出正文与费用。</summary>
    /// <param name="output">成功时返回的输出正文；null 时用「仿真输出」。</param>
    /// <param name="cost">本次调用的费用；null 表示不计费。</param>
    public static MockStep Ok(string? output = null, decimal? cost = null) =>
        new(MockOutcome.Success, Output: output, Cost: cost);

    /// <summary>构造额度耗尽步骤，可带额度重置时间。</summary>
    /// <param name="message">失败提示文本；null 时用 quota exceeded。</param>
    /// <param name="resetIn">距额度重置的剩余时长；null 表示 CLI 未给出。</param>
    public static MockStep Quota(string? message = null, TimeSpan? resetIn = null) =>
        new(MockOutcome.QuotaExhausted, message ?? "quota exceeded", resetIn);

    /// <summary>构造限流步骤，模拟 429 临时限流。</summary>
    /// <param name="message">失败提示文本；null 时用 429 rate limit exceeded。</param>
    public static MockStep RateLimited(string? message = null) =>
        new(MockOutcome.RateLimited, message ?? "429 rate limit exceeded");

    /// <summary>构造危险操作步骤，模拟需人工确认的工具调用。</summary>
    /// <param name="message">危险操作提示文本；null 时用「确认删除文件？」。</param>
    public static MockStep Dangerous(string? message = null) =>
        new(MockOutcome.DangerousOperation, message ?? "确认删除文件？");

    /// <summary>构造凭据失效步骤，模拟未登录状态。</summary>
    /// <param name="message">失败提示文本；null 时用 not logged in。</param>
    public static MockStep Auth(string? message = null) =>
        new(MockOutcome.AuthExpired, message ?? "not logged in");

    /// <summary>构造普通任务失败步骤。</summary>
    /// <param name="message">失败提示文本；null 时用「任务执行失败」。</param>
    public static MockStep Error(string? message = null) =>
        new(MockOutcome.TaskError, message ?? "任务执行失败");
}

/// <summary>
/// 仿真适配器（开发自测用）。按「任务 + 第 n 次调用」返回预设步骤，
/// 可精确复现额度耗尽切换、冷却等待、免费时段外、兜底、危险操作拦截等场景。
/// </summary>
public sealed class MockAdapter : IAgentCliAdapter
{
    private readonly Func<AgentRequest, int, MockStep> _planner;
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _stepDuration;
    private IClock _clock;

    /// <summary>构造仿真适配器：planner 决定第 n 次调用的结果。</summary>
    public MockAdapter(Func<AgentRequest, int, MockStep>? planner = null, TimeSpan? stepDuration = null,
        IClock? clock = null)
    {
        _planner = planner ?? ((_, _) => MockStep.Ok("仿真执行完成"));
        _stepDuration = stepDuration ?? TimeSpan.FromMilliseconds(20);
        _clock = clock ?? new SystemClock();
    }

    /// <summary>替换时钟（便于测试在构造后再注入）。</summary>
    public IClock Clock
    {
        get => _clock;
        set => _clock = value;
    }

    /// <summary>
    /// Mock「Live 凭证文件」路径（DESIGN-账号凭证切换 §7：MockAdapter 模拟同一套 Vault 流程）。
    /// 配置后：预检检查该文件是否存在（不存在 = 未登录 → AuthExpired）；
    /// 每次成功执行后模拟 CLI 的 refresh token 轮换（重写该文件），供「保活回写」联调。
    /// </summary>
    public string? MockLiveCredentialPath { get; set; }

    private long _tokenRotation;

    /// <summary>按全局调用序列返回步骤（超出后重复最后一步），适合简单场景。</summary>
    public static MockAdapter FromSteps(params MockStep[] steps)
    {
        if (steps.Length == 0)
        {
            steps = new[] { MockStep.Ok() };
        }

        var index = -1;
        return new MockAdapter((_, _) =>
        {
            var i = Interlocked.Increment(ref index);
            return steps[Math.Min(i, steps.Length - 1)];
        });
    }

    /// <summary>按「模型」定制：指定模型额度耗尽（可带重置时间），其余成功。</summary>
    /// <param name="modelNames">会命中额度耗尽的模型名集合（忽略大小写）。</param>
    /// <param name="resetIn">额度重置的剩余时长；null 表示 CLn 未给出重置时间。</param>
    public static MockAdapter QuotaExhaustedFor(IEnumerable<string> modelNames, TimeSpan? resetIn = null)
    {
        var set = new HashSet<string>(modelNames, StringComparer.OrdinalIgnoreCase);
        return new MockAdapter((req, _) =>
            set.Contains(req.Model.Name) ? MockStep.Quota(resetIn: resetIn) : MockStep.Ok());
    }

    /// <summary>固定为 Mock，表示不拉起真实 CLI。</summary>
    public AgentKind Kind => AgentKind.Mock;

    /// <summary>累计执行次数（跨任务累加）。</summary>
    public int TotalCalls { get; private set; }

    /// <summary>调用流水，逐条记录任务、账号、模型与第几次尝试，供测试断言。</summary>
    public IReadOnlyList<string> CallLog => _callLog;

    private readonly List<string> _callLog = new();

    /// <summary>仿真环境预检恒定通过（不依赖真实 CLI）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<PreflightResult> ValidateEnvironmentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PreflightResult.Pass("仿真适配器可用（无需真实 CLI）"));

    /// <summary>仿真账号预检：停用账号判失败，配置 Live 凭证路径时检查文件是否存在。</summary>
    public Task<PreflightResult> ValidateAccountAsync(AccountConfig account,
        CancellationToken cancellationToken = default)
    {
        if (!account.Enabled)
        {
            return Task.FromResult(PreflightResult.Fail($"账号 {account.Label} 已停用", account.Id));
        }

        // Vault 联调：Live 凭证不存在 = 未登录（引导登录的清场态）
        if (MockLiveCredentialPath is not null && !File.Exists(MockLiveCredentialPath))
        {
            return Task.FromResult(PreflightResult.Fail(
                $"账号 {account.Label} 未登录（Live 凭证不存在，请先引导登录）", account.Id));
        }

        return Task.FromResult(PreflightResult.Pass($"账号 {account.Label} 可用", account.Id));
    }

    /// <summary>
    /// 仿真：返回一份与真实 CLI 形态一致的样例模型清单，供配置页「拉取可用模型」在无 CLI 环境下联调。
    /// 真实适配器会按账号凭据环境实际读取。
    /// </summary>
    public Task<IReadOnlyList<string>> ListModelsAsync(AccountConfig account,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(SampleModels);
    }

    /// <summary>仿真模型清单（CA1861：常量数组提为 static readonly，避免每次调用重新分配）。</summary>
    private static readonly string[] SampleModels =
    {
        "claude-sonnet-4-6", "claude-opus-4-5", "claude-haiku-4-5",
        "gpt-4o", "gpt-4o-mini", "o3-mini",
        "gemini-2.5-pro", "gemini-2.5-flash",
        "hy3", "hy4"
    };

    /// <summary>仿真：模型目录带官方风格的倍率数据（形态与真实 CLI 一致），供无 CLI 环境联调折扣展示。</summary>
    public Task<IReadOnlyList<ModelCatalogEntry>> ListModelCatalogAsync(AccountConfig account,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelCatalogEntry> catalog = SampleCatalog;
        return Task.FromResult(catalog);
    }

    /// <summary>仿真模型目录（CA1861：同上，提为 static readonly）。</summary>
    private static readonly ModelCatalogEntry[] SampleCatalog =
    {
        new ModelCatalogEntry("claude-sonnet-4-6", 1.00m),
        new ModelCatalogEntry("claude-opus-4-5", 1.62m),
        new ModelCatalogEntry("claude-haiku-4-5", 0.17m),
        new ModelCatalogEntry("gpt-4o", 0.79m),
        new ModelCatalogEntry("gpt-4o-mini", 0.08m),
        new ModelCatalogEntry("o3-mini", 0.51m),
        new ModelCatalogEntry("gemini-2.5-pro", 0.65m),
        new ModelCatalogEntry("gemini-2.5-flash", 0.11m),
        new ModelCatalogEntry("hy3", 0.05m),
        new ModelCatalogEntry("hy4", 0.29m)
    };

    /// <summary>按「任务 + 第几次调用」取预设步骤，返回对应成败并可选模拟 token 轮换。</summary>
    public async Task<AgentExecutionResult> ExecuteAsync(AgentRequest request,
        CancellationToken cancellationToken = default)
    {
        var attempt = _attempts.AddOrUpdate(request.TaskId, 1, (_, v) => v + 1);
        TotalCalls++;
        _callLog.Add($"#{TotalCalls} {request.TaskId} {request.Account.Label}+{request.Model.Name} " +
                     $"attempt{attempt}{(request.IsFallback ? " [兜底]" : "")}");

        Emit(request, $"[{request.Model.Name}] 开始处理任务 {request.TaskId}");

        if (_stepDuration > TimeSpan.Zero)
        {
            // 取消时直接抛出，由引擎统一判定为「用户终止 / 超时」
            await Task.Delay(_stepDuration, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var step = _planner(request, attempt);
        var output = step.Output ?? "仿真输出";
        Emit(request, output);

        var usage = step.Cost.HasValue ? AgentUsage.Measured(step.Cost.Value) : null;

        var failure = step.Outcome switch
        {
            MockOutcome.Success => null,
            MockOutcome.RateLimited => new AgentFailure(AgentFailureKind.RateLimited, step.Message ?? "触发临时限流"),
            MockOutcome.QuotaExhausted => new AgentFailure(AgentFailureKind.QuotaExhausted,
                step.Message ?? "额度已耗尽",
                step.ResetIn.HasValue ? _clock.Now.Add(step.ResetIn.Value) : null,
                step.ResetIn.HasValue ? $"resets in {(int)step.ResetIn.Value.TotalMinutes}m" : null),
            MockOutcome.DangerousOperation => new AgentFailure(AgentFailureKind.DangerousOperation,
                $"检测到危险操作确认（{step.Message ?? "删除文件"}），已拒绝自动批准"),
            MockOutcome.AuthExpired => new AgentFailure(AgentFailureKind.AuthExpired,
                step.Message ?? "账号凭据失效或未登录"),
            MockOutcome.Timeout => new AgentFailure(AgentFailureKind.Timeout, step.Message ?? "执行超时"),
            _ => new AgentFailure(AgentFailureKind.TaskError, step.Message ?? "任务执行失败")
        };

        if (failure is null)
        {
            RotateMockToken();
            return AgentExecutionResult.Ok(request, output, usage);
        }

        return AgentExecutionResult.Fail(request, failure, output);
    }

    /// <summary>模拟 CLI 的 refresh token 轮换：重写 Mock Live 凭证文件的 refresh_token 字段。</summary>
    private void RotateMockToken()
    {
        var path = MockLiveCredentialPath;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            if (json is System.Text.Json.Nodes.JsonObject obj)
            {
                obj["refresh_token"] = $"rt-{Interlocked.Increment(ref _tokenRotation)}";
                File.WriteAllText(path, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
        }
        catch
        {
            // 模拟轮换失败不影响执行结果
        }
    }

    private static void Emit(AgentRequest request, string chunk) => request.OnOutputChunk?.Invoke(chunk + "\n");
}
