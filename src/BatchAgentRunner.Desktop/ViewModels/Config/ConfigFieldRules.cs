namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置保存共享校验器（P-04 分解产物）。
/// 原 <c>ConfigViewModel.SaveAsync</c> 把十余处 <c>Math.Max</c> 钳制与空白归一
/// （空串回落 null / 去首尾空白 / 回落默认值）内联在一处；分解后各子编辑器的
/// <c>ApplyTo</c> 均以本类为准绳——钳制与归一规则只此一份，杜绝「同一字段被两个
/// 切片各写一遍钳制」的语义分叉（REQ-019 的「以上限为唯一校验准绳」同款思路）。
/// </summary>
internal static class ConfigFieldRules
{
    /// <summary>下限 1：并发数 / 超时分钟 / 尝试次数 / 工作空间上限 / 门禁循环 / 模板展开上限。</summary>
    internal static int AtLeastOne(int value) => Math.Max(1, value);

    /// <summary>下限 0：重试次数 / 限流退避 / 最长可等待 / 静默秒数 / 选择索引 / 结果上限 / 备份份数。</summary>
    internal static int AtLeastZero(int value) => Math.Max(0, value);

    /// <summary>空白串归一为 null（保留非空白原值，不做去空白）。</summary>
    internal static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>空白串归一为 null，非空白去首尾空白。</summary>
    internal static string? TrimmedOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>空白串归一为空串，非空白去首尾空白。</summary>
    internal static string TrimmedOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    /// <summary>空白串回落默认值（保留非空白原值，不做去空白）。</summary>
    internal static string OrFallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
