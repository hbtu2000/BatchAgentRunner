using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;

namespace BatchAgentRunner.Tests.Agents;

/// <summary>
/// CliAdapterBase exit 0 静默失败兜底回归测试（2026-10-16「429 不切换账号」事故）：
/// CodeBuddy CLI 的 429 限流 / 额度耗尽以「exit 0 + 输出文本 / stream-json result 事件 is_error=true」返回，
/// 旧逻辑只拦 AuthExpired，RateLimited/QuotaExhausted 被当成功 → 任务标「完成」、引擎不退避、
/// 不记冷却、不切换到下一个有额度的账号，后续任务继续撞同一账号。
/// </summary>
public class CliAdapterSilentFailureTests
{
    /// <summary>暴露 protected 判定方法的测试适配器（不启动真实进程）。</summary>
    private sealed class TestAdapter : CliAdapterBase
    {
        public TestAdapter() : base(new CliAdapterSettings { ExecutablePath = "cmd" })
        {
        }

        public override AgentKind Kind => AgentKind.CodeBuddy;

        protected override string DefaultExecutable => "cmd";

        protected override string DefaultArgumentsTemplate => "-p {prompt}";

        public AgentExecutionResult Resolve(AgentRequest request, string output,
            string? finalResult, bool finalIsError) =>
            ResolveExitZeroOutcome(request, output, finalResult, finalIsError);
    }

    private static AgentRequest CreateRequest() => new(
        "task-001",
        "示例指令",
        new AccountConfig { Id = "acc-1", DisplayName = "账号1" },
        new ModelCandidate { Name = "model-1" },
        ExpertRef.Default(),
        Environment.CurrentDirectory,
        TimeSpan.FromMinutes(5),
        AutoAnswerPolicy.CreateDefault());

    [Fact]
    public void Exit0_result事件isError含429_判RateLimited失败()
    {
        var adapter = new TestAdapter();
        // 真实事故输出形态：stream-json result 事件 is_error=true + 429 文案
        const string output = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"开始执行"}]}}
            {"type":"result","subtype":"error_during_execution","is_error":true,"result":"429 您的使用量已超出频率限制，将在 2026-10-16 20:17:12 恢复"}
            """;

        var result = adapter.Resolve(CreateRequest(), output, "429 您的使用量已超出频率限制", finalIsError: true);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal(AgentFailureKind.RateLimited, result.Failure!.Kind);
    }

    [Fact]
    public void Exit0_result事件isError含额度耗尽_判QuotaExhausted失败()
    {
        var adapter = new TestAdapter();
        const string output = """
            {"type":"result","subtype":"error_during_execution","is_error":true,"result":"quota exceeded: usage limit reached"}
            """;

        var result = adapter.Resolve(CreateRequest(), output, "quota exceeded", finalIsError: true);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentFailureKind.QuotaExhausted, result.Failure!.Kind);
    }

    [Fact]
    public void Exit0_无result事件且纯文本429_判RateLimited失败()
    {
        var adapter = new TestAdapter();

        var result = adapter.Resolve(CreateRequest(),
            "429 您的使用量已超出频率限制，将在 2026-10-16 20:17:12 恢复",
            finalResult: null, finalIsError: false);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentFailureKind.RateLimited, result.Failure!.Kind);
    }

    [Fact]
    public void Exit0_无result事件且凭据失效_仍判AuthExpired失败()
    {
        var adapter = new TestAdapter();

        var result = adapter.Resolve(CreateRequest(),
            "Authentication required. Please use /login command to log in.",
            finalResult: null, finalIsError: false);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentFailureKind.AuthExpired, result.Failure!.Kind);
    }

    [Fact]
    public void Exit0_正常result事件_不误杀_结果取最终回答()
    {
        var adapter = new TestAdapter();
        // 回答正文碰巧提到 429/额度 也不得误判为失败（否则修复类任务会被冤杀）
        const string answer = "已修复接口 429 报错问题并补充额度校验逻辑。";

        var result = adapter.Resolve(CreateRequest(), "raw stream-json output", answer, finalIsError: false);

        Assert.True(result.Succeeded);
        Assert.Equal(answer, result.Output);
    }

    [Fact]
    public void Exit0_无result事件且输出无失败特征_维持成功不误杀()
    {
        var adapter = new TestAdapter();

        var result = adapter.Resolve(CreateRequest(), "普通输出文本", finalResult: null, finalIsError: false);

        Assert.True(result.Succeeded);
    }
}
