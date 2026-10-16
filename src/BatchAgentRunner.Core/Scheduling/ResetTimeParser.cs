using System.Globalization;
using System.Text.RegularExpressions;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>
/// 额度重置时间解析器（REQ-004c / M0 V-10）。
/// 归一化三类输入：① ISO 8601 绝对时间戳；② 相对时长（3h24m / in 2 hours / 90 分钟 / 1天）；③ HTTP Retry-After。
/// 解析结果早于当前时刻或无法解析时返回 null（降级为默认冷却时长，边缘情况 13）。
/// </summary>
public static partial class ResetTimeParser
{
    private static readonly Regex AbsoluteRegex = new(
        @"\d{4}[-/]\d{1,2}[-/]\d{1,2}(?:[T\s]\d{1,2}:\d{2}(?::\d{2})?(?:\.\d+)?\s*(?:Z|[+-]\d{2}:?\d{2})?)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RelativeRegex = new(
        @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>小时|时|分鐘|分钟|分|秒钟|秒|天|日|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s|days?|d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RetryAfterRegex = new(
        @"retry[-\s]?after\s*[:=]?\s*(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InDurationRegex = new(
        @"(?:in|after|within|约|大约)\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>小时|时|分钟|分|秒|天|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s|days?|d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 尝试从文本中解析出重置时间点。返回 null 表示不可得或不可靠。
    /// </summary>
    public static DateTimeOffset? Parse(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // ① 绝对时间戳（ISO 8601 / 常见日期时间）
        var absMatch = AbsoluteRegex.Match(text);
        if (absMatch.Success &&
            DateTimeOffset.TryParse(absMatch.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var absolute))
        {
            return absolute > now ? absolute : null;
        }

        // ② Retry-After（秒）
        var retryAfter = RetryAfterRegex.Match(text);
        if (retryAfter.Success && double.TryParse(retryAfter.Groups["value"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds))
        {
            var candidate = now.AddSeconds(seconds);
            return candidate > now ? candidate : null;
        }

        // ③ 相对时长（支持多段叠加，如 "3h24m"、"1天2小时"）
        var total = TimeSpan.Zero;
        foreach (Match m in RelativeRegex.Matches(text))
        {
            if (double.TryParse(m.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                total += ToTimeSpan(v, m.Groups["unit"].Value);
            }
        }

        // "in 2 hours" 形态（相对正则已覆盖，这里仅作保险）
        var inMatch = InDurationRegex.Match(text);
        if (total == TimeSpan.Zero && inMatch.Success &&
            double.TryParse(inMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var iv))
        {
            total = ToTimeSpan(iv, inMatch.Groups["unit"].Value);
        }

        if (total > TimeSpan.Zero)
        {
            return now.Add(total);
        }

        // 纯数字（裸 Retry-After 值）
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bare))
        {
            var candidate = now.AddSeconds(bare);
            return candidate > now ? candidate : null;
        }

        return null;
    }

    private static TimeSpan ToTimeSpan(double value, string unit) => unit.ToLowerInvariant() switch
    {
        "小时" or "时" or "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(value),
        "分钟" or "分" or "分鐘" or "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(value),
        "秒" or "秒钟" or "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(value),
        "天" or "日" or "d" or "day" or "days" => TimeSpan.FromDays(value),
        _ => TimeSpan.Zero
    };
}
