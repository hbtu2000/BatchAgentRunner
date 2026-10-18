using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>bool 取反（用于「无错误时显示提示」等反向可见性）。</summary>
public sealed class BoolInvertConverter : IValueConverter
{
    public static readonly BoolInvertConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
