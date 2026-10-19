using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Architecture;

/// <summary>
/// 架构约束测试（治理方案 v2.0 §4.3「第三道墙」）：Desktop 层不得直接持有 Infrastructure/Agents 具体类型，
/// 具体装配必须集中在 EngineFactory（阶段 2 P1-4/P1-5）。用反射检查 EngineHost 的成员类型，CI 友好。
/// 另固化 P1-3 的过渡期约束（§4.3 / §3.4）：strangler fig 期间新代码一律只依赖
/// <see cref="BatchAgentRunner.Core.Abstractions.IBatchEngine"/>，仅组合根 <c>EngineFactory</c> 可
/// <c>new BatchEngine</c>，<c>BatchEngine</c> 具体类型不得泄漏到 UI 层其它类型。
/// 以及 P1-4/P1-5 收口断言（2026-10-19）：除组合根命名空间外，任何方法体不得直接 <c>new</c>
/// Infrastructure/Agents 具体类型（IL 扫描 <c>newobj</c>/<c>newarr</c> 并解析 token）。
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

    [Fact]
    public void Desktop程序集_除组合根外_不得直接new_Infrastructure或Agents具体类型()
    {
        // §4.3 表格原列、此前未落地的断言：「Desktop 不得直接 new Infrastructure 具体类型（须经接口）」。
        // P1-4/P1-5 收口（2026-10-19）：组合根下沉 Desktop/Composition 命名空间后，其余 Desktop 代码
        // （EngineHost / CredentialVaultHost / 各 ViewModel）不得再直接实例化 Infrastructure/Agents
        // 具体类型，只允许经接口/工厂获取。扫描全部方法体（含构造器、属性访问器、编译器生成的闭包类）
        // IL 中的 newobj / newarr 指令并解析元数据 token；组合根命名空间是唯一豁免位置。
        var desktop = typeof(BatchAgentRunner.Desktop.Services.EngineHost).Assembly;
        var forbidden = new[] { "BatchAgentRunner.Infrastructure", "BatchAgentRunner.Agents" };
        var violations = new List<string>();

        foreach (var type in desktop.GetTypes())
        {
            if (type.Namespace is { } ns &&
                ns.StartsWith("BatchAgentRunner.Desktop.Composition", StringComparison.Ordinal))
            {
                continue; // 组合根：§4.3 唯一豁免位置
            }

            foreach (var method in EnumerateMethods(type))
            {
                foreach (var instantiated in ScanInstantiations(method))
                {
                    // 排除委托实例化：捕获型 lambda 经编译器转换为 newobj（如 GuidedLoginAsync 把校验
                    // 回调传给 RunAsync），源码并无 new；且回调委托类型是协作者签名强制的契约，
                    // 无法按「须经接口」替换。规则目标为「UI 实例化 Infrastructure/Agents 实现类」。
                    if (typeof(MulticastDelegate).IsAssignableFrom(instantiated))
                    {
                        continue;
                    }

                    if (forbidden.Any(p => instantiated.Namespace?.StartsWith(p, StringComparison.Ordinal) == true))
                    {
                        violations.Add($"{type.FullName}.{method.Name} 直接 new {instantiated.FullName}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    private static IEnumerable<MethodBase> EnumerateMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                   BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var ctor in type.GetConstructors(flags))
        {
            yield return ctor;
        }

        foreach (var method in type.GetMethods(flags))
        {
            yield return method;
        }
    }

    /// <summary>操作码值 → <see cref="OpCode"/> 查表（由 <see cref="OpCodes"/> 反射自建，避免手抄操作码表）。</summary>
    private static readonly Dictionary<short, OpCode> OpCodeMap = CreateOpCodeMap();

    private static Dictionary<short, OpCode> CreateOpCodeMap()
    {
        var map = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op)
            {
                map[op.Value] = op;
            }
        }

        return map;
    }

    /// <summary>
    /// 扫描方法体 IL，产出被直接实例化（<c>newobj</c> / <c>newarr</c>）的类型。
    /// 只覆盖「直接 new」语义：反射调用（<c>Activator.CreateInstance</c>）与静态工厂方法调用不在规则内。
    /// </summary>
    private static IEnumerable<Type> ScanInstantiations(MethodBase method)
    {
        byte[] il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        }
        catch
        {
            yield break;
        }

        if (il.Length == 0)
        {
            yield break;
        }

        var module = method.Module;
        var position = 0;
        while (position < il.Length)
        {
            var lead = il[position];
            short value;
            int opSize;
            if (lead == 0xFE)
            {
                if (position + 1 >= il.Length)
                {
                    yield break;
                }

                value = (short)(0xFE00 | il[position + 1]);
                opSize = 2;
            }
            else
            {
                value = lead;
                opSize = 1;
            }

            if (!OpCodeMap.TryGetValue(value, out var op))
            {
                yield break; // 无法识别的字节序列：放弃（编译器产物均为良构 IL，理论不可达）
            }

            var operandStart = position + opSize;

            if (op == OpCodes.Newobj || op == OpCodes.Newarr)
            {
                if (operandStart + 4 > il.Length)
                {
                    yield break;
                }

                var token = BitConverter.ToInt32(il, operandStart);
                Type? target = null;
                try
                {
                    target = op == OpCodes.Newobj
                        ? module.ResolveMethod(token)?.DeclaringType
                        : module.ResolveType(token);
                }
                catch
                {
                    // token 无法解析（如无泛型上下文的 MethodSpec）：跳过该指令继续扫描
                }

                if (target is not null)
                {
                    yield return target;
                }
            }

            if (op.OperandType == OperandType.InlineSwitch)
            {
                if (operandStart + 4 > il.Length)
                {
                    yield break;
                }

                var count = BitConverter.ToInt32(il, operandStart);
                position = operandStart + 4 + (count * 4);
                continue;
            }

            position = operandStart + OperandSize(op);
        }
    }

    private static int OperandSize(OpCode op) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget => 1,
        OperandType.ShortInlineI => 1,
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget => 4,
        OperandType.InlineField => 4,
        OperandType.InlineI => 4,
        OperandType.InlineMethod => 4,
        OperandType.InlineSig => 4,
        OperandType.InlineString => 4,
        OperandType.InlineTok => 4,
        OperandType.InlineType => 4,
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 => 8,
        OperandType.InlineR => 8,
        _ => 0,
    };
}
