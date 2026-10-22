using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// ARCH P-14（A-12）收口的「配置 JSON Schema 单一事实来源 + CI 校验」守卫：
/// ① <c>docs/config.schema.json</c> 能校验真实示例配置 <c>samples/config.example.json</c>（schema 校验生效）；
/// ② schema 顶层 <c>properties</c> 键集合与 <c>RunOptions</c> 的 <c>[JsonPropertyName]</c> 键集合逐字一致（代码↔文档同步机制）；
/// ③ 每个 <c>$defs</c> 的键集合与对应 C# 类型的 <c>[JsonPropertyName]</c> 键集合逐字一致（嵌套类型同步）；
/// ④ 负向用例：畸形配置必须被校验器拦截（防止校验器退化为空操作）。
/// <para>机制说明：schema 由 RunOptions 派生（非手写漂移），本测试在 CI 单测桶内强制它与代码契约同步，
/// 任何人改动 RunOptions 字段而不同步 schema 即红——这就是「由 RunOptions 生成 + 代码↔文档同步」的落地方式。</para>
/// </summary>
public class ConfigSchemaTests
{
    private static readonly JsonObject RootSchema = LoadEmbedded("BatchAgentRunner.Tests.Schemas.config.schema.json");

    private static readonly Dictionary<string, Type> TypeDefMap = new()
    {
        ["WorkspaceConfig"] = typeof(WorkspaceConfig),
        ["AccountConfig"] = typeof(AccountConfig),
        ["ModelCandidate"] = typeof(ModelCandidate),
        ["FallbackModel"] = typeof(FallbackModel),
        ["FreeTimeWindow"] = typeof(FreeTimeWindow),
        ["ExpertRef"] = typeof(ExpertRef),
        ["FallbackConfig"] = typeof(FallbackConfig),
        ["AutoShutdownConfig"] = typeof(AutoShutdownConfig),
        ["CreditsClaimConfig"] = typeof(CreditsClaimConfig),
        ["AutoAnswerPolicy"] = typeof(AutoAnswerPolicy),
        ["NotificationRule"] = typeof(NotificationRule),
        ["SmtpConfig"] = typeof(SmtpConfig),
        ["GitConfig"] = typeof(GitConfig),
    };

    private static JsonObject LoadEmbedded(string name)
    {
        var asm = typeof(ConfigSchemaTests).Assembly;
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"缺少嵌入式资源：{name}");
        return (JsonNode.Parse(stream) as JsonObject)!;
    }

    [Fact]
    public void 示例配置_可通过配置Schema校验()
    {
        var asm = typeof(ConfigSchemaTests).Assembly;
        using var sample = asm.GetManifestResourceStream("BatchAgentRunner.Tests.Samples.config.example.json")
            ?? throw new InvalidOperationException("缺少嵌入式示例配置 samples/config.example.json");

        // 配置运行态允许 // 注释与尾随逗号（见 Core/Serialization/JsonDefaults），校验解析需同样宽容。
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var instance = (JsonNode.Parse(sample, documentOptions: options) as JsonObject)!;

        var errors = new JsonSchemaValidator(RootSchema).Validate(instance, RootSchema, "$");
        Assert.Empty(errors);
    }

    [Fact]
    public void Schema顶层属性_与RunOptions的JsonPropertyName键集合一致()
    {
        var schemaProps = SchemaPropertyKeys(RootSchema, "$");
        var codeProps = JsonKeys(typeof(RunOptions));

        Assert.Equal(
            codeProps.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            schemaProps.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void 各嵌套类型Schema_与对应CSharp类型的键集合一致()
    {
        var defs = (RootSchema["$defs"] as JsonObject)!;
        foreach (var (defName, type) in TypeDefMap)
        {
            var def = (defs[defName] as JsonObject)
                ?? throw new InvalidOperationException($"schema 缺少 $defs.{defName}");
            var schemaProps = SchemaPropertyKeys(def, $"$defs.{defName}");
            var codeProps = JsonKeys(type);

            Assert.True(
                codeProps.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                    schemaProps.OrderBy(x => x, StringComparer.Ordinal)),
                $"$defs.{defName} 与 {type.Name} 键集合不一致（schema 漂移或代码新增字段未同步）：\n" +
                $"代码：{string.Join(",", codeProps.OrderBy(x => x, StringComparer.Ordinal))}\n" +
                $"Schema：{string.Join(",", schemaProps.OrderBy(x => x, StringComparer.Ordinal))}");
        }
    }

    [Fact]
    public void 畸形配置_必须被Schema校验拦截_防校验器退化为空操作()
    {
        // concurrency 应为 integer，传入字符串应报错。
        var bad = JsonNode.Parse("""{"concurrency":"not-a-number","agent_kind":"Nope","accounts":[{"id":1}]}""")!;
        var errors = new JsonSchemaValidator(RootSchema).Validate(bad, RootSchema, "$");

        Assert.Contains(errors, e => e.Contains("concurrency"));
        Assert.Contains(errors, e => e.Contains("agent_kind"));
        Assert.Contains(errors, e => e.Contains("accounts"));
    }

    private static HashSet<string> SchemaPropertyKeys(JsonObject schema, string path)
    {
        if (schema.TryGetPropertyValue("$ref", out var refNode) && refNode is JsonValue)
        {
            var def = ResolveRef(refNode.GetValue<string>(), RootSchema);
            return SchemaPropertyKeys(def, path);
        }

        var props = schema["properties"] as JsonObject;
        if (props is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return props.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> JsonKeys(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null
                     && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static JsonObject ResolveRef(string reference, JsonObject root)
    {
        var tokens = reference.Split('/')
            .Where(t => t.Length > 0 && t != "#")
            .ToArray();
        JsonNode? current = root;
        foreach (var token in tokens)
        {
            current = (current as JsonObject)?[token];
        }

        return (current as JsonObject)
            ?? throw new InvalidOperationException($"无法解析 $ref：{reference}");
    }

    /// <summary>
    /// 轻量 JSON Schema（draft 2020-12 子集）校验器，仅依赖 System.Text.Json，无外部依赖。
    /// 支持的子关键字：type / properties / required / additionalProperties(bool) / items / enum /
    /// minimum / maximum / pattern / $ref（指向 #/$defs/X）。
    /// 不支持的子关键字（description/default/examples/title）一律忽略。
    /// </summary>
    private sealed class JsonSchemaValidator
    {
        private readonly JsonObject _root;

        public JsonSchemaValidator(JsonObject root) => _root = root;

        public List<string> Validate(JsonNode? instance, JsonNode? schema, string path)
        {
            var errors = new List<string>();
            Validate(instance, schema, path, errors);
            return errors;
        }

        private void Validate(JsonNode? node, JsonNode? schema, string path, List<string> errors)
        {
            if (schema is not JsonObject so)
            {
                return; // 非对象 schema（如 true/false）不在本子集内，跳过
            }

            // $ref 优先：解析到 $defs 后校验；原 schema 的 type 含 null 时允许 null 实例。
            if (so.TryGetPropertyValue("$ref", out var refNode) && refNode is JsonValue)
            {
                if (node is null)
                {
                    var allowsNull = TypeAllowsNull(so) || TypeAllowsNull(ResolveRef(refNode.GetValue<string>(), _root));
                    if (!allowsNull)
                    {
                        errors.Add($"{path}: 不允许为 null");
                    }

                    return;
                }

                Validate(node, ResolveRef(refNode.GetValue<string>(), _root), path, errors);
                return;
            }

            if (so.TryGetPropertyValue("type", out var typeNode) && typeNode is not null && !CheckType(node, typeNode))
            {
                errors.Add($"{path}: 类型不匹配（期望 {typeNode.ToJsonString()}）");
                return; // 类型错误后不再递归，避免级联误报
            }

            if (so.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray ea)
            {
                var ok = ea.Any(e => node?.ToJsonString() == e?.ToJsonString());
                if (!ok)
                {
                    errors.Add($"{path}: 值 {node?.ToJsonString() ?? "null"} 不在枚举 {enumNode.ToJsonString()} 内");
                }
            }

            if (node is not null && node.GetValueKind() == JsonValueKind.Number)
            {
                var d = node.GetValue<double>();
                if (so.TryGetPropertyValue("minimum", out var min) && min is JsonValue mv && mv.TryGetValue<double>(out var minV) && d < minV)
                {
                    errors.Add($"{path}: {d} 小于最小值 {minV}");
                }

                if (so.TryGetPropertyValue("maximum", out var max) && max is JsonValue xv && xv.TryGetValue<double>(out var maxV) && d > maxV)
                {
                    errors.Add($"{path}: {d} 大于最大值 {maxV}");
                }
            }

            if (node is not null && node.GetValueKind() == JsonValueKind.String
                && so.TryGetPropertyValue("pattern", out var pat) && pat is JsonValue pv && pv.TryGetValue<string>(out var patStr))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(node.GetValue<string>(), patStr))
                {
                    errors.Add($"{path}: 字符串 {node.GetValue<string>()} 不匹配模式 {patStr}");
                }
            }

            if (node is JsonObject obj)
            {
                if (so.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject props)
                {
                    foreach (var prop in props)
                    {
                        if (obj.TryGetPropertyValue(prop.Key, out var child))
                        {
                            Validate(child, prop.Value, $"{path}.{prop.Key}", errors);
                        }
                    }
                }

                if (so.TryGetPropertyValue("required", out var reqNode) && reqNode is JsonArray reqs)
                {
                    foreach (var req in reqs.OfType<JsonValue>())
                    {
                        var name = req.GetValue<string>();
                        if (!obj.ContainsKey(name))
                        {
                            errors.Add($"{path}: 缺少必填字段 {name}");
                        }
                    }
                }

                if (so.TryGetPropertyValue("additionalProperties", out var ap)
                    && ap is JsonValue apValue && apValue.TryGetValue<bool>(out var apBool) && !apBool
                    && obj.Count > 0 && so["properties"] is JsonObject declaredProps)
                {
                    foreach (var key in obj.Select(p => p.Key))
                    {
                        if (!declaredProps.ContainsKey(key))
                        {
                            errors.Add($"{path}: 未知字段 {key}（additionalProperties:false）");
                        }
                    }
                }
            }
            else if (node is JsonArray arr)
            {
                if (so.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonObject items)
                {
                    var i = 0;
                    foreach (var item in arr)
                    {
                        Validate(item, items, $"{path}[{i}]", errors);
                        i++;
                    }
                }
            }
        }

        private static bool CheckType(JsonNode? node, JsonNode typeNode)
        {
            var actual = ActualTypeTokens(node);
            if (typeNode is JsonValue tv && tv.TryGetValue<string>(out var ts))
            {
                return actual.Contains(ts);
            }

            if (typeNode is JsonArray ta)
            {
                return ta.OfType<JsonValue>().Any(t => t.TryGetValue<string>(out var s) && actual.Contains(s));
            }

            return true;
        }

        private static bool TypeAllowsNull(JsonNode schema)
        {
            if (schema is not JsonObject so)
            {
                return false;
            }

            if (so.TryGetPropertyValue("type", out var typeNode))
            {
                if (typeNode is JsonValue tv && tv.TryGetValue<string>(out var ts))
                {
                    return ts == "null";
                }

                if (typeNode is JsonArray ta)
                {
                    return ta.OfType<JsonValue>().Any(t => t.TryGetValue<string>(out var s) && s == "null");
                }
            }

            return false;
        }

        private static HashSet<string> ActualTypeTokens(JsonNode? node) => node?.GetValueKind() switch
        {
            null or JsonValueKind.Null => new() { "null" },
            JsonValueKind.Object => new() { "object" },
            JsonValueKind.Array => new() { "array" },
            JsonValueKind.String => new() { "string" },
            JsonValueKind.True or JsonValueKind.False => new() { "boolean" },
            JsonValueKind.Number => new() { "number", "integer" },
            _ => new(),
        };
    }
}
