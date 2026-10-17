using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Desktop.Converters;

/// <summary>
/// 通过附加属性把「状态」映射为 Ellipse / Button 的 CSS 类，
/// 由 GlobalStyles.axaml 中的全局 &lt;Style&gt; 命中着色（双主题自动切换、运行时切换主题即时刷新）。
/// 之所以不用 {Binding ... Converter={StaticResource X}} 直接写 Classes，是因为 Avalonia XAML 不允许
/// 把 Binding 赋给 Classes 属性（仅接受字符串字面量）；附加属性是类型安全的可绑定替代方案。
/// </summary>
public class StatusDotHelper : AvaloniaObject
{
    public static readonly AttachedProperty<TaskStatus?> TaskStatusProperty =
        AvaloniaProperty.RegisterAttached<StatusDotHelper, Ellipse, TaskStatus?>("TaskStatus");

    public static readonly AttachedProperty<string?> BatchStatusTextProperty =
        AvaloniaProperty.RegisterAttached<StatusDotHelper, Ellipse, string?>("BatchStatusText");

    public static readonly AttachedProperty<string?> NavActivePageProperty =
        AvaloniaProperty.RegisterAttached<StatusDotHelper, Button, string?>("NavActivePage");

    public static readonly AttachedProperty<string?> NavPageProperty =
        AvaloniaProperty.RegisterAttached<StatusDotHelper, Button, string?>("NavPage");

    static StatusDotHelper()
    {
        TaskStatusProperty.Changed.AddClassHandler<Ellipse>(OnTaskStatusChanged);
        BatchStatusTextProperty.Changed.AddClassHandler<Ellipse>(OnBatchStatusChanged);
        NavActivePageProperty.Changed.AddClassHandler<Button>(OnNavChanged);
        NavPageProperty.Changed.AddClassHandler<Button>(OnNavChanged);
    }

    // CLR 访问器：XAML 编译器据此识别附加属性
    public static TaskStatus? GetTaskStatus(Ellipse element) => element.GetValue(TaskStatusProperty);
    public static void SetTaskStatus(Ellipse element, TaskStatus? value) => element.SetValue(TaskStatusProperty, value);

    public static string? GetBatchStatusText(Ellipse element) => element.GetValue(BatchStatusTextProperty);
    public static void SetBatchStatusText(Ellipse element, string? value) => element.SetValue(BatchStatusTextProperty, value);

    public static string? GetNavActivePage(Button element) => element.GetValue(NavActivePageProperty);
    public static void SetNavActivePage(Button element, string? value) => element.SetValue(NavActivePageProperty, value);

    public static string? GetNavPage(Button element) => element.GetValue(NavPageProperty);
    public static void SetNavPage(Button element, string? value) => element.SetValue(NavPageProperty, value);

    private static void OnTaskStatusChanged(Ellipse el, AvaloniaPropertyChangedEventArgs e)
    {
        var cls = e.NewValue is TaskStatus s ? s switch
        {
            TaskStatus.Running => "st-running",
            TaskStatus.Done => "st-done",
            TaskStatus.Failed => "st-failed",
            TaskStatus.Interrupted => "st-interrupted",
            TaskStatus.NeedsAttention => "st-attention",
            TaskStatus.Blocked => "st-blocked",
            _ => "st-pending"
        } : "st-pending";
        el.Classes.Clear();
        el.Classes.Add(cls);
    }

    private static void OnBatchStatusChanged(Ellipse el, AvaloniaPropertyChangedEventArgs e)
    {
        var cls = e.NewValue?.ToString() switch
        {
            "运行中" => "batch-running",
            "等待恢复" => "batch-waiting",
            "已阻塞" => "batch-blocked",
            "已终止" => "batch-terminated",
            "已完成" => "batch-completed",
            _ => "batch-idle"
        };
        el.Classes.Clear();
        el.Classes.Add(cls);
    }

    private static void OnNavChanged(Button btn, AvaloniaPropertyChangedEventArgs e)
    {
        var active = btn.GetValue(NavActivePageProperty);
        var page = btn.GetValue(NavPageProperty);
        if (string.Equals(active, page, StringComparison.OrdinalIgnoreCase))
        {
            if (!btn.Classes.Contains("primary")) btn.Classes.Add("primary");
        }
        else
        {
            btn.Classes.Remove("primary");
        }
    }
}
