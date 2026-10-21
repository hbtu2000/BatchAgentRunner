namespace BatchAgentRunner.Core.Observability;

/// <summary>
/// 运行日志级别的<b>单一事实来源</b>（ARCH P-10 / A-8 可观测性）。
/// </summary>
/// <remarks>
/// 治理前级别字面量（<c>"INFO" / "WARN" / "ERROR" / "DANGER"</c>）散落在
/// <c>FileRunLogger</c> 的十余个 <c>Write*</c> 方法与 <c>MapLevel</c> 分支里，
/// 新增日志点靠复制粘贴，级别拼写错了也无人拦（<c>Write("WARNING", …)</c> 会被当成普通级别、
/// 不进即时刷盘通道 → 关键排障记录滞后丢失）。
/// 收口后：级别字面量只在本类出现一次；新增级别必须在此登记并由
/// <c>ObservabilityHygieneTests</c> 的「级别单一事实来源」守卫锁死。
/// </remarks>
public static class RunLogLevels
{
    public const string Info = "INFO";
    public const string Warn = "WARN";
    public const string Error = "ERROR";
    public const string Danger = "DANGER";

    /// <summary>需要即时刷盘的级别（P2-24 批量落盘策略的下游判据）。</summary>
    public static bool IsImportant(string? level) =>
        level is Warn or Error or Danger;

    /// <summary>规范化：非法/未知级别一律回落 <see cref="Info"/>，保证级别集合封闭。</summary>
    public static string Normalize(string? level) => level switch
    {
        Warn => Warn,
        Error => Error,
        Danger => Danger,
        _ => Info
    };
}
