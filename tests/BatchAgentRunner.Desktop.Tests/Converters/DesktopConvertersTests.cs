using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using BatchAgentRunner.Desktop.Converters;

namespace BatchAgentRunner.Desktop.Tests.Converters;

/// <summary>
/// P0-17 首批冒烟测试：Desktop 程序集此前因测试项目未引用而「结构上不可测」。
/// 本文件仅覆盖其纯 UI 无关单元（值转换器），证明 Desktop 已可被引用、可被测试锁定，
/// 为阶段 2（P1-19 Desktop 补测试 + 契约收敛）铺路。不涉及窗口创建 / Application 生命周期。
/// </summary>
public class DesktopConvertersTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static IEnumerable<object[]> GreaterThanZeroCases => new List<object[]>
    {
        new object[] { 1, true },
        new object[] { 0, false },
        new object[] { -3, false },
        new object[] { 1L, true },
        new object[] { 0L, false },
        new object[] { 1.5d, true },
        new object[] { 0.0d, false },
        new object[] { 2.0m, true },
        new object[] { 0.0m, false },
    };

    [Theory]
    [MemberData(nameof(GreaterThanZeroCases))]
    public void GreaterThanZeroConverter_数值大于零返回true(object value, bool expected)
    {
        var result = GreaterThanZeroConverter.Instance.Convert(value, typeof(bool), null, Culture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GreaterThanZeroConverter_不支持类型与null返回false()
    {
        Assert.False((bool?)GreaterThanZeroConverter.Instance.Convert(null, typeof(bool), null, Culture) ?? false);
        Assert.False((bool?)GreaterThanZeroConverter.Instance.Convert("abc", typeof(bool), null, Culture) ?? false);
    }

    [Fact]
    public void EqualsTextConverter_命中分号或竖线分隔目标之一返回true()
    {
        var converter = new EqualsTextConverter();
        Assert.True((bool?)converter.Convert("done", typeof(bool), "done;failed|blocked", Culture) ?? false);
        Assert.True((bool?)converter.Convert("blocked", typeof(bool), "done;failed|blocked", Culture) ?? false);
        Assert.True((bool?)converter.Convert("DONE", typeof(bool), "done;failed", Culture) ?? false); // 大小写不敏感
    }

    [Fact]
    public void EqualsTextConverter_未命中或入参为空返回false()
    {
        var converter = new EqualsTextConverter();
        Assert.False((bool?)converter.Convert("running", typeof(bool), "done;failed", Culture) ?? false);
        Assert.False((bool?)converter.Convert(null, typeof(bool), "done", Culture) ?? false);
        Assert.False((bool?)converter.Convert("done", typeof(bool), null, Culture) ?? false);
    }

    /// <summary>
    /// P1-20 不变量：转换器的 ConvertBack 原为 <c>throw</c>（NotImplementedException / NotSupportedException），
    /// 双向绑定一旦触发即崩溃。现必须返回 <see cref="BindingOperations.DoNothing"/>（表示「不支持反向转换」），
    /// 而不是抛异常——本断言锁住该修复，防止回归成崩溃路径。
    /// </summary>
    [Fact]
    public void 单向转换器_ConvertBack不抛异常_返回DoNothing()
    {
        var background = new ActiveTabBackgroundConverter();
        var foreground = new ActiveTabForegroundConverter();

        Assert.Same(BindingOperations.DoNothing,
            background.ConvertBack(true, typeof(bool), null, Culture));
        Assert.Same(BindingOperations.DoNothing,
            foreground.ConvertBack(true, typeof(bool), null, Culture));
        Assert.Same(BindingOperations.DoNothing,
            GreaterThanZeroConverter.Instance.ConvertBack(true, typeof(bool), null, Culture));
    }
}
