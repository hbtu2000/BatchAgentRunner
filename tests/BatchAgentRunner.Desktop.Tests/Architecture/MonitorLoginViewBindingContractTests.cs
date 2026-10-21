using System.Collections.Generic;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Architecture;

/// <summary>
/// P-06 视图↔VM 绑定契约守卫（R-4 防回归，Monitor/Login 分解准入）。
/// <para>
/// 与 <see cref="ConfigViewBindingContractTests"/>（P-04）同款：Desktop 反射绑定下，
/// VM 分解（facade 属性转发 / 协作者下沉）最大的行为破坏向量是「绑定路径静默失效」。
/// 本守卫经 <see cref="XamlBindingPathChecker"/> 静态解析 <c>MonitorView.axaml</c> 与
/// <c>LoginView.axaml</c> 的全部 <c>{Binding 路径}</c>（含 DataTemplate 经 ItemsSource
/// 推断元素类型的作用域），逐一校验可在对应 CLR 类型上解析——覆盖 P-06 两个目标 VM
/// 的完整视图绑定面（Monitor facade 的统计/状态/输出流/任务列表转发属性，Login facade
/// 的步骤条/账号行/积分行绑定）。
/// </para>
/// </summary>
public class MonitorLoginViewBindingContractTests
{
    /// <summary>两个视图文件里的 XML 前缀 → CLR 命名空间（与 axaml 文件头的 xmlns 声明一致）。</summary>
    private static readonly Dictionary<string, string> PrefixToNamespace = new()
    {
        ["vm"] = "BatchAgentRunner.Desktop.ViewModels",
        ["models"] = "BatchAgentRunner.Desktop.Models",
        ["domain"] = "BatchAgentRunner.Core.Domain",
    };

    [Fact]
    public void MonitorView与LoginView_全部Binding路径可解析()
    {
        var repoRoot = XamlBindingPathChecker.LocateRepoRoot();
        var errors = new List<string>();

        XamlBindingPathChecker.Check(repoRoot, "src/BatchAgentRunner.Desktop/Views/MonitorView.axaml",
            typeof(BatchAgentRunner.Desktop.ViewModels.MonitorViewModel), PrefixToNamespace, errors);
        XamlBindingPathChecker.Check(repoRoot, "src/BatchAgentRunner.Desktop/Views/LoginView.axaml",
            typeof(BatchAgentRunner.Desktop.ViewModels.LoginViewModel), PrefixToNamespace, errors);

        Assert.True(errors.Count == 0,
            $"以下 Binding 路径无法解析（VM 分解后视图绑定面漂移？）：\n{string.Join("\n", errors)}");
    }
}
