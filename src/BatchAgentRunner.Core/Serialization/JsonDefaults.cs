using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Serialization;

/// <summary>统一的 System.Text.Json 配置：可读缩进 + 忽略未知字段（前向兼容二期预留字段）。</summary>
public static class JsonDefaults
{
    /// <summary>全局共享的序列化选项（可读缩进 / 属性名大小写不敏感 / 忽略 null 与未知字段 / 枚举字符串化）。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>按 <c>Options</c> 序列化为 JSON 字符串。</summary>
    /// <param name="value">待序列化对象，按全局统一选项输出缩进 JSON。</param>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>按 <c>Options</c> 反序列化 JSON 字符串。</summary>
    /// <param name="json">JSON 文本（容忍注释与尾逗号），空或非法内容抛异常。</param>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
