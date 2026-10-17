using System.Globalization;
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

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
