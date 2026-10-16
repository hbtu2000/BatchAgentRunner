using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using Xunit;

namespace BatchAgentRunner.Tests.Core;

public class QuotaDetectionTests
{
    private static readonly DateTimeOffset Now = At.Local(2026, 10, 15, 10, 0);

    [Fact]
    public void 解析_ISO绝对时间戳()
    {
        var result = ResetTimeParser.Parse("quota exceeded, resets at 2026-10-15T14:30:00", Now);

        Assert.NotNull(result);
        Assert.Equal(new TimeSpan(14, 30, 0), result!.Value.LocalDateTime.TimeOfDay);
    }

    [Fact]
    public void 解析_相对时长_多段叠加()
    {
        var result = ResetTimeParser.Parse("quota exhausted, resets in 3h24m", Now);

        Assert.NotNull(result);
        Assert.Equal(Now.AddHours(3).AddMinutes(24), result!.Value);
    }

    [Fact]
    public void 解析_中文相对时长()
    {
        var result = ResetTimeParser.Parse("额度已用尽，请 90 分钟后重试", Now);

        Assert.NotNull(result);
        Assert.Equal(Now.AddMinutes(90), result!.Value);
    }

    [Fact]
    public void 解析_RetryAfter秒数()
    {
        var result = ResetTimeParser.Parse("rate limited, Retry-After: 120", Now);

        Assert.NotNull(result);
        Assert.Equal(Now.AddSeconds(120), result!.Value);
    }

    [Fact]
    public void 解析_结果早于当前时刻视为不可靠()
    {
        var result = ResetTimeParser.Parse("resets at 2026-10-15T09:00:00", Now);

        Assert.Null(result);
    }

    [Fact]
    public void 解析_无法识别返回空()
    {
        Assert.Null(ResetTimeParser.Parse("something went wrong", Now));
        Assert.Null(ResetTimeParser.Parse(null, Now));
    }

    [Fact]
    public void 分类_额度耗尽()
    {
        var c = new QuotaErrorClassifier();

        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Classify("Error: quota exceeded", 1, Now).Kind);
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Classify("余额不足", 1, Now).Kind);
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Classify("insufficient_quota: free tier used up", 1, Now).Kind);
    }

    [Fact]
    public void 分类_临时限流()
    {
        var c = new QuotaErrorClassifier();
        Assert.Equal(AgentFailureKind.RateLimited, c.Classify("429 too many requests", 1, Now).Kind);
        Assert.Equal(AgentFailureKind.RateLimited, c.Classify("rate limit reached, try again later", 1, Now).Kind);
    }

    [Fact]
    public void 分类_CodeBuddy真实429文案_判RateLimited并解析出重置时间()
    {
        // 2026-10-16 实测文案：「429 您的使用量已超出频率限制，将在 2026-10-16 20:17:12 恢复」
        var c = new QuotaErrorClassifier();
        var failure = c.Classify(
            "429 您的使用量已超出频率限制，将在 2026-10-16 20:17:12 恢复", 0, Now);

        Assert.Equal(AgentFailureKind.RateLimited, failure.Kind);
        Assert.NotNull(failure.ResetAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 20, 17, 12, Now.Offset), failure.ResetAt!.Value);
    }

    [Fact]
    public void 分类_凭据失效()
    {
        var c = new QuotaErrorClassifier();
        Assert.Equal(AgentFailureKind.AuthExpired, c.Classify("unauthorized: not logged in", 1, Now).Kind);
    }

    [Fact]
    public void 分类_普通任务错误()
    {
        var c = new QuotaErrorClassifier();
        Assert.Equal(AgentFailureKind.TaskError, c.Classify("compilation failed", 1, Now).Kind);
    }

    [Fact]
    public void 分类_额度耗尽时解析出重置时间()
    {
        var c = new QuotaErrorClassifier();
        var failure = c.Classify("quota exceeded, resets in 2h", 1, Now);

        Assert.Equal(AgentFailureKind.QuotaExhausted, failure.Kind);
        Assert.NotNull(failure.ResetAt);
        Assert.Equal(Now.AddHours(2), failure.ResetAt!.Value);
    }
}
