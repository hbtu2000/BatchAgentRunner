using System.Reflection;
using System.Text.RegularExpressions;
using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Infrastructure.Persistence;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// ARCH P-08 领域原语单一来源守卫（治理方案 §3.4 第 4 条「公共资源须来自单一事实来源」的
/// 「<c>Truncate</c> / 终态集合 / <c>Clone</c> / 失败分派」四个维度；「JSON 仓储 / 路径常量」两个维度见
/// <see cref="AppPathsHygieneTests"/>）。
///
/// 债务本体（§1.2 A-5）：<c>Truncate</c>×3、终态集合×3、<c>Clone</c>×6、失败分派状态机逐字重复，
/// 已于 P2-1~P2-6 收敛为内核单一实现。本守卫把「收敛结果」固化，防的是**回潮**：
/// ① 除 <c>TextTruncate</c> 外不得再内联截断；
/// ② 终态判定成员必须委派 <see cref="TaskStatusExtensions"/>，且对全部枚举值判定一致；
/// ③ 深拷贝入口只允许 <see cref="WorkflowTypeClones"/>（<c>record with</c>）与 <see cref="TaskItem"/>
///    （唯一手写点，因承载属性通知无法记录化），其余程序集不得自造第二份；
/// ④ 依赖失败分派公开判定入口唯一（<see cref="DagScheduler"/>），且 <c>TaskDispatcher</c> 保持委派。
/// </summary>
public class DomainPrimitivesHygieneTests
{
    /// <summary>截断唯一声明点（改名单文件后须同步本守卫，否则守卫空转）。</summary>
    private const string TruncateSoleOwner = "TextTruncate.cs";

    /// <summary>终态判定唯一声明点。</summary>
    private const string TerminalSoleOwner = "TaskStatusExtensions.cs";

    /// <summary>依赖失败分派唯一声明点；该文件必须保留对它的委派调用。</summary>
    private const string DependencySoleOwner = "DagScheduler.cs";

    private const string DependencyDelegator = "TaskDispatcher.cs";

    /// <summary>允许承载深拷贝入口的类型：<c>WorkflowTypeClones</c>（record with）+ <c>TaskItem</c>（唯一手写）。</summary>
    private static readonly string[] CloneEntryOwners = { "WorkflowTypeClones", "TaskItem" };

    /// <summary>内联截断的词法特征：<c>值[..N] + "…"</c> 形态（原 StreamJsonRenderer / 流程示意 VM 各有一份）。</summary>
    private static readonly Regex InlineTruncation = new(@"\[\.\.[^\]]+\]\s*\+\s*""", RegexOptions.Compiled);

    /// <summary>终态成员的声明特征（属性或扩展方法）。</summary>
    private static readonly Regex TerminalMember = new(@"\bbool\s+IsTerminal\b", RegexOptions.Compiled);

    private static readonly Assembly[] ProductionAssemblies =
    {
        typeof(TaskItem).Assembly,                 // Core
        typeof(JsonStoreBase<>).Assembly,          // Infrastructure
        typeof(CodeBuddyAdapter).Assembly          // Agents
    };

    // ---------------- ① 截断单一来源 ----------------

    [Fact]
    public void Src内_除TextTruncate外_不得内联截断实现()
    {
        var repoRoot = LocateRepoRoot();
        var owner = Directory.GetFiles(Path.Combine(repoRoot, "src"), TruncateSoleOwner, SearchOption.AllDirectories)
            .SingleOrDefault(p => !IsBuildOutput(p));

        Assert.NotNull(owner); // 单一事实来源被改名/删除时守卫须先报警，而非空转

        var violations = new List<string>();
        foreach (var file in SourceFiles(repoRoot))
        {
            if (string.Equals(Path.GetFileName(file), TruncateSoleOwner, StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            var relative = Path.GetRelativePath(repoRoot, file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (InlineTruncation.IsMatch(lines[i]))
                {
                    violations.Add($"{relative}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现内联文本截断实现（ARCH P-08 / A-5）：\n" + string.Join('\n', violations) +
            "\n处置：改用 TextTruncate.Truncate(value, max, suffix) / TextTruncate.Result(value, max)；" +
            "新增后缀形态先在 TextTruncate 收口，再由调用方传入。");
    }

    // ---------------- ② 终态集合单一来源 ----------------

    [Fact]
    public void 终态成员_除单一事实来源外_一律委派TaskStatusExtensions()
    {
        var repoRoot = LocateRepoRoot();
        var problems = new List<string>();

        foreach (var file in SourceFiles(repoRoot))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, TerminalSoleOwner, StringComparison.Ordinal))
            {
                continue; // 单一事实来源本身
            }

            var source = File.ReadAllText(file);
            if (!TerminalMember.IsMatch(source))
            {
                continue;
            }

            // 声明了终态成员的文件必须把判定委派回单一事实来源（禁止再写一份枚举集合）。
            if (!source.Contains(".IsTerminal()", StringComparison.Ordinal))
            {
                problems.Add($"{Path.GetRelativePath(repoRoot, file)} 声明了 IsTerminal 却未委派 {TerminalSoleOwner}");
            }
        }

        Assert.True(
            problems.Count == 0,
            "终态判定出现第二份实现（ARCH P-08 / A-5）：\n" + string.Join('\n', problems) +
            "\n处置：删除内联的终态枚举集合，改为 Status.IsTerminal()；新增终态状态只改 TaskStatusExtensions。");
    }

    [Fact]
    public void 终态成员_对全部枚举值_与单一事实来源判定一致()
    {
        var problems = new List<string>();

        foreach (var status in Enum.GetValues<TaskStatus>())
        {
            var expected = status.IsTerminal();

            if (new TaskItem { Status = status }.IsTerminal != expected)
            {
                problems.Add($"TaskItem.IsTerminal({status}) = {!expected}，应为 {expected}");
            }

            if (new StageResult { Status = status }.IsTerminal != expected)
            {
                problems.Add($"StageResult.IsTerminal({status}) = {!expected}，应为 {expected}");
            }
        }

        Assert.True(
            problems.Count == 0,
            "终态判定与单一事实来源分叉（ARCH P-08）：\n" + string.Join('\n', problems));
    }

    // ---------------- ③ Clone 单一来源 ----------------

    [Fact]
    public void 深拷贝入口_仅允许WorkflowTypeClones与TaskItem()
    {
        var entries = ProductionAssemblies
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => string.Equals(m.Name, "Clone", StringComparison.Ordinal))
            .Select(m => m.DeclaringType?.Name ?? "?")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(entries); // 清单为空说明命名约定已变，须同步本守卫

        var strays = entries.Except(CloneEntryOwners, StringComparer.Ordinal).ToArray();

        Assert.True(
            strays.Length == 0,
            "出现第二份深拷贝实现（ARCH P-08 / A-5）：" + string.Join("、", strays) +
            "\n处置：工作流域类型一律走 WorkflowTypeClones 的 record with 扩展方法；" +
            "确需手写拷贝的类型须在此登记并补字段完整性契约测试（见 TaskItemCloneTests）。");
    }

    // ---------------- ④ 依赖失败分派单一来源 ----------------

    [Fact]
    public void 依赖失败分派_公开判定入口唯一且TaskDispatcher保持委派()
    {
        var publicVerdicts = typeof(TaskItem).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(DependencyVerdict))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(publicVerdicts);

        var strays = publicVerdicts.Where(n => !n.StartsWith("DagScheduler.", StringComparison.Ordinal)).ToArray();

        Assert.True(
            strays.Length == 0,
            "依赖失败分派出现第二份公开判定入口（ARCH P-08）：" + string.Join("、", strays) +
            $"\n处置：依赖三态判定只保留 {DependencySoleOwner} 的 EvaluateDependency / DependenciesSatisfied。");

        var repoRoot = LocateRepoRoot();
        var delegator = Directory.GetFiles(Path.Combine(repoRoot, "src"), DependencyDelegator, SearchOption.AllDirectories)
            .SingleOrDefault(p => !IsBuildOutput(p));

        Assert.NotNull(delegator);
        Assert.True(
            File.ReadAllText(delegator!).Contains("DagScheduler.EvaluateDependency", StringComparison.Ordinal),
            $"{DependencyDelegator} 不再委派 {DependencySoleOwner} 的 EvaluateDependency——" +
            "依赖判定一旦被内联回写即「同名不同义」回潮（P2-3）。");
    }

    // ---------------- 公共帮助 ----------------

    private static List<string> SourceFiles(string repoRoot)
        => Directory.GetFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
           path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

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
