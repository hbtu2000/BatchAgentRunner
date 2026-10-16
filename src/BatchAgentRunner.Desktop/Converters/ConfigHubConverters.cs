using System;
using System.Globalization;
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

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
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
        => throw new NotImplementedException();
}
