using Avalonia;
using Avalonia.Controls;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>
/// 让控件可以通过绑定一个「空格分隔的类名」字符串来动态设置 <c>Classes</c>。
/// Avalonia 的 <c>Classes</c> 属性是只读的，无法用 <c>{Binding ...}</c> 直接赋值，
/// 因此这里用附加属性接收字符串，在变更时先移除上一次管理的类、再添加新类。
/// 这样既能按状态切类，又不会与样式里基于 <c>{DynamicResource}</c> 的主题色冲突。
/// </summary>
public class ClassHelper : AvaloniaObject
{
    /// <summary>附加属性：接收空格分隔的类名，变更时同步到控件 Classes。</summary>
    public static readonly AttachedProperty<string?> TrackedClassesProperty =
        AvaloniaProperty.RegisterAttached<ClassHelper, Control, string?>("TrackedClasses");

    /// <summary>读取控件上由本附加属性托管的类名文本。</summary>
    /// <param name="obj">目标控件（任意 AvaloniaObject）。</param>
    public static string? GetTrackedClasses(AvaloniaObject obj) => obj.GetValue(TrackedClassesProperty);

    /// <summary>设置类名文本，变更回调会先移除旧类再添加新类。</summary>
    /// <param name="obj">目标控件（任意 AvaloniaObject）。</param>
    /// <param name="value">空格分隔的类名列表，空值表示清除本属性托管的类。</param>
    public static void SetTrackedClasses(AvaloniaObject obj, string? value) => obj.SetValue(TrackedClassesProperty, value);

    static ClassHelper()
    {
        TrackedClassesProperty.Changed.AddClassHandler<Control>(OnTrackedClassesChanged);
    }

    private static void OnTrackedClassesChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var oldVal = e.OldValue as string;
        var newVal = e.NewValue as string;

        if (!string.IsNullOrWhiteSpace(oldVal))
        {
            foreach (var c in oldVal.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                control.Classes.Remove(c);
            }
        }

        if (!string.IsNullOrWhiteSpace(newVal))
        {
            foreach (var c in newVal.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                control.Classes.Add(c);
            }
        }
    }
}
