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
    /// <summary>主测试项目可扫描的生产程序集（Core / Infrastructure / Agents；Desktop 由 Desktop.Tests 侧同等断言覆盖）。</summary>
    private static Assembly[] ProductionAssemblies() => new[]
    {
        typeof(BatchAgentRunner.Core.Engine.BatchEngine).Assembly,
        typeof(BatchAgentRunner.Infrastructure.Logging.FileRunLogger).Assembly,
        typeof(BatchAgentRunner.Agents.Internal.CliProcessRunner).Assembly
    };

    [Fact]
    public void 生产程序集_不得出现_Fake测试替身类型()
    {
        // §4.3 / P2-8：测试替身不得进生产程序集——随生产分发的测试代码既污染依赖图，
        // 又暗示「生产路径可用假实现」。替身一律居测试项目（Helpers）。
        // 范围说明：仅禁 Fake* —— MockAdapter / MockCreditsApiClient 是生产有意的
        // AgentKind.Mock 演示适配器与本地模拟 API（用户可选），非测试替身。
        var leaked = ProductionAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.StartsWith("Fake", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToArray();
        Assert.Empty(leaked);
    }

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

    [Fact]
    public void Core程序集_不得直接使用_SystemDiagnosticsProcess()
    {
        // §4.3：Core 命名空间下不得出现 System.Diagnostics.Process（进程 IO 必须赶出 Core，经 IProcessRunner 端口）。
        // 用反射扫描 IL 局部变量 + 字段/属性/参数/返回类型，覆盖 `new Process()` 这类局部变量引用。
        var core = typeof(BatchAgentRunner.Core.Engine.BatchEngine).Assembly;
        var processType = typeof(System.Diagnostics.Process);
        var psiType = typeof(System.Diagnostics.ProcessStartInfo);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in core.GetTypes())
        {
            foreach (var member in type.GetMembers(flags))
            {
                if ((member is FieldInfo fi && (fi.FieldType == processType || fi.FieldType == psiType)) ||
                    (member is PropertyInfo pi && (pi.PropertyType == processType || pi.PropertyType == psiType)))
                {
                    Assert.Fail($"{type.FullName}.{member.Name} 直接引用了 System.Diagnostics.Process/ProcessStartInfo，违反 §4.3");
                }

                if (member is MethodInfo method)
                {
                    if (method.ReturnType == processType || method.ReturnType == psiType)
                    {
                        Assert.Fail($"{type.FullName}.{method.Name} 返回 System.Diagnostics.Process/ProcessStartInfo，违反 §4.3");
                    }

                    foreach (var p in method.GetParameters())
                    {
                        if (p.ParameterType == processType || p.ParameterType == psiType)
                        {
                            Assert.Fail($"{type.FullName}.{method.Name} 的参数引用了 System.Diagnostics.Process/ProcessStartInfo，违反 §4.3");
                        }
                    }

                    MethodBody? body;
                    try { body = method.GetMethodBody(); }
                    catch { continue; }
                    if (body is null) continue;

                    foreach (var local in body.LocalVariables)
                    {
                        if (local.LocalType == processType || local.LocalType == psiType)
                        {
                            Assert.Fail($"{type.FullName}.{method.Name} 的局部变量引用了 System.Diagnostics.Process/ProcessStartInfo，违反 §4.3");
                        }
                    }
                }
            }
        }
    }
}
