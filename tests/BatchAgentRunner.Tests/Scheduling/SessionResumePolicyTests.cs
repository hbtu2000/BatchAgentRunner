using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using Xunit;

namespace BatchAgentRunner.Tests.Scheduling;

/// <summary>
/// P2-26 防回归：会话恢复策略（方案 b）的「默认关 + 全条件回退方案 a」语义。
/// 锁死注释声称的能力边界——真实 CLI 适配器的 CanResume 启用属计划性延期
/// （TECH-DEBT v2.0 §4.7 计划性延期台账 #D2），在适配器重写前本组语义不得变化。
/// </summary>
public class SessionResumePolicyTests
{
    private static readonly SessionHandle Handle = new()
    {
        AdapterKind = nameof(AgentKind.CodeBuddy),
        SessionId = "sess-1"
    };

    private static SessionResumeDecision Resolve(
        bool enable,
        SessionHandle? session,
        Func<SessionHandle, bool>? canResume = null) =>
        new SessionResumePolicy(enableSessionResume: enable).Resolve(
            session,
            AgentKind.CodeBuddy,
            canResume ?? (_ => true));

    [Fact]
    public void 默认关闭_不恢复()
    {
        // EnableSessionResume=false（默认值语义）：即便一切就绪也回退方案 a
        Assert.Equal(SessionResumeDecision.NoSession, Resolve(enable: false, Handle));
    }

    [Fact]
    public void 开启但无会话句柄_不恢复()
    {
        Assert.Equal(SessionResumeDecision.NoSession, Resolve(enable: true, session: null));
    }

    [Fact]
    public void 开启且CLI不支持恢复_回退方案a()
    {
        // M0 V-8 验证完成前真实适配器的默认形态（CanResume => false）
        Assert.Equal(
            SessionResumeDecision.NoSession,
            Resolve(enable: true, Handle, canResume: _ => false));
    }

    [Fact]
    public void 跨adapter会话_不恢复()
    {
        var decision = new SessionResumePolicy(enableSessionResume: true).Resolve(
            Handle,
            AgentKind.WorkBuddy, // 会话属另一 CLI，跨 adapter 不可恢复
            _ => true);

        Assert.Equal(SessionResumeDecision.NoSession, decision);
    }

    [Fact]
    public void 开启且CLI支持且有会话_恢复()
    {
        // 唯一进入方案 b 的路径：配置开 + 句柄在 + 同 adapter + CLI 声明支持
        Assert.Equal(
            SessionResumeDecision.Resume,
            Resolve(enable: true, Handle, canResume: _ => true));
    }
}
