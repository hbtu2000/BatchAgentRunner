using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>会话恢复决策结果。</summary>
public enum SessionResumeDecision
{
    /// <summary>不恢复：以全新会话执行（一期方案 a，等价「完整指令重发」）。</summary>
    NoSession,
    /// <summary>恢复既有会话（方案 b，PRD 15.4.6；仅在 CLI 支持且配置开启时，否则回退方案 a）。</summary>
    Resume
}

/// <summary>
/// 断点会话恢复策略（PRD 15.4.6，方案 b）。
/// 默认关闭（EnableSessionResume=false），任何不满足条件的情形一律回退方案 a（完整指令重发），保证向后兼容与可灰度。
/// 「真实 CLI 适配器声明支持恢复（CanResume）」的前置验证 M0 V-8 属计划性延期，
/// 登记于「TECH-DEBT-技术债务治理方案-v2.0.md §4.7.1 计划性延期台账 #D2」（P2-26）。
/// </summary>
public sealed class SessionResumePolicy
{
    /// <summary>是否启用会话恢复；false 时 <see cref="Resolve"/> 恒返回 <see cref="SessionResumeDecision.NoSession"/>。</summary>
    public bool EnableSessionResume { get; }

    /// <summary>构造策略；enableSessionResume 为 false 时一律回退「完整指令重发」。</summary>
    /// <param name="enableSessionResume">是否启用会话恢复（默认 false，走完整指令重发）。</param>
    public SessionResumePolicy(bool enableSessionResume) => EnableSessionResume = enableSessionResume;

    /// <summary>
    /// 恢复决策：开关开启、会话存在、会话 adapter 与当前一致且 CLI 声明可恢复时才返回
    /// <see cref="SessionResumeDecision.Resume"/>，否则一律回退 <see cref="SessionResumeDecision.NoSession"/>。
    /// </summary>
    public SessionResumeDecision Resolve(
        SessionHandle? session,
        AgentKind adapterKind,
        Func<SessionHandle, bool> canResume)
    {
        if (!EnableSessionResume || session is null)
        {
            return SessionResumeDecision.NoSession;
        }

        if (!string.Equals(session.AdapterKind, adapterKind.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return SessionResumeDecision.NoSession; // 跨 adapter 不可恢复
        }

        if (!canResume(session))
        {
            return SessionResumeDecision.NoSession; // CLI 无法恢复 → 回退方案 a
        }

        return SessionResumeDecision.Resume;
    }
}
