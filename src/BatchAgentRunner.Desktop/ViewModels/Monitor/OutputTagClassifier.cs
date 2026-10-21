using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Desktop.Models;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 实时输出流的标签分类（P-06 从 <see cref="MonitorViewModel"/> 抽出的纯函数）：
/// 对单行原始 stdout 做关键字启发式分类，以及把引擎调度类事件映射为输出流标签。
/// 逻辑与抽取前逐字节等价，无副作用。
/// </summary>
internal static class OutputTagClassifier
{
    /// <summary>对单行原始输出做轻量分类（关键字启发式，无需改动引擎输出格式）。</summary>
    public static (OutputTagKind Kind, string Tag, bool Dim) ClassifyLine(string line)
    {
        if (line.Contains("切换") || line.Contains("降级") || line.Contains("switched", StringComparison.OrdinalIgnoreCase))
            return (OutputTagKind.Switch, "切换", false);
        if (line.Contains("额度") || line.Contains("冷却") || line.Contains("免费时段") || line.Contains("时段外") ||
            line.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return (OutputTagKind.Quota, "额度", false);
        if (line.Contains("Bash:") || line.Contains("Read:") || line.Contains("Write:") || line.Contains("Edit:") ||
            line.Contains("Grep:") || line.Contains("工具调用") || line.Contains("[工具参数]") || line.Contains("Tool:"))
            return (OutputTagKind.Tool, "工具调用", false);
        if (line.Contains("错误") || line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Exception") || line.Contains("失败"))
            return (OutputTagKind.Err, "错误", false);
        if (line.Contains("完成") || line.Contains("成功") || line.Contains("通过") ||
            line.Contains("ok", StringComparison.OrdinalIgnoreCase))
            return (OutputTagKind.Ok, "完成", true);
        return (OutputTagKind.Plain, "输出", false);
    }

    /// <summary>把引擎调度类事件映射为输出流标签（AutoAnswered 的特殊处理由调用方负责）。</summary>
    public static (OutputTagKind Kind, string Tag) ClassifyEvent(BatchEventKind kind) => kind switch
    {
        BatchEventKind.ModelSwitched or BatchEventKind.AccountSwitched or BatchEventKind.FallbackUsed
            => (OutputTagKind.Switch, "切换"),
        BatchEventKind.ModelWindowSkipped or BatchEventKind.ModelCooldownSkipped or BatchEventKind.QuotaResetRecorded
            => (OutputTagKind.Quota, "额度"),
        BatchEventKind.Warning or BatchEventKind.BatchBlocked or BatchEventKind.BatchWaiting
            => (OutputTagKind.Warn, "提示"),
        BatchEventKind.DangerousOperationBlocked or BatchEventKind.AutoAnswered
            => (OutputTagKind.Err, "拦截"),
        _ => (OutputTagKind.Plain, "输出")
    };
}
