using System.Globalization;
using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 模型的免费时段（REQ-003b）。未配置（null）视为全天可用。
/// 判定以本机挂钟时间（wall-clock）为准，支持跨零点时段与星期过滤。
/// </summary>
public sealed class FreeTimeWindow
{
    /// <summary>时段开始（本机挂钟时间）。</summary>
    [JsonPropertyName("start")]
    public TimeOnly Start { get; set; }

    /// <summary>时段结束；大于 <see cref="Start"/> 时表示跨零点时段的次日结束。</summary>
    [JsonPropertyName("end")]
    public TimeOnly End { get; set; }

    /// <summary>
    /// 星期过滤。语义为「时段覆盖日」：时段覆盖的任一日命中该集合即生效；
    /// 跨零点时段同时检查开始日与结束日（如「周日 22:00 → 周一 06:00」因覆盖周一而生效）。
    /// null 表示不过滤。
    /// 刻意用具体类型而非 IReadOnlySet：System.Text.Json 无法实例化接口类型反序列化集合。
    /// </summary>
    [JsonPropertyName("days")]
    public HashSet<DayOfWeek>? Days { get; set; }

    /// <summary>空构造（反序列化用）。</summary>
    public FreeTimeWindow()
    {
    }

    /// <summary>构造免费时段。</summary>
    /// <param name="start">时段起点（含）。</param>
    /// <param name="end">时段终点（不含；与 <paramref name="start"/> 相同视为全天）。</param>
    /// <param name="days">生效星期集合；null 表示每日生效。</param>
    public FreeTimeWindow(TimeOnly start, TimeOnly end, IEnumerable<DayOfWeek>? days = null)
    {
        Start = start;
        End = end;
        Days = days is null ? null : new HashSet<DayOfWeek>(days);
    }

    /// <summary>起止相同视为全天。</summary>
    [JsonIgnore]
    public bool IsFullDay => Start == End;

    /// <summary>跨零点时段（如 22:00–06:00）。</summary>
    [JsonIgnore]
    public bool IsCrossMidnight => !IsFullDay && Start > End;

    /// <summary>判定给定时刻是否落在免费时段内（含跨零点与星期过滤）。</summary>
    /// <returns>落在时段内返回 true。</returns>
    public bool Contains(DateTimeOffset instant)
    {
        var local = instant.LocalDateTime;
        var date = DateOnly.FromDateTime(local.Date);
        var tod = TimeOnly.FromDateTime(local);

        if (!IsTimeInRange(tod))
        {
            return false;
        }

        var (startDay, endDay) = GetCoveredDays(date, tod);
        return DaysMatch(startDay, endDay);
    }

    /// <summary>
    /// 下一个时段开始时刻（用于 waiting 倒计时，REQ-003b）。
    /// </summary>
    public DateTimeOffset? NextStartAfter(DateTimeOffset instant)
    {
        var baseDate = DateOnly.FromDateTime(instant.LocalDateTime.Date);
        var offset = instant.Offset;

        for (var i = 0; i < 9; i++)
        {
            var d = baseDate.AddDays(i);
            var local = new DateTime(d.Year, d.Month, d.Day, Start.Hour, Start.Minute, Start.Second);
            if (local <= instant.LocalDateTime)
            {
                continue;
            }

            var candidate = new DateTimeOffset(local, offset);
            if (Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>人话版时段描述，如「每日 08:00–12:00」「周一至周五 22:00 → 次日 06:00」。</summary>
    public string Describe()
    {
        var days = DescribeDays();
        if (IsFullDay)
        {
            return days is null ? "全天可用" : $"{days} 全天";
        }

        return IsCrossMidnight
            ? $"{days ?? "每日"} {Start:HH:mm} → 次日 {End:HH:mm}"
            : $"{days ?? "每日"} {Start:HH:mm}–{End:HH:mm}";
    }

    /// <summary>星期过滤的人话描述（如「周一、周三」）；未配置或全选返回 null。</summary>
    /// <returns>星期描述或 null。</returns>
    public string? DescribeDays()
    {
        if (Days is null || Days.Count == 0 || Days.Count == 7)
        {
            return null;
        }

        return string.Join("、", Enum.GetValues<DayOfWeek>()
            .Where(Days.Contains)
            .Select(ToChineseDayName));
    }

    /// <summary>同 <see cref="Describe"/>。</summary>
    /// <returns>人话版时段描述。</returns>
    public override string ToString() => Describe();

    // 用户以「HH:mm」表达时段时，结束时刻天然是「闭区间」语义（写 07:59 即希望包含 07:59 这一整分钟，
    // 直到 08:00:00 才结束）。因此按分钟粒度做闭区间判定，避免跨零点/收尾分钟被半开区间误丢。
    private bool IsTimeInRange(TimeOnly tod)
    {
        if (IsFullDay)
        {
            return true;
        }

        var startMin = Start.Hour * 60 + Start.Minute;
        var endMin = End.Hour * 60 + End.Minute;
        var t = tod.Hour * 60 + tod.Minute;

        return !IsCrossMidnight
            ? t >= startMin && t <= endMin
            : t >= startMin || t <= endMin;
    }

    private (DateOnly StartDay, DateOnly EndDay) GetCoveredDays(DateOnly date, TimeOnly tod)
    {
        if (IsFullDay || !IsCrossMidnight)
        {
            return (date, date);
        }

        // 跨零点：tod >= Start 落在「当日 → 次日」区间，否则落在「前一日 → 当日」区间
        return tod >= Start
            ? (date, date.AddDays(1))
            : (date.AddDays(-1), date);
    }

    private bool DaysMatch(DateOnly startDay, DateOnly endDay)
    {
        if (Days is null || Days.Count == 0)
        {
            return true;
        }

        return Days.Contains(startDay.DayOfWeek) ||
               (endDay != startDay && Days.Contains(endDay.DayOfWeek));
    }

    /// <summary>星期中文名（周一 ~ 周日）。</summary>
    /// <param name="day">星期枚举值。</param>
    /// <returns>中文星期名。</returns>
    public static string ToChineseDayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        DayOfWeek.Sunday => "周日",
        _ => day.ToString()
    };

    /// <summary>星期缩写 token（配置 JSON 用 mon–sun）。</summary>
    /// <param name="day">星期枚举值，用于取对应缩写。</param>
    public static string ToDayToken(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "mon",
        DayOfWeek.Tuesday => "tue",
        DayOfWeek.Wednesday => "wed",
        DayOfWeek.Thursday => "thu",
        DayOfWeek.Friday => "fri",
        DayOfWeek.Saturday => "sat",
        DayOfWeek.Sunday => "sun",
        _ => day.ToString().ToLowerInvariant()
    };

    /// <summary>解析星期 token（mon–sun / monday–sunday / 1–7 / 0 / 周一–周日）；无法识别返回 null。</summary>
    /// <param name="token">待解析 token。</param>
    /// <returns>星期枚举值或 null。</returns>
    public static DayOfWeek? ParseDayToken(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "mon" or "monday" or "1" => DayOfWeek.Monday,
        "tue" or "tuesday" or "2" => DayOfWeek.Tuesday,
        "wed" or "wednesday" or "3" => DayOfWeek.Wednesday,
        "thu" or "thursday" or "4" => DayOfWeek.Thursday,
        "fri" or "friday" or "5" => DayOfWeek.Friday,
        "sat" or "saturday" or "6" => DayOfWeek.Saturday,
        "sun" or "sunday" or "7" or "0" => DayOfWeek.Sunday,
        "周一" => DayOfWeek.Monday,
        "周二" => DayOfWeek.Tuesday,
        "周三" => DayOfWeek.Wednesday,
        "周四" => DayOfWeek.Thursday,
        "周五" => DayOfWeek.Friday,
        "周六" => DayOfWeek.Saturday,
        "周日" or "周天" => DayOfWeek.Sunday,
        _ => null
    };

    /// <summary>从「HH:mm」字符串与星期 token 列表解析免费时段；起止解析失败按 00:00（全天）处理。</summary>
    /// <returns>解析出的免费时段。</returns>
    public static FreeTimeWindow ParseStartEnd(string? start, string? end, IEnumerable<string>? days = null)
    {
        var s = TimeOnly.TryParse(start, CultureInfo.InvariantCulture, out var sv) ? sv : new TimeOnly(0, 0);
        var e = TimeOnly.TryParse(end, CultureInfo.InvariantCulture, out var ev) ? ev : new TimeOnly(0, 0);
        var d = days?.Select(ParseDayToken).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return new FreeTimeWindow(s, e, d);
    }
}
