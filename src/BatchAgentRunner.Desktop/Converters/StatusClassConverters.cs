using System.Globalization;
using Avalonia.Data.Converters;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>文本等于给定值（多个值用 ; 或 | 分隔）之一时返回 true，用于条件可见性。</summary>
public sealed class EqualsTextConverter : IValueConverter
{
    // CA1861：分隔符常量数组提为 static readonly（每次绑定回调都会走到此方法）
    private static readonly char[] Separators = { ';', '|' };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        var targets = parameter.ToString()!.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return targets.Any(t => string.Equals(value.ToString(), t.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
