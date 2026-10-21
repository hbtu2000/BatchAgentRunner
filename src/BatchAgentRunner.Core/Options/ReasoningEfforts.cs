namespace BatchAgentRunner.Core.Options;

/// <summary>
/// 推理强度取值（CLI <c>--effort</c>）的唯一口径：配置默认值、参数拼装、配置界面下拉共用。
/// </summary>
/// <remarks>
/// 2026-10-22 实测（codebuddy 2.169.0 <c>--help</c>）：可选 <c>minimal</c> / <c>low</c> / <c>medium</c> / <c>high</c> / <c>xhigh</c> / <c>max</c>，
/// CLI 不传该参数时按 <c>high</c> 推理——单轮首字延迟中位 4.7s、生成约 6.6s，200+ 轮的大任务仅模型侧就要 40 分钟以上，
/// 故本工具默认降到 <c>medium</c>。
/// </remarks>
public static class ReasoningEfforts
{
    /// <summary>默认推理强度。</summary>
    public const string Default = "medium";

    /// <summary>CLI 认可的全部取值（顺序即配置界面的展示顺序）。</summary>
    public static IReadOnlyList<string> Levels { get; } =
        new[] { "minimal", "low", "medium", "high", "xhigh", "max" };

    /// <summary>
    /// 归一化：大小写/首尾空白无关；<c>middle</c> / <c>mid</c> / <c>中</c> / <c>中等</c> 视作 <c>medium</c>。
    /// 未知值回落 <see cref="Default"/>（宁可显式传错，也不静默回到 CLI 默认的 high）；空白返回 <c>null</c> = 不追加参数。
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().ToLowerInvariant();
        if (value is "middle" or "mid" or "中" or "中等")
        {
            return "medium";
        }

        return Levels.Contains(value) ? value : Default;
    }
}
