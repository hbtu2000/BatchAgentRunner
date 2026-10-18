using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>数值 &lt;= 0 → 0.45（零值统计胶囊淡化，不抢视觉）；否则 1.0。用于批级状态带统计。</summary>
public sealed class CountOpacityConverter : IValueConverter
{
    public static readonly CountOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            int n => n <= 0 ? 0.45 : 1.0,
            long n => n <= 0 ? 0.45 : 1.0,
            double n => n <= 0 ? 0.45 : 1.0,
            decimal n => n <= 0 ? 0.45 : 1.0,
            _ => 1.0
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
