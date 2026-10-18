using System.Reflection;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// 架构约束测试（治理方案 v2.0 §4.3「第三道墙：架构约束自动化」）。
/// 用反射把分层规则固化为测试，防止 P1-4/P1-9 类倒置与进程 IO 越层复发。
/// 说明：优先用反射（不依赖源码布局，CI 友好）；进程越层等无法用反射表达的规则另在 P1-9 落地时补断言。
/// </summary>
public class LayeringRulesTests
{
    [Fact]
    public void Core程序集_不得引用_Infrastructure_Agents_Desktop()
    {
        // 分层铁律：Core 是地基，反过来引用上层（Infrastructure/Agents/Desktop）即是倒置。
        var core = typeof(BatchAgentRunner.Core.Engine.BatchEngine).Assembly;
        var forbidden = new[] { "BatchAgentRunner.Infrastructure", "BatchAgentRunner.Agents", "BatchAgentRunner.Desktop" };
        var leaked = core.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => forbidden.Contains(name))
            .ToArray();
        Assert.Empty(leaked);
    }
}
