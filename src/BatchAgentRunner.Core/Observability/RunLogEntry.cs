using System.Globalization;

namespace BatchAgentRunner.Core.Observability;

/// <summary>
/// 结构化运行日志条目（ARCH P-10 / A-8 可观测性的日志侧单一写入形态）。
/// </summary>
/// <remarks>
/// 治理前 <c>IRunLogger</c> 的十余个 <c>Write*</c> 方法各自拼一段人读文本直接刷盘，
/// 同一条事实的「事件名 / 任务 / 账号 / 模型 / 明细」全挤在一个字符串里：
/// 日志只能 <c>tail</c> 看，无法按 RunId 聚合、无法按字段采集、也无法对敏感值统一脱敏。
/// 收口后：<b>一切写入都先落成条目，再由实现侧渲染</b>——
/// <list type="bullet">
///   <item>文本行（人读 tail）与结构化字段（机器采集）同源，不存在两份口径；</item>
///   <item><see cref="RunId"/> / <see cref="TaskId"/> 由条目承载，日志天然可按批次与任务关联；</item>
///   <item>脱敏只需在渲染入口做一次，新增日志点不可能绕过（由 <c>ObservabilityHygieneTests</c> 锁死）。</item>
/// </list>
/// </remarks>
public sealed record RunLogEntry(
    string Level,
    string Event,
    string Message)
{
    /// <summary>条目产生时刻（由实现侧在写入时填充，未填则取写入时刻）。</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>批次关联标识（一次运行一个，贯穿日志与指标）。</summary>
    public string? RunId { get; init; }

    public string? TaskId { get; init; }

    public string? Account { get; init; }

    public string? Model { get; init; }

    /// <summary>高危确认点标记：渲染为醒目前缀（REQ-010b），结构化侧亦作为采集维度。</summary>
    public bool IsDangerous { get; init; }

    /// <summary>结构化附加字段（成本/状态/来源等，值一律为字符串以便采集端免解析）。</summary>
    public IReadOnlyDictionary<string, string>? Fields { get; init; }

    /// <summary>
    /// 即时刷盘（P2-24 批量落盘策略的上游开关）：任务输出等需「实时 tail」的高频行置真，
    /// 不因 INFO 批量落盘而滞后；其余行按级别与批量阈值刷盘。
    /// </summary>
    public bool Flush { get; init; }

    /// <summary>
    /// 渲染人读正文：<c>{Event} {Message}</c>。事件名为空时只输出消息，
    /// 使「纯文本写入」（<see cref="Abstractions.IRunLogger.Write(string, string)"/>）保持原样。
    /// 时刻与级别由实现侧统一加前缀，避免每个写入点各拼一遍时间戳。
    /// </summary>
    public string RenderBody()
    {
        var prefix = IsDangerous ? "*** 高危 *** " : string.Empty;
        return string.IsNullOrEmpty(Event) ? prefix + Message : prefix + Event + " " + Message;
    }

    /// <summary>取用于采集的数值字段（把可空数值统一成不变文化字符串）。</summary>
    public static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Num(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// 生成批次关联标识：<c>run-{时间戳}-{6 位随机}</c>（同秒启动的两次运行也不撞车）。
    /// 全局唯一生成入口：日志与指标共用，杜绝"两个 id"的关联断裂。
    /// </summary>
    public static string NewRunId() =>
        $"run-{DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-" +
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..6];
}
