using System.Globalization;
using Avalonia.Data.Converters;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>文本等于给定值（多个值用 ; 或 | 分隔）之一时返回 true，用于条件可见性。</summary>
public sealed class EqualsTextConverter : IValueConverter
{
    // CA1861：分隔符常量数组提为 static readonly（每次绑定回调都会走到此方法）
    private static readonly char[] Separators = { ';', '|' };

    /// <summary>源文本与 parameter 中任一值（; 或 | 分隔）相等时返回 true。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        var targets = parameter.ToString()!.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return targets.Any(t => string.Equals(value.ToString(), t.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>不支持反向转换，返回 null。</summary>
    /// <param name="value">目标值，此处不使用。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
