using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Serialization;
using Xunit;

namespace BatchAgentRunner.Tests.Options;

/// <summary>
/// P-09 收口守卫：把「God Config」的收口结果变成机器可校验的不变量。
/// <para>本轮 `RunOptions` 改 <c>partial class</c> 按配置域切片（不触碰任何读写点与 JSON 形状），
/// 以下几项是「切片不得改变对外配置契约」的锁：</para>
/// ①<b>顺序契约</b>：System.Text.Json 按成员声明顺序落盘，而 partial 成员的声明顺序＝文件名升序，
///    故切片文件名以两位序号固定；顺序漂移会让用户 `config.json` 产生无意义 diff；
/// ②<b>往返契约</b>：全部持久化属性「写 → 读 → 再写」逐字节稳定（新增字段漏配转换器即红）；
/// ③<b>命名契约</b>：配置键一律 snake_case；
/// ④<b>派生契约</b>：`[JsonIgnore]` 只允许挂在只读派生属性上（可写字段一旦漏配 `[JsonPropertyName]` 会静默丢配置）。
/// </summary>
public class RunOptionsContractTests
{
    /// <summary>切分前 `RunOptions.cs`（363 行单一大类）中的持久化属性声明顺序，切片后必须逐项一致。</summary>
    private static readonly string[] ExpectedPropertyOrder =
    {
        // 2026-10-22 新增 reasoning_effort（推理强度默认 medium）：按 ① 配置域语义位置插入，不改变既有键相对顺序。
        "task_file_path", "agent_kind", "cli_path", "cli_arguments_template", "reasoning_effort", "show_cli_window",
        "workspaces", "default_workspace", "max_workspaces",
        "accounts", "model_candidates", "degradation_strategy",
        "expert", "fallback",
        "max_retries_per_task", "rate_limit_backoff_retries", "rate_limit_initial_backoff_seconds",
        "task_timeout_minutes", "max_attempts_per_task", "max_total_minutes_per_task", "max_wait_minutes",
        "wait_for_quota_reset", "default_cooldown_minutes",
        "concurrency", "force_rerun", "stop_on_failure", "terminate_after_current_task",
        "auto_shutdown", "credits_claim", "auto_answer",
        "notifications", "enable_session_resume", "default_max_gate_loops", "markdown_in_place_writeback",
        "dependency_failure_policy", "template_batch_expansion_limit", "collect_diffs", "diff_target_globs",
        "git", "result_max_kb", "backup_retention"
    };

    private static IEnumerable<PropertyInfo> PersistentProperties() =>
        typeof(RunOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null);

    [Fact]
    public void Json属性顺序_与切片前一致_不产生配置diff漂移()
    {
        var actual = PersistentProperties()
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)
            .ToArray();

        Assert.Equal(ExpectedPropertyOrder, actual);
    }

    [Fact]
    public void 全部持久化属性_写读再写_往返逐字节稳定()
    {
        var sample = Populator.Create<RunOptions>();
        var json = JsonDefaults.Serialize(sample);

        // 所有持久化属性都必须真正参与序列化：漏标 [JsonPropertyName] 或值为 null 被忽略的属性会在此暴露
        using var document = JsonDocument.Parse(json);
        Assert.Equal(ExpectedPropertyOrder, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());

        var back = JsonDefaults.Deserialize<RunOptions>(json);
        Assert.NotNull(back);
        Assert.Equal(json, JsonDefaults.Serialize(back));
    }

    [Fact]
    public void 未知字段_宽容忽略且已知字段不丢()
    {
        // 前向兼容：二期/未来字段在旧版本读取时必须被跳过，而不是抛异常。
        const string json = """
        {
          "task_file_path": "E:/tasks.json",
          "future_feature_flag": { "nested": 1 },
          "concurrency": 3
        }
        """;

        var options = JsonDefaults.Deserialize<RunOptions>(json);

        Assert.NotNull(options);
        Assert.Equal("E:/tasks.json", options!.TaskFilePath);
        Assert.Equal(3, options.Concurrency);
    }

    [Fact]
    public void Core内持久化键_一律snake_case()
    {
        var offenders = typeof(RunOptions).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(name => name is not null)
            .Distinct()
            .Where(name => !IsSnakeCase(name!))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void 配置类型中JsonIgnore_只允许挂在只读派生属性上()
    {
        var offenders = ConfigTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
            .Where(p => p.SetMethod is not null && p.SetMethod.IsPublic)
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // 可写属性一旦挂 [JsonIgnore]，用户在界面上的编辑会静默丢失且无任何提示。
        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> ConfigTypes() =>
        typeof(RunOptions).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(RunOptions).Namespace)
            .Where(t => t.IsPublic && !t.IsEnum);

    private static bool IsSnakeCase(string name)
    {
        foreach (var c in name)
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
            {
                continue;
            }

            return false;
        }

        return name.Length > 0 && char.IsAsciiLetterLower(name[0]);
    }

    /// <summary>反射构造「全字段已填充」的配置实例，使每个持久化属性都真正参与序列化。</summary>
    private sealed class Populator
    {
        public static T Create<T>() where T : class, new() => (T)Create(typeof(T), 0)!;

        private static object? Create(Type type, int depth)
        {
            if (type == typeof(string))
            {
                return "sample";
            }

            if (type.IsEnum)
            {
                return Enum.GetValues(type).Cast<object>().FirstOrDefault(v => !v.Equals(Activator.CreateInstance(type)));
            }

            // 可空基元（int? 等）先拆到基元再填充：否则会填成 null，被 WhenWritingNull 忽略后
            // 「全部持久化属性参与序列化」的往返断言就漏检了新增的可空配置字段。
            var primitive = Nullable.GetUnderlyingType(type) ?? type;
            if (primitive == typeof(bool))
            {
                return true;
            }

            if (primitive.IsPrimitive)
            {
                // 数值一律取非默认值（7 而非 0），确保「值被写入」而非等于默认值
                return Convert.ChangeType(7, primitive, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var item = Create(type.GetGenericArguments()[0], depth + 1);
                var list = (System.Collections.IList?)Activator.CreateInstance(type);
                if (list is null)
                {
                    return null;
                }

                if (item is not null)
                {
                    list.Add(item);
                }

                return list;
            }

            if (type.IsAbstract || depth > 4)
            {
                return null;
            }

            object? instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch (Exception)
            {
                // 需要构造参数的类型（如 FreeTimeWindow）不参与填充，留默认值即可
                return null;
            }

            if (instance is null)
            {
                return null;
            }

            if (type.Namespace is not null && type.Namespace.StartsWith("BatchAgentRunner", StringComparison.Ordinal))
            {
                Fill(instance, depth + 1);
            }

            return instance;
        }

        private static void Fill(object instance, int depth)
        {
            foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod is null || !property.SetMethod.IsPublic)
                {
                    continue;
                }

                if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                {
                    continue;
                }

                var value = Create(property.PropertyType, depth);
                if (value is null)
                {
                    continue;
                }

                try
                {
                    property.SetValue(instance, value);
                }
                catch (Exception)
                {
                    // 只读集合等不可写形态跳过，往返稳定性由整体断言兜底
                }
            }
        }
    }
}
