using System.Reflection;
using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Observability;
using BatchAgentRunner.Infrastructure.Logging;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// 可观测性守卫（ARCH P-10 / A-8）。
/// </summary>
/// <remarks>
/// P-10 收口了四件以前"靠自觉"的事，本守卫把收口结果变成机器可校验的不变量：
/// <list type="bullet">
///   <item><b>脱敏</b>：实现只允许一处（<see cref="SecretMasker"/>），写入路径不得绕过；</item>
///   <item><b>结构化</b>：日志实现必须提供结构化通道，且实现内部只有这一个落盘点；</item>
///   <item><b>关联</b>：<c>IRunLogger</c> 实现必须提供 RunId，引擎事件一律带 RunId；</item>
///   <item><b>指标</b>：§5.6 要求的 7 类运行级指标必须在快照里存在（改名/遗漏即红）。</item>
/// </list>
/// 与 <see cref="AppPathsHygieneTests"/> / <see cref="DomainPrimitivesHygieneTests"/> 并列，
/// 同属 §3.4「架构约束自动化」的第三道墙。
/// </summary>
public class ObservabilityHygieneTests
{
    /// <summary>唯一允许出现级别字面量的文件（单一事实来源）。</summary>
    private const string SoleLevelFile = "RunLogLevels.cs";

    /// <summary>唯一允许声明脱敏实现的文件（单一事实来源）。</summary>
    private const string SoleMaskerFile = "SecretMasker.cs";

    /// <summary>写日志点的级别字面量：<c>Write("WARN", …)</c>——级别必须走 <see cref="RunLogLevels"/> 常量。</summary>
    private static readonly Regex LevelLiteral = new(@"Write\(\s*""(INFO|WARN|ERROR|DANGER)""", RegexOptions.Compiled);

    private static readonly Regex MaskerDeclaration =
        new(@"\b(static\s+)?(string|void|bool)\s+\w*(Mask|Redact|Sanitize|Scrub)\w*\s*\(", RegexOptions.Compiled);

    /// <summary>§5.6 要求的运行级指标字段（新增指标须同步本清单与文档）。</summary>
    private static readonly string[] RequiredMetricFields =
    {
        "tasks_completed",
        "tasks_failed",
        "failure_rate",
        "degradation_switches",
        "misswitch_rate",
        "fallback_triggered",
        "cooldown_skips",
        "window_skips",
        "average_duration_seconds",
        "throughput_per_hour"
    };

    [Fact]
    public void 脱敏实现_全仓唯一_且不得出现第二套()
    {
        var repoRoot = LocateRepoRoot();
        var violations = new List<string>();

        foreach (var file in SourceFiles(repoRoot))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, SoleMaskerFile, StringComparison.Ordinal))
            {
                continue; // 单一事实来源本身
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (MaskerDeclaration.IsMatch(lines[i]))
                {
                    violations.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现第二套脱敏实现（ARCH P-10）：\n" + string.Join('\n', violations) +
            "\n处置：一律改调 SecretMasker.Mask；新增形态只改 SecretMasker 的正则清单。");
    }

    [Fact]
    public void 日志级别_字面量只出现在RunLogLevels_实现侧一律引用常量()
    {
        var repoRoot = LocateRepoRoot();
        var violations = new List<string>();

        foreach (var file in SourceFiles(repoRoot))
        {
            if (string.Equals(Path.GetFileName(file), SoleLevelFile, StringComparison.Ordinal))
            {
                continue;
            }

            // 日志实现是级别的消费大户：允许其出现（经常量），但必须不含裸字面量
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (LevelLiteral.IsMatch(lines[i]))
                {
                    violations.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现裸露的日志级别字面量（ARCH P-10）：\n" + string.Join('\n', violations) +
            "\n处置：改用 RunLogLevels.Info/Warn/Error/Danger；写日志点用 RunLogLevels.Normalize(level) 兜底未知级别。");
    }

    [Fact]
    public void 日志实现_必须提供结构化通道与RunId()
    {
        var implementations = typeof(FileRunLogger).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IRunLogger).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(implementations);

        var problems = new List<string>();
        foreach (var type in implementations)
        {
            if (type.GetProperty(nameof(IRunLogger.StructuredLogFilePath)) is null)
            {
                problems.Add($"{type.Name} 未提供 {nameof(IRunLogger.StructuredLogFilePath)}（结构化采集通道缺失）");
            }

            if (type.GetProperty(nameof(IRunLogger.RunId)) is null)
            {
                problems.Add($"{type.Name} 未提供 {nameof(IRunLogger.RunId)}（日志与指标无法关联）");
            }

            var entryChannel = type.GetMethod(nameof(IRunLogger.Write), new[] { typeof(RunLogEntry) });
            if (entryChannel is null)
            {
                problems.Add($"{type.Name} 未实现 {nameof(IRunLogger.Write)}(RunLogEntry) 结构化写入通道");
            }
        }

        Assert.True(
            problems.Count == 0,
            "IRunLogger 实现不满足可观测性契约（ARCH P-10）：\n" + string.Join('\n', problems));
    }

    [Fact]
    public void FileRunLogger_内部只有结构化通道一个落盘点_防绕过脱敏()
    {
        // 断言方式：对底层 writer 的直接写调用只允许出现在「结构化通道 → WriteLine」这一处方法体内。
        // 若有人在别的 Write* 里直接 writer.WriteLine，脱敏与结构化都会被绕过而无人察觉。
        var path = LocateSourceFile(LocateRepoRoot(), "FileRunLogger.cs");
        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, l => l.Contains("private void WriteLine", StringComparison.Ordinal));
        Assert.True(start >= 0, "未找到 FileRunLogger 的统一落盘方法 WriteLine（重命名后请同步本守卫）");

        // 定位方法体范围（按大括号配平），方法体内的两路输出（文本 + 结构化）合法，其余一律视为绕过
        var end = start;
        var depth = 0;
        for (var i = start; i < lines.Length; i++)
        {
            depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
            if (i > start && depth == 0)
            {
                end = i;
                break;
            }
        }

        var violations = lines
            .Select((line, i) => (line, number: i + 1))
            .Where(x => x.line.Contains(".WriteLine(", StringComparison.Ordinal))
            .Where(x => x.number - 1 < start || x.number - 1 > end)
            .Select(x => $"{x.number}: {x.line.Trim()}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "FileRunLogger 内存在绕过结构化通道的直接写盘（ARCH P-10）：\n" + string.Join('\n', violations) +
            "\n处置：所有写入方法一律转成 RunLogEntry 后经 Write(RunLogEntry) 落盘。");
    }

    [Fact]
    public void 指标快照_覆盖运行级指标全集()
    {
        var fields = new RunMetrics("run-guard").Snapshot(DateTimeOffset.Now).ToFields();
        var missing = RequiredMetricFields.Where(f => !fields.ContainsKey(f)).ToArray();

        Assert.True(
            missing.Length == 0,
            "运行级指标快照缺字段（ARCH §5.6 / P-10）：" + string.Join('、', missing) +
            "\n处置：在 RunMetrics 补埋点并同步本守卫清单与 ARCH 文档；删除字段同样需要同步（防止悄悄下线指标）。");

        // 派生量不得为 NaN/负（口径错误的第一信号）
        var problems = fields
            .Where(f => f.Key.EndsWith("_rate", StringComparison.Ordinal) && double.TryParse(f.Value, out var v) &&
                        (v is < 0 or > 1))
            .Select(f => $"{f.Key}={f.Value}")
            .ToArray();

        Assert.True(problems.Length == 0, "比率型指标越界：[0,1] 之外的取值 → " + string.Join('、', problems));
    }

    [Fact]
    public void 引擎事件模型_携带RunId关联字段()
    {
        // 关联标识必须是事件模型的一等字段：由引擎在广播时统一填充，
        // 任一事件（含 UI 订阅到的事件流）都能与日志、指标串到同一次运行。
        var ev = new BatchAgentRunner.Core.Events.BatchEvent(
            BatchAgentRunner.Core.Events.BatchEventKind.Info, DateTimeOffset.Now, "探针")
        { RunId = "run-guard" };

        Assert.Equal("run-guard", ev.RunId);
        Assert.Null(new BatchAgentRunner.Core.Events.BatchEvent(
            BatchAgentRunner.Core.Events.BatchEventKind.Info, DateTimeOffset.Now, "探针").RunId);
    }

    private static List<string> SourceFiles(string repoRoot)
        => Directory.GetFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static string LocateSourceFile(string repoRoot, string fileName)
        => Directory.GetFiles(Path.Combine(repoRoot, "src"), fileName, SearchOption.AllDirectories)
            .Single(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

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
