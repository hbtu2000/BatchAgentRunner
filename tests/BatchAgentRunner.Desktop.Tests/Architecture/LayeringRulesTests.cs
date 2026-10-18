using System.Reflection;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Architecture;

/// <summary>
/// 架构约束测试（治理方案 v2.0 §4.3「第三道墙」）：Desktop 层不得直接持有 Infrastructure/Agents 具体类型，
/// 具体装配必须集中在 EngineFactory（阶段 2 P1-4/P1-5）。用反射检查 EngineHost 的成员类型，CI 友好。
/// </summary>
public class LayeringRulesTests
{
    [Fact]
    public void EngineHost_成员类型不得为_Infrastructure或Agents具体类型()
    {
        // 阶段 2 P1-4/P1-5：组合根已下沉到 EngineFactory，EngineHost 只应持 Core 抽象契约与 Desktop 自身类型。
        // 若本断言失败，说明又有 Infrastructure/Agents 具体类型被直接塞进 EngineHost 字段/属性。
        var hostType = typeof(BatchAgentRunner.Desktop.Services.EngineHost);
        var memberTypes = hostType
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(f => f.FieldType)
            .Concat(hostType
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(p => p.PropertyType))
            .ToArray();

        var leaked = memberTypes
            .Where(t => t.Namespace is not null &&
                        (t.Namespace.StartsWith("BatchAgentRunner.Infrastructure") ||
                         t.Namespace.StartsWith("BatchAgentRunner.Agents")))
            .Select(t => t.FullName)
            .Distinct()
            .ToArray();

        Assert.Empty(leaked);
    }
}
