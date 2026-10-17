using BatchAgentRunner.Core.Domain;

namespace BatchAgentRunner.Core.Scheduling;

/// <summary>会话恢复决策结果。</summary>
public enum SessionResumeDecision
{
    /// <summary>不恢复：以全新会话执行（一期方案 a，等价「完整指令重发」）。</summary>
    NoSession,
    /// <summary>恢复既有会话（二期方案 b，仅在 CLI 支持且配置开启时）。</summary>
    Resume
}

/// <summary>
/// 断点会话恢复策略（二期：PRD 15.4.6）。
/// 默认关闭（EnableSessionResume=false），任何不满足条件的情形一律回退一期方案 a，保证向后兼容与可灰度。
/// </summary>
public sealed class SessionResumePolicy
{
    public bool EnableSessionResume { get; }

    public SessionResumePolicy(bool enableSessionResume) => EnableSessionResume = enableSessionResume;

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
