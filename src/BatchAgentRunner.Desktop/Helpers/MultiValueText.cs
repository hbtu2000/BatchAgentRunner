using System.Linq;

namespace BatchAgentRunner.Desktop.Helpers;

/// <summary>
/// P1-19 契约收敛：多值文本 ↔ 字符串数组 的统一解析/拼接（白名单、diff 通配符等字段共用）。
/// 此前 <see cref="ViewModels.ConfigViewModel"/> 在 Reload/SaveAsync 多处手写
/// <c>Split('\n', TrimEntries | RemoveEmptyEntries)</c> / <c>string.Join("\n", …)</c>，存在重复；
/// 现集中到此处单一实现，消除重复并便于单测锁住契约。
/// </summary>
public static class MultiValueText
{
    /// <summary>把多值文本按给定分隔符拆成去空白/去空项数组；null/空白返回空数组。</summary>
    public static string[] Split(string? text, params char[] separators)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        if (separators.Length == 0)
        {
            separators = new[] { '\n' };
        }

        return text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>把字符串集合拼接为以 <paramref name="separator"/> 分隔的文本；跳过 null/空白项并 trim。</summary>
    public static string Join(IEnumerable<string?>? values, string separator = "\n")
    {
        if (values is null)
        {
            return string.Empty;
        }

        return string.Join(separator, values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim()));
    }
}
