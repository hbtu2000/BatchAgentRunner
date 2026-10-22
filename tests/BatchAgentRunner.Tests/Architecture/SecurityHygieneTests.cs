using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// 静态安全扫描守卫（ARCH P-12 / A-11）。
/// </summary>
/// <remarks>
/// P0-3 已修命令注入，但「靠自觉」的防线会随重构悄悄退化。本守卫把三类仓级专属不变量变成机器可校验的红线：
/// <list type="bullet">
///   <item><b>命令注入</b>：<see cref="BatchAgentRunner.Agents.Internal.CliCommandBuilder"/> 是 CLI 参数的唯一拼装点，
///   用户可控的 <c>{prompt}</c> / <c>{workspace}</c> 必须经 <c>Quote(NormalizeToSingleLine(…))</c> 包裹；
///   引号实现唯一；适配器一律经 <c>CliCommandBuilder.Build</c> 路由，不得自己裸替换占位符。</item>
///   <item><b>路径遍历</b>：<c>FileCredentialVault</c> 的账号 ID 落盘前一律经 <c>NormalizeAccountId</c> 净化
///   （拒绝 <c>..</c> / <c>/</c> / <c>\</c> / 非法文件名字符），防凭证目录被遍历。</item>
///   <item><b>凭证硬编码</b>：源码不得出现硬编码密钥（PRD 第 8 章——凭据只以环境变量名/引用形式存在）。</item>
/// </list>
/// 与 <see cref="ObservabilityHygieneTests"/> / <see cref="DomainPrimitivesHygieneTests"/> 并列，同属 §3.4「架构约束自动化」的第三道墙。
/// </remarks>
public class SecurityHygieneTests
{
    // 命令注入：用户可控占位符的替换两侧必须保持「Quote(NormalizeToSingleLine(…))」形态。
    private static readonly Regex PromptQuote =
        new(@"Replace\(\s*PlaceholderPrompt\s*,\s*Quote\(\s*NormalizeToSingleLine\(", RegexOptions.Compiled);

    private static readonly Regex WorkspaceQuote =
        new(@"Replace\(\s*PlaceholderWorkspace\s*,\s*Quote\(\s*NormalizeToSingleLine\(", RegexOptions.Compiled);

    [Fact]
    public void CliCommandBuilder_用户占位符_经Quote与NormalizeToSingleLine包裹_防命令注入()
    {
        var path = LocateSourceFile("CliCommandBuilder.cs");
        var text = File.ReadAllText(path);

        Assert.True(
            PromptQuote.IsMatch(text),
            "CliCommandBuilder.Build 的 {prompt} 替换未保持 Quote(NormalizeToSingleLine(…)) 形态——" +
            "去掉引号或单行折叠会复活 2026-10-22 命令注入事故（换行被 cmd.exe 当分隔符截断参数）。");

        Assert.True(
            WorkspaceQuote.IsMatch(text),
            "CliCommandBuilder.Build 的 {workspace} 替换未保持 Quote(NormalizeToSingleLine(…)) 形态——" +
            "路径含空格/换行时会被拆成多条命令或截断。");
    }

    [Fact]
    public void CliCommandBuilder_引号实现唯一_防绕过脱敏()
    {
        // 引号（含换行折叠前的转义）只能有一处事实来源；再写一份引号逻辑会绕过统一防御。
        var path = LocateSourceFile("CliCommandBuilder.cs");
        var text = File.ReadAllText(path);

        var declarations = Regex.Count(text, @"\bstring\s+Quote\s*\(");
        Assert.True(
            declarations == 1,
            "CliCommandBuilder 内 string Quote( 的声明数 ≠ 1，出现第二份引号实现会绕过命令注入防御。");
    }

    [Fact]
    public void CliAdapterBase_命令构造一律经CliCommandBuilder_不得裸替换占位符()
    {
        var path = LocateSourceFile("CliAdapterBase.cs");
        var text = File.ReadAllText(path);

        Assert.True(
            text.Contains("CliCommandBuilder.Build(", StringComparison.Ordinal),
            "CliAdapterBase 未路由到 CliCommandBuilder.Build——新增命令构造必须走唯一拼装点（注入面单一）。");

        // 适配器不得自己再做占位符替换：凡出现第二处 .Replace(Placeholder…) 即视为绕过唯一拼装点。
        var straySubstitutions = Regex.Count(text, @"\.Replace\(\s*Placeholder");
        Assert.True(
            straySubstitutions == 0,
            "CliAdapterBase 内出现裸占位符替换（.Replace(Placeholder…)），绕过了 CliCommandBuilder 的引号/折叠防御。");
    }

    [Fact]
    public void FileCredentialVault_账号ID入口一律经NormalizeAccountId净化_防路径遍历()
    {
        var path = LocateSourceFile("FileCredentialVault.cs");
        var text = File.ReadAllText(path);

        // 所有接收外部 accountId 的公共方法必须先归一化，否则账号目录可被 ../ 遍历。
        var entryMethods = new[]
        {
            "BackupFromLiveAsync", "SwitchToAsync", "GetSnapshot",
            "SnapshotExists", "MarkVerifiedAsync", "DeleteSnapshotAsync"
        };

        var missing = entryMethods
            .Where(m => !MethodBody(text, m).Contains("NormalizeAccountId(", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "以下 FileCredentialVault 公共方法的方法体内未调用 NormalizeAccountId 净化账号 ID（路径遍历风险）：" +
            string.Join("、", missing));
    }

    /// <summary>截取指定方法的方法体文本（首个同名调用的方法签名到配对大括号）；找不到时返回空串。</summary>
    private static string MethodBody(string text, string methodName)
    {
        var signature = text.IndexOf(methodName + "(", StringComparison.Ordinal);
        if (signature < 0)
        {
            return string.Empty;
        }

        var open = text.IndexOf('{', signature);
        if (open < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[signature..(i + 1)];
                }
            }
        }

        return string.Empty;
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a\\b")]
    [InlineData(".\\")]
    [InlineData("a*b")]
    public void FileCredentialVault_NormalizeAccountId_拒绝遍历字符_行为不变(string malicious)
    {
        // NormalizeAccountId 是 private static，经反射调用——它的拒绝逻辑是路径遍历的唯一闸门，
        // 改名/削弱即红（本断言同时锁死方法名与拒绝语义）。
        var method = typeof(BatchAgentRunner.Infrastructure.CredentialVault.FileCredentialVault)
            .GetMethod("NormalizeAccountId", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var ex = Record.Exception(() => method!.Invoke(null, new object[] { malicious }));
        Assert.IsType<TargetInvocationException>(ex);
        Assert.IsType<BatchAgentRunner.Core.Domain.CredentialVaultException>(
            ((TargetInvocationException)ex).InnerException);
    }

    [Fact]
    public void FileCredentialVault_NormalizeAccountId_合法ID原样通过()
    {
        var method = typeof(BatchAgentRunner.Infrastructure.CredentialVault.FileCredentialVault)
            .GetMethod("NormalizeAccountId", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, new object[] { "acc-1" })!;
        Assert.Equal("acc-1", result);
    }

    [Fact]
    public void 源码_不得硬编码密钥_凭证只以环境变量名引用()
    {
        var repoRoot = LocateRepoRoot();
        var violations = new List<string>();

        // 仅匹配「名称含密钥语义」的符号被赋以「长得像密钥」的字面量；
        // 显式排除存「环境变量名/引用」的字段（PRD 第 8 章——它们存的是变量名而非密钥值）。
        var assignment = new Regex(
            @"((?i)(secret|password|passwd|privatekey|accesstoken|apikey|token|credential)[a-z0-9_]*)\s*=\s*""([A-Za-z0-9+/=_\-]{20,})""");

        foreach (var file in SourceFiles(repoRoot))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = assignment.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                var identifier = match.Groups[1].Value;
                if (identifier.EndsWith("EnvironmentVariable", StringComparison.OrdinalIgnoreCase) ||
                    identifier.EndsWith("Reference", StringComparison.OrdinalIgnoreCase) ||
                    identifier.EndsWith("Variable", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 存的是环境变量名/引用，不是密钥值
                }

                var literal = match.Groups[3].Value;
                var looksLikeSecret = literal.Length >= 20 &&
                                      literal.Any(char.IsDigit) && literal.Any(char.IsLetter);
                if (looksLikeSecret)
                {
                    violations.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现疑似硬编码密钥（ARCH P-12 / A-11 凭证硬编码守卫）：\n" + string.Join('\n', violations) +
            "\n处置：凭据一律改为环境变量名引用（env:XXX）或引用路径，禁止把密钥值写进源码。");
    }

    private static List<string> SourceFiles(string repoRoot)
        => Directory.GetFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static string LocateSourceFile(string fileName)
        => Directory.GetFiles(Path.Combine(LocateRepoRoot(), "src"), fileName, SearchOption.AllDirectories)
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
