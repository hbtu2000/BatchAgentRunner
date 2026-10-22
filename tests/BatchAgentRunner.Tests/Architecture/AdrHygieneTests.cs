using System.Text.RegularExpressions;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// ADR 机制守卫（ARCH P-14 / A-12）。
/// </summary>
/// <remarks>
/// §5.8 要求「设计文档与代码同步靠机器守卫，而非人肉」。本守卫把 ADR 的四条机制固化为不变量：
/// <list type="bullet">
///   <item><b>编号</b>：文件名一律 <c>ADR-NNNN-slug.md</c>，从 0001 起连续、不复用不删号（决策可追溯）；</item>
///   <item><b>结构</b>：必备「状态 / 背景 / 决策 / 后果」四段，状态取值受限于约定集合；</item>
///   <item><b>索引</b>：<c>docs/adr/README.md</c> 与本目录文件双向一一对应（漏登记、悬挂链接均红）；</item>
///   <item><b>模板</b>：<c>template.md</c> 必须存在，保证新增 ADR 有统一骨架。</item>
/// </list>
/// 这样「新增决策未写 ADR」「ADR 写了没登记」「索引指向已删文件」都会在 CI 单测桶内立刻暴露。
/// </remarks>
public class AdrHygieneTests
{
    /// <summary>ADR 文件名格式：ADR-0001-kebab-slug.md。</summary>
    private static readonly Regex FileNamePattern = new(@"^ADR-(\d{4})-[a-z0-9]+(?:-[a-z0-9]+)*\.md$", RegexOptions.Compiled);

    /// <summary>README 索引表中的 ADR 链接。</summary>
    private static readonly Regex IndexLinkPattern = new(@"\]\((ADR-\d{4}-[a-z0-9-]+\.md)\)", RegexOptions.Compiled);

    /// <summary>允许的状态取值（状态机；取代关系写在正文「关联」中）。</summary>
    private static readonly string[] AllowedStatusTokens = ["提议", "已接受", "已废弃", "已取代"];

    private static readonly string[] RequiredSections = ["## 背景", "## 决策", "## 后果"];

    [Fact]
    public void ADR文件名_编号连续且格式合规()
    {
        var numbers = AdrFiles()
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var match = FileNamePattern.Match(name);
                Assert.True(match.Success, $"ADR 文件名不合规：{name}（应为 ADR-NNNN-kebab-slug.md）");
                return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            })
            .OrderBy(n => n)
            .ToList();

        Assert.NotEmpty(numbers);
        Assert.Equal(1, numbers[0]);
        Assert.Equal(numbers.Distinct().Count(), numbers.Count);
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }

    [Fact]
    public void ADR必备章节与状态_齐全且取值合规()
    {
        foreach (var path in AdrFiles())
        {
            var text = File.ReadAllText(path);
            var name = Path.GetFileName(path);

            foreach (var section in RequiredSections)
            {
                Assert.True(
                    text.Contains(section, StringComparison.Ordinal),
                    $"{name} 缺少必备章节「{section}」——ADR 必须说清背景/决策/后果。");
            }

            var statusLine = text.Split('\n')
                .FirstOrDefault(l => l.StartsWith("- 状态：", StringComparison.Ordinal));
            Assert.NotNull(statusLine);
            Assert.Contains(
                AllowedStatusTokens,
                token => statusLine!.Contains(token, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void 索引与ADR文件_双向一一对应()
    {
        var readme = ReadAdrFile("README.md");
        var linked = IndexLinkPattern.Matches(readme)
            .Select(m => m.Groups[1].Value)
            .ToList();

        var actual = AdrFiles().Select(p => Path.GetFileName(p)!).ToList();

        Assert.Equal(actual.Distinct().Count(), linked.Distinct().Count());
        Assert.Equal(
            actual.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            linked.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void ADR模板存在()
    {
        Assert.True(File.Exists(Path.Combine(AdrDirectory(), "template.md")), "docs/adr/template.md 缺失。");
    }

    private static List<string> AdrFiles()
        => Directory.GetFiles(AdrDirectory(), "ADR-*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static string ReadAdrFile(string name)
    {
        var path = Path.Combine(AdrDirectory(), name);
        Assert.True(File.Exists(path), $"缺少 docs/adr/{name}");
        return File.ReadAllText(path);
    }

    private static string AdrDirectory() => Path.Combine(LocateRepoRoot(), "docs", "adr");

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
