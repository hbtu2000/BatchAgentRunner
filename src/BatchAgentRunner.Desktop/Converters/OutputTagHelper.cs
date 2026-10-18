using Avalonia;
using Avalonia.Controls;
using BatchAgentRunner.Desktop.Models;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>
/// 把 <see cref="OutputTagKind"/> 映射为输出流标签胶囊的 CSS 类，由 MonitorView 的
/// UserControl.Styles 命中着色（双主题自动切换）。Avalonia 不允许把 Binding 直接赋给
/// Classes 属性，故用附加属性作为类型安全的可绑定替代（同 StatusDotHelper 手法）。
/// </summary>
public class OutputTagHelper : AvaloniaObject
{
    public static readonly AttachedProperty<OutputTagKind> TagKindProperty =
        AvaloniaProperty.RegisterAttached<OutputTagHelper, Border, OutputTagKind>("TagKind");

    static OutputTagHelper()
    {
        TagKindProperty.Changed.AddClassHandler<Border>(OnTagKindChanged);
    }

    public static OutputTagKind GetTagKind(Border element) => element.GetValue(TagKindProperty);

    public static void SetTagKind(Border element, OutputTagKind value) => element.SetValue(TagKindProperty, value);

    private static void OnTagKindChanged(Border el, AvaloniaPropertyChangedEventArgs e)
    {
        // 仅切换颜色类，保留布局类 tag-pill
        el.Classes.Remove("tag-tool");
        el.Classes.Remove("tag-ok");
        el.Classes.Remove("tag-warn");
        el.Classes.Remove("tag-err");
        el.Classes.Remove("tag-plain");

        var cls = e.NewValue is OutputTagKind k ? k switch
        {
            OutputTagKind.Tool => "tag-tool",
            OutputTagKind.Ok => "tag-ok",
            OutputTagKind.Warn => "tag-warn",
            OutputTagKind.Err => "tag-err",
            OutputTagKind.Switch => "tag-warn",
            OutputTagKind.Quota => "tag-warn",
            _ => "tag-plain"
        } : "tag-plain";
        el.Classes.Add(cls);
    }
}

/// <summary>
/// 输出流消息前景色：Dim 行降为次级色（主题感知，从 Application.Resources 取对应主题 brush）。
/// </summary>
public sealed class OutputMessageForegroundConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var key = value is true ? "TextSecondaryBrush" : "TextPrimaryBrush";
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var res) &&
            res is Avalonia.Media.IBrush brush)
        {
            return brush;
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        null;
}
