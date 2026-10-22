using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>数值 &lt;= 0 → 0.45（零值统计胶囊淡化，不抢视觉）；否则 1.0。用于批级状态带统计。</summary>
public sealed class CountOpacityConverter : IValueConverter
{
    /// <summary>共享单例，供 XAML 以 StaticResource 复用。</summary>
    public static readonly CountOpacityConverter Instance = new();

    /// <summary>数值 ≤ 0 时返回 0.45 淡化，否则 1.0；非数值按 1.0。</summary>
    /// <param name="value">源值（整型 / 浮点 / 小数），非数值按 1.0。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            int n => n <= 0 ? 0.45 : 1.0,
            long n => n <= 0 ? 0.45 : 1.0,
            double n => n <= 0 ? 0.45 : 1.0,
            decimal n => n <= 0 ? 0.45 : 1.0,
            _ => 1.0
        };

    /// <summary>不支持反向转换，返回 DoNothing。</summary>
    /// <param name="value">目标值，此处不使用。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
