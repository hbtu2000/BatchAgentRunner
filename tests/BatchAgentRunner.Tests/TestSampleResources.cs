using System.Reflection;

namespace BatchAgentRunner.Tests;

/// <summary>
/// P2-21 / P2-28：测试读取 samples 的嵌入式资源副本，避免依赖磁盘/克隆路径。
/// 资源通过 .csproj 的 Link 指向真实 samples 文件，内容随 samples 同步、但不依赖运行时路径。
/// </summary>
internal static class TestSampleResources
{
    public static string ReadText(string name)
    {
        var asm = typeof(TestSampleResources).Assembly;
        var resourceName = $"BatchAgentRunner.Tests.Samples.{name}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到嵌入资源：{resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
