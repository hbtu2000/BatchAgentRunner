using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// P2-30 逃逸阀守卫（治理方案 v2.0 §1.3 P2-30 / §4.7 逃逸阀治理）。
/// P2-30 的债务本体是「规则豁免无登记、无到期日」——登记过但没到期日的豁免与没登记等价：
/// 无人复核、也无法判断是否已过期（§4.7「到期未清理的逃逸自动升级为该迭代的 P1」形同虚设）。
/// 本守卫把三件事固化成测试：
///
/// ① **站点标注**：`src` 内每个 `#pragma warning disable` / `[SuppressMessage]`，以及 `.editorconfig`
///    的 `severity = none`、各 csproj / `Directory.Build.props` 的 `&lt;NoWarn&gt;`，都必须在就近注释写明
///    「逃逸阀台账 #N / 到期日 YYYY-MM-DD / 责任人 X」（§4.7 规则：例外一律显式）；
/// ② **台账完整性**：方案 §4.7 台账每行须具备 §4.6 强制四项（发现日 / 责任人 / 到期日 / 不变量断言），
///    且到期日未过期——过期即失败，等价于自动升级为当期 P1；
/// ③ **双向一致**：代码侧引用的台账 ID 必须在台账中存在；台账每条 ID 必须在仓内被某处引用
///    （防「台账记了一套、代码另有一套」，P2-30 盘点的 Desktop `NoWarn` 就是这类漏登记）。
///
/// 与 `CommentHygieneTests`（P2-26，管「注释不得自述延期」）的分工：那个管注释内容，本守卫管门禁例外。
/// </summary>
public class EscapeValveHygieneTests
{
    private static readonly Regex LedgerRef = new(@"逃逸阀台账\s*#(\d+)", RegexOptions.Compiled);

    private static readonly Regex DueDate = new(@"到期日\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);

    private static readonly Regex Owner = new(@"责任人\s*(\S+)", RegexOptions.Compiled);

    private static readonly Regex DateOnly = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private static readonly Regex SeverityNone = new(@"^\s*dotnet_diagnostic\.[A-Za-z0-9_]+\.severity\s*=\s*none\s*$", RegexOptions.Compiled);

    private static readonly Regex ParameterlessCountdownWindow = new(@"new\s+ShutdownCountdownWindow\s*\(\s*\)", RegexOptions.Compiled);

    [Fact]
    public void Src内_每个告警抑制_必须携带台账ID与到期日与责任人()
    {
        var repoRoot = LocateRepoRoot();
        var violations = new List<string>();
        foreach (var file in SourceFiles(repoRoot))
        {
            var relative = Path.GetRelativePath(repoRoot, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // pragma：标注可写在同行尾注释，也可写在紧邻的上方注释
                if (lines[i].Contains("#pragma warning disable", StringComparison.Ordinal))
                {
                    var start = Math.Max(0, i - 3);
                    Check(relative, i + 1, "#pragma warning disable", string.Join('\n', lines[start..(i + 1)]), violations);
                }

                // SuppressMessage：Justification 通常写在随后的行上
                if (lines[i].Contains("[SuppressMessage(", StringComparison.Ordinal))
                {
                    var end = Math.Min(lines.Length, i + 4);
                    Check(relative, i + 1, "[SuppressMessage]", string.Join('\n', lines[i..end]), violations);
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void 配置与工程文件_每条规则豁免_必须携带台账ID与到期日()
    {
        var repoRoot = LocateRepoRoot();
        var violations = new List<string>();

        // .editorconfig：severity = none 上方须有连续的 # 注释块承载标注
        var editorconfig = Path.Combine(repoRoot, ".editorconfig");
        Assert.True(File.Exists(editorconfig), $"未找到 .editorconfig：{editorconfig}");
        var cfgLines = File.ReadAllLines(editorconfig);
        for (var i = 0; i < cfgLines.Length; i++)
        {
            if (!SeverityNone.IsMatch(cfgLines[i]))
            {
                continue;
            }

            var start = i;
            while (start > 0 && cfgLines[start - 1].TrimStart().StartsWith('#'))
            {
                start--;
            }

            Check(".editorconfig", i + 1, cfgLines[i].Trim(), string.Join('\n', cfgLines[start..i]), violations);
        }

        // csproj / props：<NoWarn> 上方须有 XML 注释块承载标注
        foreach (var file in ProjectFiles(repoRoot))
        {
            var relative = Path.GetRelativePath(repoRoot, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("<NoWarn>", StringComparison.Ordinal))
                {
                    continue;
                }

                var start = i;
                while (start > 0 && !lines[start - 1].Contains("<!--", StringComparison.Ordinal))
                {
                    start--;
                }

                var blockStart = Math.Max(0, start - 1);
                Check(relative, i + 1, "<NoWarn>", string.Join('\n', lines[blockStart..(i + 1)]), violations);
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void 逃逸阀台账_每行四要素齐全且到期日未过期()
    {
        var repoRoot = LocateRepoRoot();
        var rows = ReadLedger(repoRoot);
        Assert.True(rows.Count >= 10, $"§4.7 逃逸阀台账解析到的行数异常（{rows.Count}）——表结构可能被改动，请同步更新本守卫的解析口径");

        var problems = new List<string>();
        var today = DateTime.Today;
        foreach (var row in rows)
        {
            if (!DateOnly.IsMatch(row.Found))
            {
                problems.Add($"#{row.Id} 发现日缺失或非 YYYY-MM-DD：'{row.Found}'");
            }

            if (string.IsNullOrWhiteSpace(row.Owner))
            {
                problems.Add($"#{row.Id} 责任人缺失");
            }

            if (!DateOnly.IsMatch(row.Due))
            {
                problems.Add($"#{row.Id} 到期日缺失或非 YYYY-MM-DD：'{row.Due}'（P2-30 债务本体：无到期日 = 无人复核）");
            }
            else if (DateTime.ParseExact(row.Due, "yyyy-MM-dd", CultureInfo.InvariantCulture) < today)
            {
                problems.Add($"#{row.Id} 到期日 {row.Due} 已过期 —— 按 §4.7 规则升级为当期 P1：清理该豁免，或改期并在台账写明理由");
            }

            if (string.IsNullOrWhiteSpace(row.Invariant))
            {
                problems.Add($"#{row.Id} 缺少不变量断言（§4.6 强制四项之一；不可测的文档型豁免须写明「由本守卫锁死登记完整性」）");
            }
        }

        Assert.True(
            problems.Count == 0,
            "§4.7 逃逸阀台账存在不合规登记：\n" + string.Join('\n', problems));
    }

    [Fact]
    public void 代码侧引用的台账ID_与台账双向一致()
    {
        var repoRoot = LocateRepoRoot();
        var ledgerIds = ReadLedger(repoRoot).Select(r => r.Id).ToHashSet();

        var referenced = new Dictionary<int, string>();
        foreach (var file in AnnotatableFiles(repoRoot))
        {
            var relative = Path.GetRelativePath(repoRoot, file);
            var text = File.ReadAllText(file);
            foreach (Match m in LedgerRef.Matches(text))
            {
                referenced[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = relative;
            }
        }

        var dangling = referenced.Keys.Where(id => !ledgerIds.Contains(id)).OrderBy(id => id).ToArray();
        var unregistered = ledgerIds.Where(id => !referenced.ContainsKey(id)).OrderBy(id => id).ToArray();

        Assert.True(
            dangling.Length == 0 && unregistered.Length == 0,
            "代码侧豁免标注与 §4.7 台账不一致：\n" +
            string.Join('\n', dangling.Select(id => $"  代码引用了台账不存在的 #{id}（{referenced[id]}）")) +
            string.Join('\n', unregistered.Select(id => $"  台账 #{id} 在仓内无任何引用（豁免已消失？请从台账下线）")) +
            "\n处置：新增豁免 → 先在台账登记四要素，再在代码就近注释引用其 ID；删除豁免 → 同步下线台账条目。");
    }

    [Fact]
    public void ShutdownCountdown窗口_不得出现无参构造_AVLN3001豁免的前提()
    {
        // §4.7 #10：AVLN3001 之所以必须豁免，是因为 ShutdownCountdownWindow 只由 EngineHost 带参构造、
        // 不经 XAML URI 加载。一旦为消告警补上无参构造，豁免前提即消失——须先删豁免再改代码。
        var repoRoot = LocateRepoRoot();
        var hits = SourceFiles(repoRoot)
            .Where(f => ParameterlessCountdownWindow.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(repoRoot, f))
            .ToArray();

        Assert.True(
            hits.Length == 0,
            "出现无参构造 `new ShutdownCountdownWindow()`：" + string.Join(", ", hits) +
            "\n处置：该窗口只应由 EngineHost 以 ShutdownCountdownWindow(int) 构造；若确需 XAML 加载，先移除 §4.7 #10 豁免再改。");
    }

    /// <summary>校验标注块是否具备「台账 ID + 到期日 + 责任人」三要素；缺项写入违规清单。</summary>
    private static void Check(string relative, int line, string site, string block, List<string> violations)
    {
        if (!LedgerRef.IsMatch(block))
        {
            violations.Add($"{relative}:{line} [{site}] 缺少「逃逸阀台账 #N」标注");
            return;
        }

        if (!DueDate.IsMatch(block))
        {
            violations.Add($"{relative}:{line} [{site}] 缺少「到期日 YYYY-MM-DD」");
            return;
        }

        if (!Owner.IsMatch(block))
        {
            violations.Add($"{relative}:{line} [{site}] 缺少「责任人」");
        }
    }

    /// <summary>解析 §4.7 逃逸阀台账表格行（列序：# / 逃逸 / 位置 / 理由 / 发现日 / 责任人 / 到期日 / 不变量断言）。</summary>
    private static List<(int Id, string Found, string Owner, string Due, string Invariant)> ReadLedger(string repoRoot)
    {
        var doc = Path.Combine(repoRoot, "docs", "TECH-DEBT-技术债务治理方案-v2.0.md");
        Assert.True(File.Exists(doc), $"未找到治理方案文档：{doc}（守卫测试须在仓库检出内运行）");

        var rows = new List<(int, string, string, string, string)>();
        var inSection = false;
        foreach (var line in File.ReadAllLines(doc))
        {
            if (line.Contains("**逃逸阀台账（", StringComparison.Ordinal))
            {
                inSection = true;
                continue;
            }

            if (inSection && line.StartsWith("#### ", StringComparison.Ordinal))
            {
                break;
            }

            if (!inSection || !line.StartsWith('|'))
            {
                continue;
            }

            var cells = line.Split('|');
            if (cells.Length < 9 || !int.TryParse(cells[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                continue; // 表头 / 分隔行 / 说明行
            }

            rows.Add((id, cells[5].Trim(), cells[6].Trim(), cells[7].Trim(), cells[8].Trim()));
        }

        return rows;
    }

    private static List<string> SourceFiles(string repoRoot)
        => Directory.GetFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(NotGenerated)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static List<string> ProjectFiles(string repoRoot)
    {
        var files = new List<string> { Path.Combine(repoRoot, "Directory.Build.props") };
        foreach (var dir in new[] { "src", "tests" })
        {
            files.AddRange(Directory.GetFiles(Path.Combine(repoRoot, dir), "*.csproj", SearchOption.AllDirectories).Where(NotGenerated));
        }

        return files.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>全部可能出现「逃逸阀台账 #N」标注的文件（源码 / 工程 / 配置 / 钩子）。</summary>
    private static List<string> AnnotatableFiles(string repoRoot)
    {
        var files = new List<string>();
        files.AddRange(SourceFiles(repoRoot));
        files.AddRange(ProjectFiles(repoRoot));
        foreach (var name in new[] { ".editorconfig", "Directory.Packages.props" })
        {
            var path = Path.Combine(repoRoot, name);
            if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        var hooks = Path.Combine(repoRoot, ".githooks");
        if (Directory.Exists(hooks))
        {
            files.AddRange(Directory.GetFiles(hooks, "*", SearchOption.AllDirectories));
        }

        return files;
    }

    private static bool NotGenerated(string path)
        => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

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
