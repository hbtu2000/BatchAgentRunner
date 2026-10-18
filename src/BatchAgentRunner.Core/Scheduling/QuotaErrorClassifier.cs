using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>归一化后的 Agent 失败信息。</summary>
public sealed record AgentFailure(
    AgentFailureKind Kind,
    string Message,
    DateTimeOffset? ResetAt = null,
    string? Raw = null)
{
    public static AgentFailure None { get; } = new(AgentFailureKind.None, string.Empty);
}

/// <summary>
/// 额度错误分类器（REQ-004 / REQ-005）。
/// 模式库来自各 CLI 实际错误特征，M0 V-3 实测后扩展；此处为可配置的初始库。
/// </summary>
public sealed class QuotaErrorClassifier
{
    /// <summary>额度耗尽关键词（命中即判定为额度耗尽，不再走限流退避）。</summary>
    public List<string> QuotaPatterns { get; } = new()
    {
        "quota", "out of credits", "insufficient_quota", "insufficient balance", "no credits",
        "billing", "payment required", "credit balance", "exceeded your current quota",
        "usage limit", "plan limit", "free tier", "额度", "配额", "余额不足", "已用尽", "用量已达上限"
    };

    /// <summary>临时限流关键词（先退避重试同一组合）。</summary>
    public List<string> RateLimitPatterns { get; } = new()
    {
        "rate limit", "rate_limit", "rate-limit", "too many requests", "429",
        "try again later", "temporarily", "限流", "请求过于频繁", "请稍后重试",
        // 2026-10-16 实测 codebuddy 429 文案："您的使用量已超出频率限制，将在 <时间> 恢复"
        "频率限制", "使用量已超出"
    };

    /// <summary>凭据失效关键词。</summary>
    public List<string> AuthPatterns { get; } = new()
    {
        "unauthorized", "unauthenticated", "not logged in", "login required", "session expired",
        "invalid api key", "401", "403", "未登录", "凭据", "登录已过期", "token expired",
        // 2026-10-15 实测 codebuddy 2.167.0：未登录时 stderr 输出 "Authentication required. Please use /login command..."
        "authentication required", "/login command"
    };

    /// <summary>
    /// 依据输出文本与退出码分类失败。额度关键词优先于限流关键词。
    /// </summary>
    public AgentFailure Classify(string? output, int exitCode, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.Now;
        var text = output ?? string.Empty;

        // CA1862：Contains 显式使用 StringComparison.OrdinalIgnoreCase（原实现对两侧同时
        // ToLowerInvariant，与本写法语义等价且避免额外分配；行为由 QuotaDetectionTests 锁定）
        if (QuotaPatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            var resetAt = ResetTimeParser.Parse(text, reference);
            return new AgentFailure(AgentFailureKind.QuotaExhausted, ExtractMessage(text, "额度已耗尽"), resetAt, text);
        }

        if (RateLimitPatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            var resetAt = ResetTimeParser.Parse(text, reference);
            // 2026-10-16：带上传解析的重置时间——退避重试升级为冷却时（BatchEngine goto QuotaExhausted），
            // 可直接采用服务端给的恢复时间（如「将在 2026-10-16 20:17:12 恢复」），而非默认冷却时长。
            return new AgentFailure(AgentFailureKind.RateLimited, ExtractMessage(text, "触发临时限流"), resetAt, text);
        }

        if (AuthPatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return new AgentFailure(AgentFailureKind.AuthExpired, ExtractMessage(text, "账号凭据失效或未登录"), null, text);
        }

        if (exitCode != 0)
        {
            return new AgentFailure(AgentFailureKind.TaskError, ExtractMessage(text, $"Agent 退出码 {exitCode}"), null, text);
        }

        return new AgentFailure(AgentFailureKind.TaskError, ExtractMessage(text, "Agent 执行未返回有效结果"), null, text);
    }

    private static string ExtractMessage(string output, string fallback)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return fallback;
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meaningful = lines.FirstOrDefault(l => l.Length > 0 && l.Length <= 200);
        return meaningful is null ? fallback : $"{fallback}：{meaningful}";
    }
}
