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

    /// <summary>
    /// §3.4 第 5 条 + P-02 退出标准：<see cref="BatchEngine"/> facade 文件硬上限 400 行，防止上帝类回潮。
    /// 扫描源文件**代码行数**（排除 XML 文档注释行；行为等价性由契约测试守护，此处只守「代码规模」这道量化的架构闸门）。
    /// </summary>
    [Fact]
    public void BatchEngine_facade文件不得超过400行_防上帝类回潮()
    {
        const int facadeLineCap = 400;
        var path = LocateEngineSource();
        Assert.True(File.Exists(path), $"未找到 BatchEngine.cs 源文件：{path}（守卫测试须在仓库检出内运行）");

        var lines = CountCodeLines(path);
        Assert.True(lines <= facadeLineCap,
            $"BatchEngine.cs 共 {lines} 行（不含 XML 文档注释），超过 P-02 上帝类收口上限 {facadeLineCap} 行；请继续下沉编排到协作者而非在此膨胀。");
    }

    /// <summary>
    /// 统计源码的**代码行数**（排除 `///` 的 XML 文档注释行）。
    /// 口径说明（ARCH P-12）：CS1591 恢复后公共 API 文档是强制项，文档行数由「API 面大小」决定、
    /// 与上帝类回潮无关；若计入闸门，等于用「文档写得全」惩罚架构约束。故本闸门只量代码行，
    /// 空行与普通注释仍计入（它们不会因补文档而机械膨胀）。
    /// </summary>
    private static int CountCodeLines(string path) =>
        File.ReadAllLines(path).Count(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal));

    private static string LocateEngineSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "BatchAgentRunner.Core", "Engine", "BatchEngine.cs");
    }

    /// <summary>
    /// §3.4 第 5 条 + P-03 退出标准（与 <see cref="BatchEngine_facade文件不得超过400行_防上帝类回潮"/> 同款闸门）：
    /// <see cref="TaskRunner"/> facade 文件硬上限 400 行，防止从 <c>TaskRunner</c> 抽出的决策/协作逻辑回流膨胀。
    /// P-03 已将降级/冷却/兜底/免费时段判定下沉为 <c>DegradationPolicy</c> 等，状态机本体移入 <c>TaskStepExecutor</c>，
    /// <c>TaskRunner</c> 仅作薄 facade；此处守「行数规模」这道量化的架构闸门。
    /// </summary>
    [Fact]
    public void TaskRunner_facade文件不得超过400行_防上帝类回潮()
    {
        const int facadeLineCap = 400;
        var path = LocateTaskRunnerSource();
        Assert.True(File.Exists(path), $"未找到 TaskRunner.cs 源文件：{path}（守卫测试须在仓库检出内运行）");

        var lines = CountCodeLines(path);
        Assert.True(lines <= facadeLineCap,
            $"TaskRunner.cs 共 {lines} 行（不含 XML 文档注释），超过 P-03 上帝类收口上限 {facadeLineCap} 行；请继续下沉决策到策略对象而非在此膨胀。");
    }

    private static string LocateTaskRunnerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "BatchAgentRunner.Core", "Engine", "TaskRunner.cs");
    }

    /// <summary>
    /// §3.4 第 5 条 + P-04 退出标准（与 <see cref="BatchEngine_facade文件不得超过400行_防上帝类回潮"/> 同款闸门）：
    /// <see cref="ConfigViewModel"/> facade 文件硬上限 400 行，防止 UI 上帝类回潮。
    /// P-04 已将 9 个配置域拆为 <c>BatchAgentRunner.Desktop.ViewModels.Config</c> 下的子编辑器，
    /// <c>ConfigViewModel</c> 退为「状态持有 + 绑定转发 + 跨域编排」薄 facade；此处守「行数规模」这道量化的架构闸门。
    /// </summary>
    [Fact]
    public void ConfigViewModel_facade文件不得超过400行_防上帝类回潮()
    {
        const int facadeLineCap = 400;
        var path = LocateConfigViewModelSource();
        Assert.True(File.Exists(path), $"未找到 ConfigViewModel.cs 源文件：{path}（守卫测试须在仓库检出内运行）");

        var lines = CountCodeLines(path);
        Assert.True(lines <= facadeLineCap,
            $"ConfigViewModel.cs 共 {lines} 行（不含 XML 文档注释），超过 P-04 上帝类收口上限 {facadeLineCap} 行；请继续下沉配置域到子编辑器而非在此膨胀。");
    }

    private static string LocateConfigViewModelSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "BatchAgentRunner.Desktop", "ViewModels", "ConfigViewModel.cs");
    }

    /// <summary>
    /// §3.4 第 5 条 + P-05 退出标准（与 <see cref="ConfigViewModel_facade文件不得超过400行_防上帝类回潮"/> 同款闸门）：
    /// <see cref="TemplateEditorViewModel"/> facade 文件硬上限 400 行，防止 UI 上帝类回潮。
    /// P-05 已将模板加载 / 变量展开 / 流程示意构建三类交互逻辑下沉为
    /// <c>BatchAgentRunner.Desktop.ViewModels.TemplateEditor</c> 下的三个 Coordinator，
    /// <c>TemplateEditorViewModel</c> 退为「状态持有 + 绑定转发 + 编排」薄 facade；此处守「行数规模」这道量化的架构闸门。
    /// </summary>
    [Fact]
    public void TemplateEditorViewModel_facade文件不得超过400行_防上帝类回潮()
    {
        const int facadeLineCap = 400;
        var path = LocateTemplateEditorViewModelSource();
        Assert.True(File.Exists(path), $"未找到 TemplateEditorViewModel.cs 源文件：{path}（守卫测试须在仓库检出内运行）");

        var lines = CountCodeLines(path);
        Assert.True(lines <= facadeLineCap,
            $"TemplateEditorViewModel.cs 共 {lines} 行（不含 XML 文档注释），超过 P-05 上帝类收口上限 {facadeLineCap} 行；请继续下沉交互逻辑到 Coordinator 而非在此膨胀。");
    }

    private static string LocateTemplateEditorViewModelSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "BatchAgentRunner.Desktop", "ViewModels", "TemplateEditorViewModel.cs");
    }

    /// <summary>
    /// §3.4 第 5 条 + P-06 退出标准（与 <see cref="ConfigViewModel_facade文件不得超过400行_防上帝类回潮"/> 同款闸门）：
    /// <see cref="MonitorViewModel"/> facade 文件硬上限 400 行，防止 UI 上帝类回潮。
    /// P-06 已将原 983 行上帝类下沉为 <c>BatchAgentRunner.Desktop.ViewModels.Monitor</c> 下的协作者
    /// （输出流 / 统计 / 批次状态 / 提示板 / 任务列表 / 操作编排），facade 仅「状态持有 + 绑定转发 + 事件编排」。
    /// </summary>
    [Fact]
    public void MonitorViewModel_facade文件不得超过400行_防上帝类回潮()
    {
        const int facadeLineCap = 400;
        var path = LocateViewModelSource("MonitorViewModel.cs");
        Assert.True(File.Exists(path), $"未找到 MonitorViewModel.cs 源文件：{path}（守卫测试须在仓库检出内运行）");

        var lines = CountCodeLines(path);
        Assert.True(lines <= facadeLineCap,
            $"MonitorViewModel.cs 共 {lines} 行（不含 XML 文档注释），超过 P-06 上帝类收口上限 {facadeLineCap} 行；请继续下沉交互逻辑到 Coordinator 而非在此膨胀。");
    }

    /// <summary>
    /// §3.4 第 5 条 + P-06 退出标准（与 <see cref="ConfigViewModel_facade文件不得超过400行_防上帝类回潮"/> 同款闸门）：
    /// <see cref="LoginViewModel"/> facade 文件硬上限 400 行，防止 UI 上帝类回潮。
    /// P-06 已将原 751 行上帝类下沉为 <c>BatchAgentRunner.Desktop.ViewModels.Login</c> 下的协作者
    /// （一键登录流程 / 积分领取流程 / 账号池 / 步骤条）+ 三个行视图模型独立文件。
    /// </summary>
    [Fact]
    public void LoginViewModel_facade文件不得超过400行_防上帝类回潮()
    {
        const int facadeLineCap = 400;
        var path = LocateViewModelSource("LoginViewModel.cs");
        Assert.True(File.Exists(path), $"未找到 LoginViewModel.cs 源文件：{path}（守卫测试须在仓库检出内运行）");

        var lines = CountCodeLines(path);
        Assert.True(lines <= facadeLineCap,
            $"LoginViewModel.cs 共 {lines} 行（不含 XML 文档注释），超过 P-06 上帝类收口上限 {facadeLineCap} 行；请继续下沉交互逻辑到 Coordinator 而非在此膨胀。");
    }

    private static string LocateViewModelSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "BatchAgentRunner.Desktop", "ViewModels", fileName);
    }
}
