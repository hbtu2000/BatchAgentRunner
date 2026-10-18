using BatchAgentRunner.Desktop.Helpers;
using Xunit;

namespace BatchAgentRunner.Desktop.Tests.Helpers;

/// <summary>
/// P1-19 契约收敛：多值文本解析/拼接的纯函数不变量（白名单、diff 通配符等字段共用同一实现）。
/// </summary>
public class MultiValueTextTests
{
    [Fact]
    public void Split_null或空白返回空数组()
    {
        Assert.Empty(MultiValueText.Split(null));
        Assert.Empty(MultiValueText.Split(string.Empty));
        Assert.Empty(MultiValueText.Split("   "));
    }

    [Theory]
    [InlineData(" a , b \n c ", new[] { "a", "b", "c" })]
    [InlineData("a\n\nb", new[] { "a", "b" })]
    [InlineData("x;y;z", new[] { "x", "y", "z" })]
    public void Split_按多分隔符去空白去空项(string input, string[] expected)
    {
        Assert.Equal(expected, MultiValueText.Split(input, ',', '\n', ';'));
    }

    [Fact]
    public void Join_跳过空白项并trim()
    {
        Assert.Equal("a\nb\nc", MultiValueText.Join(new[] { " a ", "", "b ", null, "c" }));
    }

    [Fact]
    public void Join_null返回空串()
    {
        Assert.Equal(string.Empty, MultiValueText.Join(null));
    }

    [Fact]
    public void 往返_拆分后再拼接等价()
    {
        var list = new[] { "alpha", "beta", "gamma" };
        var joined = MultiValueText.Join(list);
        Assert.Equal(list, MultiValueText.Split(joined, '\n'));
    }
}
