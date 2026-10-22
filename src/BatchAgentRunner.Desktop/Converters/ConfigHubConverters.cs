using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using BatchAgentRunner.Desktop.Helpers;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>配置中心：根据当前激活的子视图 key 返回按钮背景色（选中=蓝，未选=浅灰；P2-10/P2-11：经 AppBrushes 取主题 token）。</summary>
public sealed class ActiveTabBackgroundConverter : IValueConverter
{
    /// <summary>当前激活 key 与 parameter 相等时返回激活底色，否则返回轨道色。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isActive = string.Equals(value as string, parameter as string, StringComparison.Ordinal);
        return isActive ? AppBrushes.NavActive() : AppBrushes.NavInactiveTrack();
    }

    // P1-20：原为 `throw new NotImplementedException()`——双向绑定一旦触发即崩溃。
    // 这两个转换器本就是单向（tab 选中态→颜色），ConvertBack 不应被调用；若被调用，
    // 返回 UnsetValue 表示「不支持反向转换」而非抛异常，避免运行时崩溃。
    /// <summary>单向转换，不支持反向，返回 DoNothing。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>配置中心：根据当前激活的子视图 key 返回按钮文字色（选中=白，未选=深灰；P2-10/P2-11：经 AppBrushes 取主题 token）。</summary>
public sealed class ActiveTabForegroundConverter : IValueConverter
{
    /// <summary>当前激活 key 与 parameter 相等时返回激活文字色，否则正文色。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isActive = string.Equals(value as string, parameter as string, StringComparison.Ordinal);
        return isActive ? AppBrushes.NavActiveText() : AppBrushes.NavInactiveText();
    }

    /// <summary>单向转换，不支持反向，返回 DoNothing。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
