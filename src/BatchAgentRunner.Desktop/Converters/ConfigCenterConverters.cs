using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>配置中心 rail 区段导航：当绑定的区段 key 等于 ConverterParameter 时返回 true（用于各区段面板 IsVisible）。</summary>
public sealed class SectionVisibleConverter : IValueConverter
{
    /// <summary>共享单例，供 XAML 以 StaticResource 复用。</summary>
    public static readonly SectionVisibleConverter Instance = new();

    /// <summary>当前区段 key 与 ConverterParameter 相等时返回 true。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

    /// <summary>不支持反向转换，返回 null。</summary>
    /// <param name="value">目标值，此处不使用。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>把准备度步骤状态（done / current / pending）映射为类名字符串（rd-done / rd-current / rd-pending），
/// 供 ClassHelper.TrackedClasses 绑定到 Ellipse 上着色（样式仍用 {DynamicResource}）。</summary>
public sealed class ReadinessStateToClassConverter : IValueConverter
{
    /// <summary>共享单例，供 XAML 以 StaticResource 复用。</summary>
    public static readonly ReadinessStateToClassConverter Instance = new();

    /// <summary>把 done / current / pending 映射为 rd-* 类名。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "done" => "rd-done",
            "current" => "rd-current",
            _ => "rd-pending"
        };

    /// <summary>不支持反向转换，返回 null。</summary>
    /// <param name="value">目标值，此处不使用。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>统计多行文本的非空行数，返回形如「18 条」的字符串（危险黑名单 / 白名单计数展示）。</summary>
public sealed class CountLinesConverter : IValueConverter
{
    /// <summary>共享单例，供 XAML 以 StaticResource 复用。</summary>
    public static readonly CountLinesConverter Instance = new();

    /// <summary>统计多行文本的非空行数，返回「N 条」。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrEmpty(text))
        {
            return "0 条";
        }

        var count = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                count++;
            }
        }

        return $"{count} 条";
    }

    /// <summary>不支持反向转换，返回 null。</summary>
    /// <param name="value">目标值，此处不使用。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>rail 导航项高亮：当绑定的区段 key 等于 ConverterParameter 时返回 "rail-item active"，否则返回 "rail-item"，
/// 供 ClassHelper.TrackedClasses 绑定（.active 由样式以 {DynamicResource} 着色）。</summary>
public sealed class RailActiveConverter : IValueConverter
{
    /// <summary>共享单例，供 XAML 以 StaticResource 复用。</summary>
    public static readonly RailActiveConverter Instance = new();

    /// <summary>区段 key 匹配时返回 rail-item active，否则 rail-item。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? "rail-item active"
            : "rail-item";

    /// <summary>不支持反向转换，返回 null。</summary>
    /// <param name="value">目标值，此处不使用。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
