using Avalonia;
using Avalonia.Controls;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>
/// 让控件可以通过绑定一个「空格分隔的类名」字符串来动态设置 <see cref="Control.Classes"/>。
/// Avalonia 的 <c>Classes</c> 属性是只读的，无法用 <c>{Binding ...}</c> 直接赋值，
/// 因此这里用附加属性接收字符串，在变更时先移除上一次管理的类、再添加新类。
/// 这样既能按状态切类，又不会与样式里基于 <c>{DynamicResource}</c> 的主题色冲突。
/// </summary>
public class ClassHelper : AvaloniaObject
{
    public static readonly AttachedProperty<string?> TrackedClassesProperty =
        AvaloniaProperty.RegisterAttached<ClassHelper, Control, string?>("TrackedClasses");

    public static string? GetTrackedClasses(AvaloniaObject obj) => obj.GetValue(TrackedClassesProperty);

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
