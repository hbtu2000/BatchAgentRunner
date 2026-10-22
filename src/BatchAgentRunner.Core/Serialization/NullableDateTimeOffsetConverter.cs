using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Serialization;

/// <summary>
/// 兼容 PRD 附录 A 的任务文件写法：<c>"started_at": ""</c> 与 ISO 8601 时间戳互转。
/// 空字符串 / null 一律视为 null；写出时空值回填为空字符串，保持用户文件形状稳定。
/// </summary>
public sealed class NullableDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    /// <summary>
    /// 读取可为空时间：<c>null</c> / 空字符串一律视为 <c>null</c>；字符串按 ISO 8601 解析
    /// （本地时区假定并转 UTC）；数字按 Unix 秒 / 毫秒时间戳解析；其它类型跳过并返回 <c>null</c>。
    /// </summary>
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return null;
                }

                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var dto))
                {
                    return dto;
                }

                if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var dt))
                {
                    return new DateTimeOffset(dt, TimeSpan.Zero);
                }

                return null;
            case JsonTokenType.Number:
                // 兼容 Unix 秒/毫秒时间戳
                if (reader.TryGetInt64(out var unix))
                {
                    return unix > 1_000_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                        : DateTimeOffset.FromUnixTimeSeconds(unix);
                }

                return null;
            default:
                reader.Skip();
                return null;
        }
    }

    /// <summary>写出：有值按 <c>yyyy-MM-ddTHH:mm:sszzz</c> 输出；<c>null</c> 回填空字符串（保持用户文件形状稳定）。</summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(value.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteStringValue(string.Empty);
        }
    }
}
