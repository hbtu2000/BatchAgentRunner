using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>
/// 读取 CodeBuddy CLI 本地缓存的官方「产品配置」中的模型倍率目录。
/// 数据来源：~/.codebuddy/local_storage/entry_*.info —— CLI/IDE 登录后由服务端配置同步落盘，
/// 形如 [{"userId":..., "data":{"models":[{"id":"glm-5.3","credits":"x0.79 credits",...},...]}}]。
/// 这正是官方模型选择器右侧折扣列（0.29x / 1.62x 等）的数据源；读取失败时返回空（降级为无倍率展示）。
/// </summary>
public static partial class CodeBuddyModelCatalogReader
{
    private static readonly object Lock = new();
    private static Dictionary<string, decimal>? _cache;
    private static DateTime _cacheTime;

    /// <summary>尝试读取模型倍率目录（进程内缓存 5 分钟）。失败或未登录过时返回 false / 空字典。</summary>
    public static bool TryReadCredits(out IReadOnlyDictionary<string, decimal> credits)
    {
        lock (Lock)
        {
            if (_cache is null || DateTime.UtcNow - _cacheTime > TimeSpan.FromMinutes(5))
            {
                _cache = ReadFromDisk();
                _cacheTime = DateTime.UtcNow;
            }

            credits = _cache;
            return _cache.Count > 0;
        }
    }

    private static Dictionary<string, decimal> ReadFromDisk()
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codebuddy", "local_storage");
            if (!Directory.Exists(dir))
            {
                return result;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.info"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length is 0 or > 10_000_000)
                    {
                        continue;
                    }

                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    Collect(doc.RootElement, result);
                }
                catch (JsonException)
                {
                    // 非 JSON 内容（可能是别的本地存储条目），跳过
                }
                catch (IOException)
                {
                    // 文件被 CLI 占用等，跳过
                }
            }
        }
        catch
        {
            // 任何环境异常都不影响调用方（降级为无倍率）
        }

        return result;
    }

    /// <summary>递归收集所有带 id + credits 的模型对象（兼容数组包 data、多 userId 分片等形态）。</summary>
    private static void Collect(JsonElement element, Dictionary<string, decimal> map)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    if (element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        && element.TryGetProperty("credits", out var creditsEl) && creditsEl.ValueKind == JsonValueKind.String
                        && idEl.GetString() is { Length: > 0 } id
                        && TryParseCredits(creditsEl.GetString(), out var multiplier))
                    {
                        map[id] = multiplier; // 后出现的覆盖先出现的（新配置优先）
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        Collect(property.Value, map);
                    }

                    break;
                }

            case JsonValueKind.Array:
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item, map);
                    }

                    break;
                }
        }
    }

    /// <summary>解析倍率文本：「x0.51 credits」/「x0.21」→ 0.51 / 0.21。</summary>
    public static bool TryParseCredits(string? text, out decimal multiplier)
    {
        multiplier = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = CreditsRegex().Match(text);
        return match.Success
            && decimal.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out multiplier);
    }

    [GeneratedRegex(@"x\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsRegex();
}
