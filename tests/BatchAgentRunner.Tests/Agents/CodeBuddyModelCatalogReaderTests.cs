using BatchAgentRunner.Agents.Internal;

namespace BatchAgentRunner.Tests.Agents;

/// <summary>官方模型倍率目录解析测试（credits 文本形态取自真实产品配置缓存）。</summary>
public class CodeBuddyModelCatalogReaderTests
{
    [Theory]
    [InlineData("x0.51 credits", 0.51)]
    [InlineData("x2.00 credits", 2.00)]
    [InlineData("x0.21", 0.21)]
    [InlineData("X1.62", 1.62)]
    [InlineData("x 0.29 credits", 0.29)]
    public void TryParseCredits_官方倍率文本_解析为数值(string text, decimal expected)
    {
        Assert.True(CodeBuddyModelCatalogReader.TryParseCredits(text, out var multiplier));
        Assert.Equal(expected, multiplier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("credits")]
    [InlineData("0.51x")]
    [InlineData("free")]
    public void TryParseCredits_非法文本_返回false(string? text)
    {
        Assert.False(CodeBuddyModelCatalogReader.TryParseCredits(text, out _));
    }
}
