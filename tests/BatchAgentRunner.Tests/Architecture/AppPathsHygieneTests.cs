using System.Reflection;
using System.Text.RegularExpressions;
using BatchAgentRunner.Infrastructure.Persistence;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// P2-31 路径常量守卫（治理方案 v2.0 §1.3 P2-31）。
/// 债务本体是「三份 <c>Json*Store</c> 逐行同构 + <c>"BatchAgentRunner"</c> 路径魔法串散落 9 处」——
/// 改一处必漏改两处。本守卫把收口结果固化为两条断言：
///
/// ① **魔法串清零**：<c>src</c> 内除 <c>AppPaths.cs</c>（唯一声明点）外，
///    不得再出现应用名字面量 <c>"BatchAgentRunner"</c>；
/// ② **样板清零**：三份 <c>Json*Store</c> 不得再自带「<c>Directory.CreateDirectory</c> /
///    <c>JsonSerializer</c> / <c>AtomicFileWriter</c>」加载与原子写样板，
///    且必须继承 <see cref="JsonStoreBase{T}"/>；
/// ③ **第 4 份同构仓储拦截**：② 按文件名清单核对，新增第 4 份仓储会静默漏网——
///    故再按「类型名 <c>Json*Store</c>」扫描 Infrastructure 程序集，
///    凡具体仓储一律要求继承 <see cref="JsonStoreBase{T}"/>（ARCH P-07 收口轮加固）。
///
/// 与 <see cref="EscapeValveHygieneTests"/>（管门禁例外）、<see cref="CommentHygieneTests"/>（管注释内容）并列，
/// 同属 §4.3「第三道墙：架构约束自动化」。
/// </summary>
public class AppPathsHygieneTests
{
    /// <summary>唯一允许出现应用名字符串字面量的文件（单一事实来源）。</summary>
    private const string SoleDeclarationFile = "AppPaths.cs";

    private static readonly string[] StoreFileNames =
    {
        "JsonCooldownStore.cs",
        "JsonCreditsClaimStore.cs",
        "JsonConfigStore.cs"
    };

    /// <summary>加载与原子写样板的词法特征；出现在派生仓储里即为「脚手架未被抽走」。</summary>
    private static readonly string[] BoilerplateMarkers =
    {
        "Directory.CreateDirectory",
        "JsonSerializer.",
        "AtomicFileWriter"
    };

    private static readonly Regex AppNameLiteral = new(@"""BatchAgentRunner""", RegexOptions.Compiled);

    [Fact]
    public void Src内_除AppPaths外_不得内联应用名路径魔法串()
    {
        var repoRoot = LocateRepoRoot();
        var violations = new List<string>();

        foreach (var file in SourceFiles(repoRoot))
        {
            if (string.Equals(Path.GetFileName(file), SoleDeclarationFile, StringComparison.Ordinal))
            {
                continue; // 单一事实来源本身
            }

            var relative = Path.GetRelativePath(repoRoot, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (AppNameLiteral.IsMatch(lines[i]))
                {
                    violations.Add($"{relative}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现内联的应用名路径魔法串（P2-31）：\n" + string.Join('\n', violations) +
            "\n处置：改用 AppPaths 的对应常量；新数据目录先在 AppPaths 登记，再由调用方引用。");
    }

    [Fact]
    public void AppPaths_派生目录_全部落在本地应用数据根下()
    {
        var root = BatchAgentRunner.Infrastructure.AppPaths.Root;
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchAgentRunner");

        // 守卫文件本身含该字面量（断言基线），故此处用拼接避免自指。
        Assert.Equal(expectedRoot, root);

        var properties = typeof(BatchAgentRunner.Infrastructure.AppPaths)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .ToArray();

        Assert.NotEmpty(properties);

        var problems = new List<string>();
        foreach (var p in properties)
        {
            var value = (string?)p.GetValue(null);
            if (string.IsNullOrEmpty(value))
            {
                problems.Add($"{p.Name} 为空");
                continue;
            }

            if (!value.StartsWith(root, StringComparison.Ordinal))
            {
                problems.Add($"{p.Name} = '{value}' 不以应用根 '{root}' 为前缀");
            }
        }

        Assert.True(
            problems.Count == 0,
            "AppPaths 存在脱根的目录常量：\n" + string.Join('\n', problems));
    }

    [Fact]
    public void 三份JsonStore_继承JsonStoreBase且自带加载与原子写样板清零()
    {
        var repoRoot = LocateRepoRoot();
        var baseType = typeof(JsonStoreBase<>);

        var problems = new List<string>();
        foreach (var name in StoreFileNames)
        {
            var path = Directory.GetFiles(Path.Combine(repoRoot, "src"), name, SearchOption.AllDirectories)
                .SingleOrDefault(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal));

            if (path is null)
            {
                problems.Add("未找到源码文件 " + name + "（重命名后请同步更新本守卫的 StoreFileNames）");
                continue;
            }

            var source = File.ReadAllText(path);
            var relative = Path.GetRelativePath(repoRoot, path);

            foreach (var marker in BoilerplateMarkers)
            {
                if (source.Contains(marker, StringComparison.Ordinal))
                {
                    problems.Add($"{relative} 仍自带样板：{marker}");
                }
            }

            // 类型名与文件名同名：从程序集取类型，避免硬编码命名空间。
            var type = baseType.Assembly.GetTypes()
                .SingleOrDefault(t => t.Name == Path.GetFileNameWithoutExtension(name));
            if (type is null)
            {
                problems.Add($"未在 Infrastructure 程序集中找到类型 {Path.GetFileNameWithoutExtension(name)}");
                continue;
            }

            if (type.BaseType is null || !type.BaseType.IsGenericType ||
                type.BaseType.GetGenericTypeDefinition() != baseType)
            {
                problems.Add($"{type.Name} 未继承 JsonStoreBase<T>（当前基类：{type.BaseType?.Name ?? "<无>"}）");
            }
        }

        Assert.True(
            problems.Count == 0,
            "三份 Json*Store 的重复样板未清零（P2-31）：\n" + string.Join('\n', problems) +
            "\n处置：把加载/保存样板保留在 JsonStoreBase<T>，派生类只提供 CreateEmpty() 与可选的 NormalizeAsync()。");
    }

    [Fact]
    public void Infrastructure内Json仓储具体类型_均须继承JsonStoreBase_防第四份同构仓储()
    {
        var baseType = typeof(JsonStoreBase<>);
        var stores = baseType.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } &&
                        t.Name.StartsWith("Json", StringComparison.Ordinal) &&
                        t.Name.EndsWith("Store", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(stores); // 清单本身为空说明命名约定已变，需同步本守卫

        var problems = stores
            .Where(t => t.BaseType is null || !t.BaseType.IsGenericType ||
                        t.BaseType.GetGenericTypeDefinition() != baseType)
            .Select(t => $"{t.Name}（基类：{t.BaseType?.Name ?? "<无>"}）")
            .ToArray();

        Assert.True(
            problems.Length == 0,
            "存在未继承 JsonStoreBase<T> 的单文件 JSON 仓储（ARCH P-07 防第四份同构）：\n" +
            string.Join('\n', problems) +
            "\n处置：新增单文件 JSON 仓储一律继承 JsonStoreBase<T>，只提供 CreateEmpty() 与可选的 NormalizeAsync()。");
    }

    private static List<string> SourceFiles(string repoRoot)
        => Directory.GetFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

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
