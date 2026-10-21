using BatchAgentRunner.Core.Text;
using Xunit;

namespace BatchAgentRunner.Tests.Core;

/// <summary>
/// P2-4 偿还守卫：<see cref="TextTruncate"/> 是全仓文本截断的单一事实来源
/// （原 BatchEngine / TaskRunner / WorkflowExecutor / GateEvaluator / ReportWriter / 积分客户端六处私有副本已收敛）。
/// 锁死两类行为不变量：① 各调用点原后缀语义逐字保留（结果类 = KB 指引；通用类 = 「已截断」；报告类 = 省略号）；
/// ② 边界（null / maxChars&lt;=0 / 恰好等于上限）不截断。
/// </summary>
public class TextTruncateTests
{
    [Fact]
    public void Result_未超限_原样返回()
    {
        Assert.Null(TextTruncate.Result(null, 100));
        Assert.Equal("abc", TextTruncate.Result("abc", 100));
        Assert.Equal("abc", TextTruncate.Result("abc", 3)); // 恰好等于上限不截断
    }

    [Fact]
    public void Result_超限_附完整内容见运行日志指引且KB按上限折算()
    {
        var value = new string('x', 10);

        var cut = TextTruncate.Result(value, 4);

        Assert.Equal("xxxx\n…（结果已截断，超过 0KB，完整内容见运行日志）", cut);
    }

    [Fact]
    public void Result_KB上限_指引显示真实KB数()
    {
        var value = new string('x', 2049);

        var cut = TextTruncate.Result(value, 2048);

        Assert.Equal(new string('x', 2048) + "\n…（结果已截断，超过 2KB，完整内容见运行日志）", cut);
    }

    [Fact]
    public void Result_上限非正_视为不截断()
    {
        Assert.Equal("abc", TextTruncate.Result("abc", 0));
        Assert.Equal("abc", TextTruncate.Result("abc", -1));
    }

    [Fact]
    public void Truncate_超限_附指定后缀()
    {
        Assert.Equal("abcd…（已截断）", TextTruncate.Truncate("abcdef", 4, "…（已截断）"));
        Assert.Equal("abcd…", TextTruncate.Truncate("abcdef", 4, "…"));
        Assert.Equal("abcd", TextTruncate.Truncate("abcdef", 4, null)); // 无后缀 = 纯截断
    }

    [Fact]
    public void Truncate_未超限或空值_原样返回()
    {
        Assert.Null(TextTruncate.Truncate(null, 4, "…"));
        Assert.Equal("abc", TextTruncate.Truncate("abc", 3, "…"));
        Assert.Equal("", TextTruncate.Truncate("", 3, "…"));
    }
}
