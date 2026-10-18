using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>配置中心：根据当前激活的子视图 key 返回按钮背景色（选中=蓝，未选=浅灰）。</summary>
public sealed class ActiveTabBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isActive = string.Equals(value as string, parameter as string, StringComparison.Ordinal);
        return new SolidColorBrush(isActive ? Color.Parse("#2F6FED") : Color.Parse("#E5E7EB"));
    }

    // P1-20：原为 `throw new NotImplementedException()`——双向绑定一旦触发即崩溃。
    // 这两个转换器本就是单向（tab 选中态→颜色），ConvertBack 不应被调用；若被调用，
    // 返回 UnsetValue 表示「不支持反向转换」而非抛异常，避免运行时崩溃。
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>配置中心：根据当前激活的子视图 key 返回按钮文字色（选中=白，未选=深灰）。</summary>
public sealed class ActiveTabForegroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isActive = string.Equals(value as string, parameter as string, StringComparison.Ordinal);
        return new SolidColorBrush(isActive ? Colors.White : Color.Parse("#1F2937"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
