using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>bool 取反（用于「无错误时显示提示」等反向可见性）。</summary>
public sealed class BoolInvertConverter : IValueConverter
{
    /// <summary>共享单例，供 XAML 以 StaticResource 复用。</summary>
    public static readonly BoolInvertConverter Instance = new();

    /// <summary>把 bool 源值取反，用于反向可见性绑定。</summary>
    /// <param name="value">源值（bool），非 bool 时按 false 处理。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;

    /// <summary>不支持反向转换，返回 DoNothing 以保持源值。</summary>
    /// <param name="value">目标值，此处不使用。</param>
    /// <param name="targetType">目标类型，由绑定系统传入，此处不使用。</param>
    /// <param name="parameter">ConverterParameter，此处不使用。</param>
    /// <param name="culture">绑定区域信息，此处不使用。</param>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
