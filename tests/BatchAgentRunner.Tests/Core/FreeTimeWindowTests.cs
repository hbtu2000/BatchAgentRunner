using BatchAgentRunner.Core.Domain;
using Xunit;

namespace BatchAgentRunner.Tests.Core;

public class FreeTimeWindowTests
{
    [Fact]
    public void 不跨零点时段_区间内为真()
    {
        var w = new FreeTimeWindow(new TimeOnly(8, 0), new TimeOnly(12, 0));

        Assert.True(w.Contains(At.Local(2026, 10, 15, 10, 0)));
        Assert.True(w.Contains(At.Local(2026, 10, 15, 8, 0)));
        // 结束整点按分钟闭区间：12:00 这一分钟仍算在时段内，12:01 才结束
        Assert.True(w.Contains(At.Local(2026, 10, 15, 12, 0)));
        Assert.False(w.Contains(At.Local(2026, 10, 15, 12, 1)));
        Assert.False(w.Contains(At.Local(2026, 10, 15, 7, 59)));
        Assert.False(w.IsCrossMidnight);
    }

    [Fact]
    public void 跨零点时段_22点到次日6点_两段均为真()
    {
        var w = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));

        Assert.True(w.IsCrossMidnight);
        Assert.True(w.Contains(At.Local(2026, 10, 15, 23, 30)));
        Assert.True(w.Contains(At.Local(2026, 10, 16, 2, 0)));
        // 结束分钟（06:00）按闭区间仍包含在时段内，06:01 才结束
        Assert.True(w.Contains(At.Local(2026, 10, 16, 6, 0)));
        Assert.False(w.Contains(At.Local(2026, 10, 16, 6, 1)));
        Assert.False(w.Contains(At.Local(2026, 10, 15, 8, 0)));
        Assert.False(w.Contains(At.Local(2026, 10, 15, 21, 59)));
    }

    [Fact]
    public void 跨零点时段_23点至次日7点59_首末分钟均包含()
    {
        // 用户常见表达「23:00 → 07:59」：希望覆盖到次日 08:00 之前
        var w = new FreeTimeWindow(new TimeOnly(23, 0), new TimeOnly(7, 59));

        Assert.True(w.IsCrossMidnight);
        Assert.True(w.Contains(At.Local(2026, 10, 15, 23, 0)));
        Assert.True(w.Contains(At.Local(2026, 10, 15, 23, 59)));
        Assert.True(w.Contains(At.Local(2026, 10, 16, 0, 0)));
        Assert.True(w.Contains(At.Local(2026, 10, 16, 7, 59)));
        // 08:00 起离开时段
        Assert.False(w.Contains(At.Local(2026, 10, 16, 8, 0)));
        Assert.False(w.Contains(At.Local(2026, 10, 16, 12, 0)));
        Assert.False(w.Contains(At.Local(2026, 10, 15, 22, 59)));
    }

    [Fact]
    public void 星期过滤_跨零点按时段覆盖日判定_周日22点覆盖周一即生效()
    {
        var workdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        var w = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0), workdays);

        // 2026-10-18 是周日：22:30 起覆盖「周日 → 周一」，周一命中 → 生效
        Assert.True(w.Contains(At.Local(2026, 10, 18, 22, 30)));
        // 2026-10-19 周一 02:00：开始日为周日（10-18），覆盖周日→周一，周一命中 → 生效
        Assert.True(w.Contains(At.Local(2026, 10, 19, 2, 0)));
        // 2026-10-17 周六 23:00：覆盖周六→周日，两天均未命中工作日 → 不生效
        Assert.False(w.Contains(At.Local(2026, 10, 17, 23, 0)));
    }

    [Fact]
    public void 未配置星期_视为每日生效()
    {
        var w = new FreeTimeWindow(new TimeOnly(9, 0), new TimeOnly(10, 0));
        Assert.True(w.Contains(At.Local(2026, 10, 17, 9, 30)));
        Assert.True(w.Contains(At.Local(2026, 10, 18, 9, 30)));
    }

    [Fact]
    public void 起止相同视为全天()
    {
        var w = new FreeTimeWindow(new TimeOnly(0, 0), new TimeOnly(0, 0));
        Assert.True(w.IsFullDay);
        Assert.True(w.Contains(At.Local(2026, 10, 15, 3, 0)));
    }

    [Fact]
    public void 下一可用开始时间_不跨零点()
    {
        var w = new FreeTimeWindow(new TimeOnly(8, 0), new TimeOnly(12, 0));
        var next = w.NextStartAfter(At.Local(2026, 10, 15, 13, 0));

        Assert.NotNull(next);
        Assert.Equal(new TimeOnly(8, 0), TimeOnly.FromDateTime(next!.Value.LocalDateTime));
        Assert.Equal(16, next.Value.LocalDateTime.Day);
    }

    [Fact]
    public void 下一可用开始时间_跨零点_当日晚段尚未到达()
    {
        var w = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var next = w.NextStartAfter(At.Local(2026, 10, 15, 8, 0));

        Assert.NotNull(next);
        Assert.Equal(15, next!.Value.LocalDateTime.Day);
        Assert.Equal(new TimeOnly(22, 0), TimeOnly.FromDateTime(next.Value.LocalDateTime));
    }

    [Fact]
    public void 下一可用开始时间_跨零点且当日晚段已过_取次日()
    {
        var w = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        var next = w.NextStartAfter(At.Local(2026, 10, 15, 23, 30));

        Assert.NotNull(next);
        Assert.Equal(16, next!.Value.LocalDateTime.Day);
    }

    [Fact]
    public void 时段描述_跨零点以箭头展示()
    {
        var w = new FreeTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));
        Assert.Contains("→", w.Describe());

        var d = new FreeTimeWindow(new TimeOnly(8, 0), new TimeOnly(12, 0));
        Assert.Contains("08:00–12:00", d.Describe());
    }
}
