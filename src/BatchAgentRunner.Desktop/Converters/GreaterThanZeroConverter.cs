using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>数值 &gt; 0 → true（用于「非零才显示」的统计卡 / 徽标可见性）。</summary>
public sealed class GreaterThanZeroConverter : IValueConverter
{
    public static readonly GreaterThanZeroConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            int n => n > 0,
            long n => n > 0,
            double n => n > 0,
            decimal n => n > 0,
            _ => false
        };

    // P1-20（同 ConfigHubConverters）：单向转换器 ConvertBack 不应抛异常，
    // 返回 DoNothing 表示「不支持反向转换」，避免双向绑定触发时崩溃。
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
