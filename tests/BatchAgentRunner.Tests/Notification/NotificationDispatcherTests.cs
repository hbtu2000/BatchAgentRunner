using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Infrastructure.Notification;
using Xunit;

namespace BatchAgentRunner.Tests.Notification;

public class RecordingNotifier : INotifier
{
    public NotificationChannel Channel { get; }

    public List<NotificationRule> Rules { get; } = new();

    public RecordingNotifier(NotificationChannel channel) => Channel = channel;

    public Task NotifyAsync(NotificationRule rule, NotificationContext context, CancellationToken cancellationToken = default)
    {
        Rules.Add(rule);
        return Task.CompletedTask;
    }
}

/// <summary>通知分发测试（REQ-018）：触发匹配、安静时段过滤、通道路由。</summary>
public class NotificationDispatcherTests
{
    private static NotificationContext Ctx(NotifyTrigger trigger) => new()
    {
        Trigger = trigger,
        BatchName = "批处理",
        BatchStatus = BatchStatus.Completed,
        Total = 10,
        Done = 8,
        Failed = 2,
        NeedsAttention = 0,
        Blocked = 0,
        DurationSeconds = 120
    };

    private static NotificationRule Rule(NotificationChannel channel, params NotifyTrigger[] triggers) => new()
    {
        Channel = channel,
        Triggers = triggers.ToList()
    };

    [Fact]
    public async Task 触发事件匹配_推送对应通道()
    {
        var desktop = new RecordingNotifier(NotificationChannel.Desktop);
        var dispatcher = new NotificationDispatcher(new INotifier[] { desktop });
        var rules = new[] { Rule(NotificationChannel.Desktop, NotifyTrigger.BatchCompleted) };

        await dispatcher.DispatchAsync(rules, Ctx(NotifyTrigger.BatchCompleted));

        Assert.Single(desktop.Rules);
    }

    [Fact]
    public async Task 触发事件不匹配_不推送()
    {
        var desktop = new RecordingNotifier(NotificationChannel.Desktop);
        var dispatcher = new NotificationDispatcher(new INotifier[] { desktop });
        var rules = new[] { Rule(NotificationChannel.Desktop, NotifyTrigger.BatchCompleted) };

        await dispatcher.DispatchAsync(rules, Ctx(NotifyTrigger.BatchBlocked));

        Assert.Empty(desktop.Rules);
    }

    [Fact]
    public async Task 同一规则多个触发_命中任一即推送()
    {
        var webhook = new RecordingNotifier(NotificationChannel.Webhook);
        var dispatcher = new NotificationDispatcher(new INotifier[] { webhook });
        var rules = new[]
        {
            Rule(NotificationChannel.Webhook, NotifyTrigger.BatchCompleted, NotifyTrigger.BatchBlocked, NotifyTrigger.BatchTerminated)
        };

        await dispatcher.DispatchAsync(rules, Ctx(NotifyTrigger.BatchTerminated));

        Assert.Single(webhook.Rules);
    }

    [Fact]
    public async Task 多规则多通道_各自路由()
    {
        var desktop = new RecordingNotifier(NotificationChannel.Desktop);
        var webhook = new RecordingNotifier(NotificationChannel.Webhook);
        var smtp = new RecordingNotifier(NotificationChannel.Smtp);
        var dispatcher = new NotificationDispatcher(new INotifier[] { desktop, webhook, smtp });
        var rules = new[]
        {
            Rule(NotificationChannel.Desktop, NotifyTrigger.BatchCompleted),
            Rule(NotificationChannel.Smtp, NotifyTrigger.BatchCompleted)
        };

        await dispatcher.DispatchAsync(rules, Ctx(NotifyTrigger.BatchCompleted));

        Assert.Single(desktop.Rules);
        Assert.Single(smtp.Rules);
        Assert.Empty(webhook.Rules);
    }

    [Fact]
    public async Task 空触发列表_不推送()
    {
        var desktop = new RecordingNotifier(NotificationChannel.Desktop);
        var dispatcher = new NotificationDispatcher(new INotifier[] { desktop });
        var rules = new[] { Rule(NotificationChannel.Desktop) };

        await dispatcher.DispatchAsync(rules, Ctx(NotifyTrigger.BatchCompleted));

        Assert.Empty(desktop.Rules);
    }

    [Fact]
    public async Task 通道无对应通知器_静默跳过不抛异常()
    {
        var desktop = new RecordingNotifier(NotificationChannel.Desktop);
        var dispatcher = new NotificationDispatcher(new INotifier[] { desktop });
        var rules = new[] { Rule(NotificationChannel.Webhook, NotifyTrigger.BatchCompleted) };

        await dispatcher.DispatchAsync(rules, Ctx(NotifyTrigger.BatchCompleted));

        Assert.Empty(desktop.Rules);
    }

    [Theory]
    [InlineData("22:00-07:00", 23, 30, true)]   // 跨午夜：夜间命中
    [InlineData("22:00-07:00", 6, 30, true)]    // 跨午夜：清晨命中
    [InlineData("22:00-07:00", 12, 0, false)]   // 跨午夜：白天不命中
    [InlineData("01:00-06:00", 3, 0, true)]     // 同日内：命中
    [InlineData("01:00-06:00", 8, 0, false)]    // 同日内：不命中
    [InlineData("", 3, 0, false)]               // 未配置：从不静默
    [InlineData("bad-format", 3, 0, false)]     // 非法格式：不静默
    public void 安静时段判定(string quietHours, int hour, int minute, bool expected)
    {
        Assert.Equal(expected, NotificationDispatcher.IsQuietHoursAt(quietHours, new TimeOnly(hour, minute)));
    }
}

/// <summary>
/// P2-26 防回归：SMTP 凭据引用解析的行为契约。
/// 原注释自称「返回占位并提示」与实际实现脱节（实况：环境变量解析已实现）——
/// 本组锁死「环境变量名 → 取其值 / 未设置 → 空串」，实现若退化为占位即失败；
/// 未实现的仅剩 ICredentialVault 集成（TECH-DEBT v2.0 §4.7 计划性延期台账 #D1）。
/// </summary>
public class SmtpNotifierCredentialResolutionTests
{
    [Fact]
    public void 凭据引用_按环境变量名解析取值()
    {
        // 唯一变量名避免与并行用例相互污染；用后即还
        var name = $"BATCHAGENT_TEST_SECRET_{Guid.NewGuid():N}";
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "s3cret");

            Assert.Equal("s3cret", SmtpNotifier.RetrieveSecret(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }

    [Fact]
    public void 凭据引用_环境变量未设置_返回空串()
    {
        var name = $"BATCHAGENT_TEST_MISSING_{Guid.NewGuid():N}";

        Assert.Equal(string.Empty, SmtpNotifier.RetrieveSecret(name));
    }
}
