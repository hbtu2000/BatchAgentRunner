using System.Reflection;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Architecture;

/// <summary>
/// 架构约束测试（治理方案 v2.0 §4.3「第三道墙」）：Desktop 层不得直接持有 Infrastructure/Agents 具体类型，
/// 具体装配必须集中在 EngineFactory（阶段 2 P1-4/P1-5）。用反射检查 EngineHost 的成员类型，CI 友好。
/// 另固化 P1-3 的过渡期约束（§4.3 / §3.4）：strangler fig 期间新代码一律只依赖
/// <see cref="BatchAgentRunner.Core.Abstractions.IBatchEngine"/>，仅组合根 <c>EngineFactory</c> 可
/// <c>new BatchEngine</c>，<c>BatchEngine</c> 具体类型不得泄漏到 UI 层其它类型。
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

    [Fact]
    public void Desktop程序集_除组合根EngineFactory外_不得直接引用BatchEngine具体类型()
    {
        // §4.3 / §3.4（P1-3 过渡期约束）：strangler fig 期间新代码一律依赖 IBatchEngine，
        // 仅组合根 EngineFactory 允许 new BatchEngine；其余 Desktop 类型（EngineHost/IEngineHost 等）
        // 必须只持有/返回 IBatchEngine，不得让 BatchEngine 具体类型泄漏到 UI 层。
        var desktop = typeof(BatchAgentRunner.Desktop.Services.EngineHost).Assembly;
        var concrete = typeof(BatchAgentRunner.Core.Engine.BatchEngine);
        var compositionRoot = typeof(BatchAgentRunner.Desktop.Composition.EngineFactory);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in desktop.GetTypes())
        {
            if (type == compositionRoot) continue; // 组合根是唯一允许引用具体类型的点

            foreach (var member in type.GetMembers(flags))
            {
                if ((member is FieldInfo fi && fi.FieldType == concrete) ||
                    (member is PropertyInfo pi && pi.PropertyType == concrete))
                {
                    Assert.Fail($"{type.FullName}.{member.Name} 直接引用了 BatchEngine 具体类型，违反 §4.3（P1-3）：应依赖 IBatchEngine");
                }

                if (member is MethodInfo method)
                {
                    if (method.ReturnType == concrete)
                    {
                        Assert.Fail($"{type.FullName}.{method.Name} 返回 BatchEngine 具体类型，违反 §4.3（P1-3）：应返回 IBatchEngine");
                    }

                    foreach (var p in method.GetParameters())
                    {
                        if (p.ParameterType == concrete)
                        {
                            Assert.Fail($"{type.FullName}.{method.Name} 的参数引用了 BatchEngine 具体类型，违反 §4.3（P1-3）：应依赖 IBatchEngine");
                        }
                    }

                    MethodBody? body;
                    try { body = method.GetMethodBody(); }
                    catch { continue; }
                    if (body is null) continue;

                    foreach (var local in body.LocalVariables)
                    {
                        if (local.LocalType == concrete)
                        {
                            Assert.Fail($"{type.FullName}.{method.Name} 的局部变量引用了 BatchEngine 具体类型，违反 §4.3（P1-3）：应依赖 IBatchEngine");
                        }
                    }
                }
            }
        }
    }
}
