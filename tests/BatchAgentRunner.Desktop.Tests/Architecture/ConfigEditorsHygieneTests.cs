using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BatchAgentRunner.Desktop.Models;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Architecture;

/// <summary>
/// P-09「<c>ConfigEditors</c> 泛型化 + 统一校验反馈」的防回潮守卫（对应 P-08 的 <c>DomainPrimitivesHygieneTests</c>）。
/// <para>锁死三个「加字段必漏改 / 加编辑器必返工」的向量：</para>
/// ①<b>解析单一来源</b>：编辑器内不得再手写 <c>X.TryParse</c> —— 解析一律走 <see cref="FieldParsers"/>；
/// ②<b>校验反馈可见</b>：凡在视图里绑定 <c>FieldEditor&lt;T&gt;.Text</c> 的输入框，同一模板内必须呈现其
/// <c>Error</c>/<c>HasError</c>，杜绝「输入非法但界面毫无提示、<c>Apply()</c> 又静默覆盖」；
/// ③<b>声明即回写</b>：新声明的 <c>FieldEditor&lt;T&gt;</c> 字段必须在 <c>Apply()</c> 被读取，
/// 否则用户填了值却从未落盘（P2-15 前「改字段必漏改」的编辑器侧形态）。
/// </summary>
public class ConfigEditorsHygieneTests
{
    /// <summary>形如 <c>decimal.TryParse(…)</c> 的解析调用点。</summary>
    private static readonly Regex TryParseCall = new(@"[\w\)]\.TryParse\s*\(", RegexOptions.Compiled);

    /// <summary>形如 <c>public FieldEditor&lt;decimal?&gt; UnitPrice { get; }</c> 的字段声明。</summary>
    private static readonly Regex FieldDeclaration =
        new(@"\bpublic\s+FieldEditor<[^>]*>\s+(\w+)\s*(?:=>|\{)", RegexOptions.Compiled);

    [Fact]
    public void Desktop内_解析调用_只出现在FieldParsers单一事实来源()
    {
        var offenders = SourceFiles()
            .Where(f => !string.Equals(Path.GetFileName(f), "FieldEditor.cs", StringComparison.Ordinal))
            .Select(f => (Path: Describe(f), Hits: TryParseCall.Count(File.ReadAllText(f))))
            .Where(x => x.Hits > 0)
            .Select(x => $"{x.Path}：{x.Hits} 处解析调用（应改走 FieldParsers + FieldEditor<T>）")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void 编辑器_解析字段_一律参与回写_声明即须被读取()
    {
        var offenders = SourceFiles()
            .Select(f => (Path: Describe(f), Text: File.ReadAllText(f)))
            .SelectMany(f => FieldDeclaration.Matches(f.Text).Cast<Match>()
                .Select(m => (f.Path, Field: m.Groups[1].Value, f.Text)))
            .Where(x => !IsRead(x.Text, x.Field))
            .Select(x => $"{x.Path}：字段 {x.Field} 声明后无任何读取点（Apply() 漏写 → 用户输入永不落盘）")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void 配置视图_凡绑定解析字段文本的输入框_必须呈现校验反馈()
    {
        var fields = FieldEditorPropertyNames();
        var offenders = new List<string>();

        foreach (var view in Directory.GetFiles(
                     Path.Combine(LocateRepoRoot(), "src", "BatchAgentRunner.Desktop", "Views"), "*.axaml"))
        {
            var document = XDocument.Load(view);

            // 按「最近祖先 DataTemplate」分组：校验反馈只要求与输入框同处一个数据模板
            var groups = new Dictionary<XElement, List<string>>();
            foreach (var element in document.Descendants())
            {
                if (!groups.TryGetValue(EnclosingTemplate(element, document.Root!), out var paths))
                {
                    paths = new List<string>();
                    groups[EnclosingTemplate(element, document.Root!)] = paths;
                }

                foreach (var attribute in element.Attributes())
                {
                    if (ExtractBindingPath(attribute.Value) is { } path)
                    {
                        paths.Add(path);
                    }
                }
            }

            foreach (var (template, paths) in groups)
            {
                var feedback = paths
                    .Where(p => p.EndsWith(".Error", StringComparison.Ordinal) ||
                                p.EndsWith(".HasError", StringComparison.Ordinal))
                    .Select(Prefix)
                    .ToHashSet(StringComparer.Ordinal);

                var missing = paths
                    .Where(p => p.EndsWith(".Text", StringComparison.Ordinal))
                    .Select(Prefix)
                    .Distinct(StringComparer.Ordinal)
                    .Where(fields.Contains)
                    .Where(f => !feedback.Contains(f))
                    .ToArray();

                foreach (var field in missing)
                {
                    offenders.Add($"{Describe(view)}：<{template.Name.LocalName}> 内 {field}.Text 有输入框，"
                                  + $"但无 {field}.Error / {field}.HasError 反馈绑定（输入非法时界面毫无提示）");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>字段是否真被 <c>Apply()</c> 读取（<c>HasError</c> / <c>Value</c> / <c>Text</c> 任一引用即算）。</summary>
    private static bool IsRead(string source, string field) =>
        source.Contains($"{field}.HasError", StringComparison.Ordinal) ||
        source.Contains($"{field}.Value", StringComparison.Ordinal) ||
        source.Contains($"{field}.Text", StringComparison.Ordinal);

    private static HashSet<string> FieldEditorPropertyNames() =>
        typeof(FieldEditor<>).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(FieldEditor<>))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static XElement EnclosingTemplate(XElement element, XElement root)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (current.Name.LocalName == "DataTemplate")
            {
                return current;
            }
        }

        return root;
    }

    /// <summary>取 <c>{Binding 路径}</c> 里的路径（含转换器参数的写法取首段）；非绑定返回 null。</summary>
    private static string? ExtractBindingPath(string value)
    {
        if (!value.StartsWith("{Binding", StringComparison.Ordinal))
        {
            return null; // {DynamicResource …} / {x:Static …} / 字面量 / {Binding}（整体绑定）
        }

        var path = value.Trim('{', '}')["Binding".Length..].Trim();
        if (path.Length == 0)
        {
            return null;
        }

        path = path.Split(',')[0].Split(' ')[0];
        if (path.Length == 0)
        {
            return null;
        }

        return path.TrimStart('!') is { Length: > 0 } trimmed ? trimmed : null;
    }

    private static string Prefix(string path) => path[..path.LastIndexOf('.')];

    private static List<string> SourceFiles()
    {
        var desktop = Path.Combine(LocateRepoRoot(), "src", "BatchAgentRunner.Desktop");

        return Directory.GetFiles(desktop, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static string Describe(string absolutePath) =>
        Path.GetRelativePath(LocateRepoRoot(), absolutePath).Replace('\\', '/');

    private static string LocateRepoRoot() => XamlBindingPathChecker.LocateRepoRoot();
}
