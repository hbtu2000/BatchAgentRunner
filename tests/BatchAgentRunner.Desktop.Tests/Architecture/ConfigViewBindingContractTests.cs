using System.Collections.Generic;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Architecture;

/// <summary>
/// P-04 视图↔VM 绑定契约守卫（R-4 防回归）。
/// <para>
/// Desktop 使用反射绑定（<c>AvaloniaUseCompiledBindingsByDefault=false</c>，见 Desktop.csproj 注释）：
/// 绑定路径写错不会产生编译错误，只会在运行时静默失效（控件空白 / 命令无反应），
/// 这正是 P-04 分解 <c>ConfigViewModel</c> 时最大的行为破坏向量。本守卫经
/// <see cref="XamlBindingPathChecker"/> 静态解析 <c>ConfigView.axaml</c> / <c>ConfigHubView.axaml</c>
/// 的全部 <c>{Binding 路径}</c> 并逐一校验可解析，把「UI 行为等价」从人工 review 变为机器断言。
/// </para>
/// <para>
/// 覆盖范围说明：只校验「路径存在性」这一最易随 VM 分解漂移的维度；转换器 / 命令参数 /
/// 可见性语义由各视图契约测试（<c>ConfigViewTests</c> 等）与 VM 白箱测试守护。
/// </para>
/// </summary>
public class ConfigViewBindingContractTests
{
    /// <summary>Config 两个视图文件里的 XML 前缀 → CLR 命名空间（与 axaml 文件头的 xmlns 声明一致）。</summary>
    private static readonly Dictionary<string, string> PrefixToNamespace = new()
    {
        ["vm"] = "BatchAgentRunner.Desktop.ViewModels",
        ["models"] = "BatchAgentRunner.Desktop.Models",
        ["domain"] = "BatchAgentRunner.Core.Domain",
    };

    [Fact]
    public void ConfigView与ConfigHubView_全部Binding路径可解析()
    {
        var repoRoot = XamlBindingPathChecker.LocateRepoRoot();
        var errors = new List<string>();

        XamlBindingPathChecker.Check(repoRoot, "src/BatchAgentRunner.Desktop/Views/ConfigView.axaml",
            typeof(BatchAgentRunner.Desktop.ViewModels.ConfigViewModel), PrefixToNamespace, errors);
        XamlBindingPathChecker.Check(repoRoot, "src/BatchAgentRunner.Desktop/Views/ConfigHubView.axaml",
            typeof(BatchAgentRunner.Desktop.ViewModels.ConfigHubViewModel), PrefixToNamespace, errors);

        Assert.True(errors.Count == 0,
            $"以下 Binding 路径无法解析（VM 分解后视图绑定面漂移？）：\n{string.Join("\n", errors)}");
    }
}
