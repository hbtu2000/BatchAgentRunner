using System.Text.RegularExpressions;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// P2-26 注释卫生守卫（治理方案 v2.0 §1.3 P2-26 / §4.7.1 计划性延期台账）。
/// 锁死的规则：**计划性延期必须进台账，不得以注释自述；注释声称的能力边界必须与实现一致**。
///
/// 扫描 src 全部 .cs 的注释（跳过字符串字面量），命中以下模式即失败：
/// ① 直接声称不实现：「不实现」「未实现」「暂不支持」「尚未实现」「待实现」；
/// ② 声称当前是占位/桩：「占位」且非「占位符」（后者是模板变量领域词，合法）；
/// ③ 以「二期」标签自述延期：「二期」+「扩展 / 后续 / 再换 / 换成」。
/// （「二期」作为已交付功能的历史标签合法——如「（二期：任务依赖 topo 排序）」；台账点名的
/// 「一期不实现 workflow」「二期扩展」「返回占位并提示」类自述在 2026-10-20 P2-26 收口轮已清零。）
///
/// 实现说明：注释提取用最小词法扫描（行注释 / 块注释 / 常规 + 逐字 + 插值字符串），
/// 覆盖本仓全部源码形态；新增例外一律先登记 TECH-DEBT §4.7.1 台账再放行，不放行白名单。
/// </summary>
public class CommentHygieneTests
{
    private static readonly string[] SelfDeferralClaims = { "不实现", "未实现", "暂不支持", "尚未实现", "待实现" };

    private static readonly string[] Phase2DeferralKeywords = { "扩展", "后续", "再换", "换成" };

    /// <summary>自称桩实现：「占位」紧邻声明性语素（并/值/实现/逻辑/状态），或被「返回/仅是/纯为/纯属」修饰。
    /// 「占位符」是模板变量领域词、「防环：先占位」是算法用语，均不命中。</summary>
    private static readonly Regex StubClaim = new(@"占位(并|值|实现|逻辑|状态)|(返回|仅是|纯为|纯属)占位", RegexOptions.Compiled);

    [Fact]
    public void Src注释_不得自述计划性延期或能力未实现()
    {
        var srcRoot = LocateSrcRoot();
        Assert.True(Directory.Exists(srcRoot), $"未找到源码目录：{srcRoot}（守卫测试须在仓库检出内运行）");

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(Directory.GetParent(srcRoot)!.FullName, file);
            foreach (var (line, text) in ExtractComments(File.ReadAllText(file)))
            {
                var reason = Classify(text);
                if (reason is not null)
                {
                    violations.Add($"{relative}:{line} [{reason}] {text.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现自述计划性延期 / 能力未实现的注释（P2-26）：\n" +
            string.Join('\n', violations) +
            "\n处置：与实现不符的改为如实描述；真实延期登记「TECH-DEBT-技术债务治理方案-v2.0.md §4.7 计划性延期台账」后在注释中引用台账 ID。");
    }

    /// <summary>机械占位：`/// &lt;summary&gt;成员名。&lt;/summary&gt;`（正文只是标识符本身）。</summary>
    private static readonly Regex PlaceholderSummary =
        new(@"///\s*<summary>([A-Za-z_]\w*)。</summary>", RegexOptions.Compiled);

    /// <summary>机械占位：`/// &lt;param name="x"&gt;x。&lt;/param&gt;`（正文只是参数名本身）。</summary>
    private static readonly Regex PlaceholderParam =
        new(@"///\s*<param name=""([A-Za-z_]\w*)"">([A-Za-z_]\w*)。</param>", RegexOptions.Compiled);

    [Fact]
    public void Src文档注释_不得以成员名作summary或参数名作param_占位不算文档()
    {
        // ARCH P-12 恢复 CS1591 后，「补文档」的捷径是给每个成员塞一行「成员名。」——
        // 这能过编译却零信息量，且会掩盖真正缺文档的成员。CS1591 只保证「有文档」，
        // 本守卫保证「文档有信息」：正文必须描述行为/时机/语义，而不是复述标识符。
        var srcRoot = LocateSrcRoot();
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                 !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(Directory.GetParent(srcRoot)!.FullName, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var summary = PlaceholderSummary.Match(lines[i]);
                if (summary.Success)
                {
                    violations.Add($"{relative}:{i + 1} summary 正文 = 成员名「{summary.Groups[1].Value}」");
                }

                var param = PlaceholderParam.Match(lines[i]);
                if (param.Success && param.Groups[1].Value == param.Groups[2].Value)
                {
                    violations.Add($"{relative}:{i + 1} param 正文 = 参数名「{param.Groups[1].Value}」");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现机械占位式 XML 文档（ARCH P-12）：\n" + string.Join('\n', violations) +
            "\n处置：写清该成员做什么 / 何时被调用 / 参数的业务含义；只复述标识符不算文档。");
    }

    /// <summary>返回违规原因；合规返回 null。</summary>
    private static string? Classify(string comment)
    {
        foreach (var claim in SelfDeferralClaims)
        {
            if (comment.Contains(claim, StringComparison.Ordinal))
            {
                return claim;
            }
        }

        var stub = StubClaim.Match(comment);
        if (stub.Success)
        {
            return "自称占位/桩实现";
        }

        if (comment.Contains("二期", StringComparison.Ordinal))
        {
            foreach (var keyword in Phase2DeferralKeywords)
            {
                if (comment.Contains(keyword, StringComparison.Ordinal))
                {
                    return $"二期+{keyword}";
                }
            }
        }

        return null;
    }

    private static string LocateSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchAgentRunner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src");
    }

    /// <summary>提取源码中的全部注释文本（行注释 / 块注释），跳过字符串字面量。返回（起始行, 注释原文）。</summary>
    private static List<(int Line, string Text)> ExtractComments(string source)
    {
        var comments = new List<(int, string)>();
        var i = 0;
        var line = 1;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (c == '\r' || c == '\t' || c == ' ')
            {
                i++;
                continue;
            }

            // 行注释
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                var start = i;
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                comments.Add((line, source.Substring(start, i - start)));
                continue;
            }

            // 块注释
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n')
                    {
                        line++;
                    }

                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                comments.Add((line, source.Substring(start, i - start)));
                continue;
            }

            // 字符串字面量：可带 $ / @ 前缀（组合序不限），逐字串以 "" 转义引号
            var prefixLen = 0;
            while (prefixLen < 2 && i + prefixLen < source.Length &&
                   (source[i + prefixLen] == '$' || source[i + prefixLen] == '@'))
            {
                prefixLen++;
            }

            if (prefixLen > 0 || c == '"')
            {
                if (i + prefixLen < source.Length && source[i + prefixLen] == '"')
                {
                    var verbatim = source.AsSpan(i, prefixLen).Contains('@');
                    i += prefixLen + 1;
                    while (i < source.Length)
                    {
                        if (source[i] == '\n')
                        {
                            line++;
                            i++;
                            continue;
                        }

                        if (verbatim)
                        {
                            if (source[i] == '"')
                            {
                                if (i + 1 < source.Length && source[i + 1] == '"')
                                {
                                    i += 2; // "" 转义
                                    continue;
                                }

                                i++;
                                break;
                            }
                        }
                        else if (source[i] == '\\')
                        {
                            i += 2; // 跳过高位转义序列
                            continue;
                        }
                        else if (source[i] == '"')
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    continue;
                }
            }

            i++;
        }

        return comments;
    }
}
