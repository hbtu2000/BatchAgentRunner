using System.Reflection;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Architecture;

/// <summary>
/// Desktop 层架构守卫（治理方案 v2.0 §4.3）：
/// ① P2-7 防腐层——XAML 不得直绑领域对象 <c>TaskItem</c> 的内部成员（<c>{Binding Item.…}</c> 形态）：
/// 领域模型的 UI 契约侵入只应经行 VM 消费，展示属性一律由行 VM 暴露；
/// ② P2-8——Desktop 生产程序集不得出现 Fake* 测试替身（与 Core.Tests 侧
/// <c>LayeringRulesTests.生产程序集_不得出现_Fake测试替身类型</c> 互为补充，覆盖其未引用 Desktop 的缺口）。
/// </summary>
public class DesktopBindingRulesTests
{
    [Fact]
    public void Desktop的XAML_不得直绑TaskItem内部成员()
    {
        var desktopRoot = LocateDesktopRoot();
        Assert.True(Directory.Exists(desktopRoot), $"未找到 Desktop 源码目录：{desktopRoot}（守卫测试须在仓库检出内运行）");

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(desktopRoot, "*.axaml", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(Directory.GetParent(desktopRoot)!.FullName, file);
            var lineNo = 0;
            foreach (var line in File.ReadAllLines(file))
            {
                lineNo++;
                // 行 VM 的 Item 暴露面只允许代码侧消费；XAML 绑定触达 Item. 即越界
                if (line.Contains("{Binding Item.", StringComparison.Ordinal) ||
                    line.Contains("{Binding Path=Item.", StringComparison.Ordinal))
                {
                    violations.Add($"{relative}:{lineNo} {line.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现 XAML 直绑领域对象 TaskItem 内部成员（P2-7 防腐层）：\n" +
            string.Join('\n', violations) +
            "\n处置：在行 VM（TaskRowViewModel）补展示属性透传，XAML 改绑行 VM 属性。");
    }

    [Fact]
    public void Desktop程序集_不得出现_Fake测试替身类型()
    {
        var desktop = typeof(BatchAgentRunner.Desktop.ViewModels.MainViewModel).Assembly;
        var leaked = desktop.GetTypes()
            .Where(t => t.Name.StartsWith("Fake", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToArray();
        Assert.Empty(leaked);
    }

    private static string LocateDesktopRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "BatchAgentRunner.Desktop");
    }
}
