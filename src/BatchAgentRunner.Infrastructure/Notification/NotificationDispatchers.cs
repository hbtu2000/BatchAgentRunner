using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Observability;

namespace BatchAgentRunner.Infrastructure.Notification;

/// <summary>桌面通知（无 UI 上下文时降级为日志记录；UI 可注入 toast 回调）。</summary>
public sealed class DesktopNotifier : INotifier
{
    private readonly Action<string, string>? _toast;
    private readonly IRunLogger? _logger;

    public DesktopNotifier(Action<string, string>? toast = null, IRunLogger? logger = null)
    {
        _toast = toast;
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Desktop;

    public Task NotifyAsync(NotificationRule rule, NotificationContext context, CancellationToken cancellationToken = default)
    {
        var title = $"[BatchAgentRunner] {context.Trigger}";
        var body = BuildBody(context);
        if (_toast is not null)
        {
            _toast(title, body);
        }

        _logger?.Write("NOTIFY", $"{title} {body}");
        return Task.CompletedTask;
    }

    internal static string BuildBody(NotificationContext c) =>
        $"批处理 {c.BatchName}：{c.BatchStatus}（成功 {c.Done} / 失败 {c.Failed} / 待人工 {c.NeedsAttention} / 阻塞 {c.Blocked}）";
}

/// <summary>Webhook 通知（POST JSON 到用户配置的 Webhook URL，兼容企业微信/Slack/Discord 等通用 inbound）。</summary>
public sealed class WebhookNotifier : INotifier
{
    private readonly HttpClient _http;

    public WebhookNotifier(HttpClient? http = null) => _http = http ?? new HttpClient();

    public NotificationChannel Channel => NotificationChannel.Webhook;

    public async Task NotifyAsync(NotificationRule rule, NotificationContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rule.WebhookUrl))
        {
            return;
        }

        var payload = new
        {
            trigger = context.Trigger.ToString(),
            batch = context.BatchName,
            status = context.BatchStatus.ToString(),
            total = context.Total,
            done = context.Done,
            failed = context.Failed,
            needs_attention = context.NeedsAttention,
            blocked = context.Blocked,
            duration_seconds = context.DurationSeconds,
            report_path = context.ReportPath,
            message = context.Message
        };

        try
        {
            await _http.PostAsJsonAsync(rule.WebhookUrl, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 通知失败不阻断批处理
            Console.Error.WriteLine($"[WebhookNotifier] 推送失败：{ex.Message}");
        }
    }
}

/// <summary>SMTP 邮件通知（凭据走引用，不存明文密码）。</summary>
public sealed class SmtpNotifier : INotifier
{
    private readonly IRunLogger? _logger;

    public SmtpNotifier(IRunLogger? logger = null) => _logger = logger;

    public NotificationChannel Channel => NotificationChannel.Smtp;

    public async Task NotifyAsync(NotificationRule rule, NotificationContext context, CancellationToken cancellationToken = default)
    {
        var cfg = rule.Smtp;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Host) || cfg.To.Count == 0)
        {
            return;
        }

#pragma warning disable SYSLIB0014 // 逃逸阀台账 #1 / 到期日 2026-12-31 / 责任人 工程 —— SmtpClient 已过时但 .NET 仍支持；换 MailKit 属功能迁移而非缺陷修复，计划性延期见 TECH-DEBT v2.0 §4.7.1 台账 #D1
        try
        {
            using var client = new System.Net.Mail.SmtpClient(cfg.Host, cfg.Port)
            {
                EnableSsl = cfg.UseSsl,
                Credentials = string.IsNullOrWhiteSpace(cfg.CredentialRef)
                    ? null
                    : new System.Net.NetworkCredential(cfg.CredentialRef, RetrieveSecret(cfg.CredentialRef))
            };
            var message = new System.Net.Mail.MailMessage
            {
                From = new System.Net.Mail.MailAddress(cfg.From ?? cfg.To[0]),
                Subject = $"[BatchAgentRunner] {context.Trigger} - {context.BatchName}",
                Body = DesktopNotifier.BuildBody(context)
            };
            foreach (var to in cfg.To)
            {
                message.To.Add(to);
            }

            // CA2016：向前传播取消令牌（取消即中止本轮发送）
            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Write(RunLogLevels.Warn, $"SMTP 通知发送失败：{ex.Message}");
        }
#pragma warning restore SYSLIB0014
    }

    /// <summary>
    /// 凭据引用解析：环境变量名直接取其值，未设置时为空串（调用方按无凭据发送）。
    /// ICredentialVault 集成属计划性延期，登记于「TECH-DEBT-技术债务治理方案-v2.0.md §4.7.1 计划性延期台账 #D1」，
    /// 按 P2-26 规则只在台账滚动状态、注释只描述当前实现。
    /// </summary>
    internal static string RetrieveSecret(string credentialRef)
    {
        return Environment.GetEnvironmentVariable(credentialRef) ?? string.Empty;
    }
}

/// <summary>通知分发器：按规则触发通道，应用安静时段过滤。</summary>
public sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly Dictionary<NotificationChannel, INotifier> _notifiers;

    public NotificationDispatcher(IEnumerable<INotifier> notifiers)
    {
        _notifiers = notifiers.ToDictionary(n => n.Channel, n => n);
    }

    public async Task DispatchAsync(IEnumerable<NotificationRule> rules, NotificationContext context, CancellationToken cancellationToken = default)
    {
        foreach (var rule in rules)
        {
            if (rule.Triggers is not { Count: > 0 } || !rule.Triggers.Contains(context.Trigger))
            {
                continue;
            }

            if (IsQuietHours(rule.QuietHours))
            {
                continue;
            }

            if (_notifiers.TryGetValue(rule.Channel, out var notifier))
            {
                await notifier.NotifyAsync(rule, context, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsQuietHours(string? quietHours) =>
        IsQuietHoursAt(quietHours, TimeOnly.FromTimeSpan(DateTime.Now.TimeOfDay));

    /// <summary>
    /// 安静时段判定（纯函数，便于测试）。格式 HH:mm-HH:mm；跨午夜区间（如 22:00-07:00）按「或」判定；
    /// 未配置或格式非法时一律返回 false（不静默）。
    /// </summary>
    public static bool IsQuietHoursAt(string? quietHours, TimeOnly now)
    {
        if (string.IsNullOrWhiteSpace(quietHours))
        {
            return false;
        }

        var parts = quietHours.Split('-');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!TimeOnly.TryParseExact(parts[0].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(parts[1].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            return false;
        }

        return start <= end
            ? now >= start && now <= end
            : now >= start || now <= end; // 跨午夜
    }
}
