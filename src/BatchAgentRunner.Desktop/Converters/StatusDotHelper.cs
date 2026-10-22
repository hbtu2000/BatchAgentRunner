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
/// P2-13 偿还：批级状态原按中文字面量（「运行中」「等待恢复」…）匹配，改文案即断色；
/// 现改绑 <see cref="BatchStatus"/> 枚举——文案与着色解耦，重写文案不再影响配色。
/// </summary>
public class StatusDotHelper : AvaloniaObject
{
    /// <summary>附加属性：任务状态，变更时切换 Ellipse 的 st-* 类。</summary>
    public static readonly AttachedProperty<TaskStatus?> TaskStatusProperty =
        AvaloniaProperty.RegisterAttached<StatusDotHelper, Ellipse, TaskStatus?>("TaskStatus");

    /// <summary>附加属性：批次状态，变更时切换 Ellipse 的 batch-* 类。</summary>
    public static readonly AttachedProperty<BatchStatus?> BatchStatusProperty =
        AvaloniaProperty.RegisterAttached<StatusDotHelper, Ellipse, BatchStatus?>("BatchStatus");

    /// <summary>附加属性：当前激活页面 key，与 NavPage 相同则按钮加 primary 类。</summary>
    public static readonly AttachedProperty<string?> NavActivePageProperty =
        AvaloniaProperty.RegisterAttached<StatusDotHelper, Button, string?>("NavActivePage");

    /// <summary>附加属性：按钮所属页面 key，用于判定导航项是否高亮。</summary>
    public static readonly AttachedProperty<string?> NavPageProperty =
        AvaloniaProperty.RegisterAttached<StatusDotHelper, Button, string?>("NavPage");

    static StatusDotHelper()
    {
        TaskStatusProperty.Changed.AddClassHandler<Ellipse>(OnTaskStatusChanged);
        BatchStatusProperty.Changed.AddClassHandler<Ellipse>(OnBatchStatusChanged);
        NavActivePageProperty.Changed.AddClassHandler<Button>(OnNavChanged);
        NavPageProperty.Changed.AddClassHandler<Button>(OnNavChanged);
    }

    // CLR 访问器：XAML 编译器据此识别附加属性
    /// <summary>读取 Ellipse 上的任务状态。</summary>
    /// <param name="element">状态点 Ellipse。</param>
    public static TaskStatus? GetTaskStatus(Ellipse element) => element.GetValue(TaskStatusProperty);
    /// <summary>设置任务状态，变更回调切换状态点着色类。</summary>
    /// <param name="element">状态点 Ellipse。</param>
    /// <param name="value">任务状态枚举，null 按待处理着色。</param>
    public static void SetTaskStatus(Ellipse element, TaskStatus? value) => element.SetValue(TaskStatusProperty, value);

    /// <summary>读取 Ellipse 上的批次状态。</summary>
    /// <param name="element">状态点 Ellipse。</param>
    public static BatchStatus? GetBatchStatus(Ellipse element) => element.GetValue(BatchStatusProperty);
    /// <summary>设置批次状态，变更回调切换状态点着色类。</summary>
    /// <param name="element">状态点 Ellipse。</param>
    /// <param name="value">批次状态枚举，null 按空闲着色。</param>
    public static void SetBatchStatus(Ellipse element, BatchStatus? value) => element.SetValue(BatchStatusProperty, value);

    /// <summary>读取按钮上的当前激活页面 key。</summary>
    /// <param name="element">导航按钮 Button。</param>
    public static string? GetNavActivePage(Button element) => element.GetValue(NavActivePageProperty);
    /// <summary>设置当前激活页面 key，变更回调刷新导航高亮。</summary>
    /// <param name="element">导航按钮 Button。</param>
    /// <param name="value">当前激活页面的 key 文本。</param>
    public static void SetNavActivePage(Button element, string? value) => element.SetValue(NavActivePageProperty, value);

    /// <summary>读取按钮所属页面 key。</summary>
    /// <param name="element">导航按钮 Button。</param>
    public static string? GetNavPage(Button element) => element.GetValue(NavPageProperty);
    /// <summary>设置按钮所属页面 key，变更回调刷新导航高亮。</summary>
    /// <param name="element">导航按钮 Button。</param>
    /// <param name="value">按钮所属页面的 key 文本。</param>
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
        var cls = e.NewValue is BatchStatus s ? s switch
        {
            BatchStatus.Running => "batch-running",
            BatchStatus.Waiting => "batch-waiting",
            BatchStatus.Blocked => "batch-blocked",
            BatchStatus.Terminated => "batch-terminated",
            BatchStatus.Completed => "batch-completed",
            _ => "batch-idle"
        } : "batch-idle";
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
