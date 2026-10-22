namespace BatchAgentRunner.Core.Text;

/// <summary>
/// 文本截断单一事实来源（P2-4 偿还：原 <c>BatchEngine</c> / <c>TaskRunner</c> / <c>WorkflowExecutor</c> /
/// <c>GateEvaluator</c> / <c>ReportWriter</c> / 积分客户端六处各写一份，截断后缀文案互不一致——
/// 「改一处必漏改其余」的重复实现收敛于此）。
/// </summary>
public static class TextTruncate
{
    /// <summary>
    /// 任务结果 / 阶段摘要截断：超过 <paramref name="maxChars"/> 时附「完整内容见运行日志」指引（KB 数按 maxChars 折算）。
    /// <paramref name="maxChars"/> &lt;= 0 视为「不截断」（与原 <c>BatchEngine</c> / <c>TaskRunner</c> 私有实现语义一致）。
    /// </summary>
    public static string? Result(string? value, int maxChars)
    {
        var suffix = maxChars <= 0
            ? null
            : $"\n…（结果已截断，超过 {maxChars / 1024}KB，完整内容见运行日志）";
        return Cut(value, maxChars, suffix);
    }

    /// <summary>通用截断：超过 <paramref name="maxChars"/> 时附固定 <paramref name="suffix"/>（null / 空后缀 = 纯截断）。</summary>
    /// <param name="value">待截断文本，null 原样返回。</param>
    /// <param name="maxChars">字符上限，&lt;=0 表示不截断。</param>
    /// <param name="suffix">超限时追加的提示后缀，null 或空为纯截断。</param>
    public static string? Truncate(string? value, int maxChars, string? suffix) =>
        Cut(value, maxChars, suffix);

    private static string? Cut(string? value, int maxChars, string? suffix)
    {
        if (value is null || maxChars <= 0)
        {
            return value;
        }

        return value.Length <= maxChars ? value : value[..maxChars] + suffix;
    }
}
