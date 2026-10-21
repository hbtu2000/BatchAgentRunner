using System.Reflection;
using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Options;
using Xunit;

namespace BatchAgentRunner.Tests.Architecture;

/// <summary>
/// P-09 God Config 收口的防回潮守卫：
/// ① `RunOptions` 必须保持「按配置域切片」形态——任一分片超过阈值或整体回缩为单一大类即红；
/// ② `RunOptions` 的 JSON 往返克隆（P2-9 原债务）已消除，禁止以任何克隆成员形式复现。
/// </summary>
public class RunOptionsHygieneTests
{
    /// <summary>单个配置域切片的行数上限（原单一大类 363 行，切片后各域均远低于此）。</summary>
    private const int MaxSliceLines = 200;

    /// <summary>切片数下限：低于此值说明有人把配置又塞回了单一大类。</summary>
    private const int MinSliceCount = 5;

    [Fact]
    public void RunOptions_须按配置域切片_不得回缩为单一大类()
    {
        var files = RunOptionsSources();

        Assert.True(files.Count >= MinSliceCount,
            $"RunOptions 应保持「按配置域 partial 切片」形态，实测切片文件 {files.Count} 个（< {MinSliceCount}）。\n" +
            "处置：新增配置请落到所属配置域切片（`Core/Options/RunOptions.NN-*.cs`），勿塞回 `RunOptions.cs`。");
    }

    [Fact]
    public void RunOptions_各配置域切片_均不得超过行数上限_防GodConfig回潮()
    {
        var offenders = RunOptionsSources()
            .Select(f => (File: Path.GetFileName(f), Lines: File.ReadAllLines(f).Length))
            .Where(x => x.Lines > MaxSliceLines)
            .Select(x => $"{x.File}：{x.Lines} 行")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void RunOptions_禁止深拷贝成员_JSON往返克隆不得复现()
    {
        Assert.False(typeof(ICloneable).IsAssignableFrom(typeof(RunOptions)),
            "RunOptions 不得实现 ICloneable：配置克隆历史上靠 JSON 往返实现（P2-9），应显式赋值而非序列化往返。");

        var offenders = new[] { typeof(RunOptions).Assembly }
            .Concat(AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name is { } n && n.StartsWith("BatchAgentRunner", StringComparison.Ordinal)))
            .Distinct()
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.Where(t => t is not null).Select(t => t!);
                }
            })
            .Where(t => t.IsClass)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => CloneName.IsMatch(m.Name))
            .Where(m => m.ReturnType == typeof(RunOptions) ||
                        m.ReturnType == typeof(System.Threading.Tasks.ValueTask<RunOptions>) ||
                        m.ReturnType == typeof(System.Threading.Tasks.Task<RunOptions>))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name} -> {m.ReturnType.Name}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static readonly Regex CloneName =
        new(@"(Clone|Copy|DeepCopy|Duplicate|Snapshot)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<string> RunOptionsSources()
    {
        var optionsDirectory = Path.Combine(LocateRepoRoot(), "src", "BatchAgentRunner.Core", "Options");

        return Directory.GetFiles(optionsDirectory, "RunOptions*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

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
