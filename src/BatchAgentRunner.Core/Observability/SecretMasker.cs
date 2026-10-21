using System.Text.RegularExpressions;

namespace BatchAgentRunner.Core.Observability;

/// <summary>
/// 运行日志敏感信息脱敏的<b>单一事实来源</b>（ARCH P-10 / A-8「凭证/密钥绝不落日志」）。
/// </summary>
/// <remarks>
/// 治理前脱敏<b>零基础设施</b>：<c>src</c> 内无任何 <c>Mask/Redact/Sanitize</c> 实现，
/// 「凭证不落日志」全靠每个写入点自觉（如 <c>CliAdapterBase</c> 只打凭据引用不打值）。
/// 一旦某处把 <c>BuildEnvironment</c> 的结果、CLI 回显或异常消息拼进日志，密钥即明文落盘——
/// 而运行日志是用户排障时最常外发的文件。
/// 收口后：<b>所有日志写入在渲染入口统一过一次 <see cref="Mask"/></b>，新增写入点不可能绕过；
/// 「脱敏实现只允许一处」由 <c>ObservabilityHygieneTests</c> 守卫。
///
/// 边界：本类只做<b>形态识别</b>（键名 / Bearer / JWT / <c>sk-</c> 前缀），不做语义判断，
/// 故不存在"漏判"之外的误判风险放大；账号 id、模型名、工作区路径等非敏感业务字段一律不脱敏。
/// </remarks>
public static class SecretMasker
{
    /// <summary>脱敏占位符。</summary>
    public const string Placeholder = "***";

    /// <summary>
    /// 键名形态：<c>CODEBUDDY_API_KEY=xxx</c> / <c>token: xxx</c> / <c>"password": "xxx"</c> /
    /// <c>Authorization: Bearer xxx</c>。键名与分隔符保留（便于排障定位是哪个键被吞），值整体替换。
    /// 键名<b>不要求词首边界</b>：<c>CODEBUDDY_API_KEY</c> 这类带前缀的环境变量名同样命中；
    /// 值以 <c>(?!\*\*\*)</c> 排除已打码的占位符，保证脱幂等（重复脱敏不会层层加码）。
    /// </summary>
    private static readonly Regex KeyValuePattern = new(
        @"(?<key>api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|passwd|pwd|authorization|auth[_-]?token|credential(?:s)?|cookie|session[_-]?key)" +
        @"(?<sep>\s*""?\s*[:=]\s*)" +
        @"(?<value>(?!\*\*\*)(?:(?:bearer|basic)\s+[A-Za-z0-9._\-+/=]+|""[^""]*""|'[^']*'|[^\s,;}\]""']+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Authorization 头形态：<c>Bearer xxxx</c>。</summary>
    private static readonly Regex BearerPattern = new(
        @"\b(?<scheme>bearer|basic)\s+(?<value>[A-Za-z0-9._\-+/=]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>JWT（<c>eyJ</c> 开头的三段式）。</summary>
    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}",
        RegexOptions.Compiled);

    /// <summary>常见 API Key 前缀形态（<c>sk-</c> / <c>ghp_</c> / <c>xoxb-</c> / <c>AKIA…</c>）。</summary>
    private static readonly Regex PrefixedKeyPattern = new(
        @"\b(?:(?:sk|ghp|gho|ghs|glpat|xoxb|xoxp)[-_][A-Za-z0-9_\-]{8,}|AKIA[A-Z0-9]{12,})",
        RegexOptions.Compiled);

    /// <summary>
    /// 脱敏：把文本中的凭证/密钥片段替换为 <see cref="Placeholder"/>。
    /// 空输入原样返回（高频调用路径零分配）。
    /// </summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // 顺序有讲究：先按「键名=值」整体吞掉（保住键名与分隔符），
        // 再处理无键名的裸形态（JWT / 前缀密钥 / Bearer 头），避免重复打码出 "*** ***"。
        var masked = KeyValuePattern.Replace(text,
            m => m.Groups["key"].Value + m.Groups["sep"].Value + Placeholder);
        masked = JwtPattern.Replace(masked, Placeholder);
        masked = PrefixedKeyPattern.Replace(masked, Placeholder);
        masked = BearerPattern.Replace(masked, m => m.Groups["scheme"].Value + " " + Placeholder);
        return masked;
    }

    /// <summary>是否检出敏感片段（供守卫测试与自检使用，不落日志）。</summary>
    public static bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return PrefixedKeyPattern.IsMatch(text)
            || JwtPattern.IsMatch(text)
            || BearerPattern.IsMatch(text)
            || KeyValuePattern.IsMatch(text);
    }
}
