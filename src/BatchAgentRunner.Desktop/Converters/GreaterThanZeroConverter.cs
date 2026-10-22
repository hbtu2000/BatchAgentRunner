using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>数值 &gt; 0 → true（用于「非零才显示」的统计卡 / 徽标可见性）。</summary>
public sealed class GreaterThanZeroConverter : IValueConverter
{
    /// <summary>共享单例，供 XAML 以 StaticResource 复用。</summary>
    public static readonly GreaterThanZeroConverter Instance = new();

    /// <summary>数值 > 0 时返回 true，用于「非零才显示」。</summary>
    /// <param name="value">源值（整型 / 浮点 / 小数），非数值按 false。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
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
    /// <summary>不支持反向转换，返回 DoNothing。</summary>
    /// <param name="value">目标值，此处不使用。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
