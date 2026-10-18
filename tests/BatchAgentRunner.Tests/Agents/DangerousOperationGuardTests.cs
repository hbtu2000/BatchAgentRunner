using BatchAgentRunner.Agents.Internal;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Tests.Agents;

/// <summary>
/// P1-15 高危盲区：<see cref="DangerousOperationGuard"/> 是无人值守下自动批准的安全闸门
/// （REQ-002b）。白名单/黑名单的优先级与匹配语义必须经断言锁住，否则会误放行危险操作或误拦正常确认。
/// </summary>
public class DangerousOperationGuardTests
{
    private static AutoAnswerPolicy Policy() => new()
    {
        AllowedWhitelist = new List<string> { "确认继续" },
        DangerousBlacklist = new List<string> { "rm -rf", "删除所有" }
    };

    [Fact]
    public void Evaluate_命中白名单_返回自动允许且不回传匹配模式()
    {
        var decision = DangerousOperationGuard.Evaluate("是否确认继续 (y/n)?", Policy(), out var matched);

        Assert.Equal(GuardDecision.AutoAllow, decision);
        // 白名单命中直接放行，不回传匹配模式（与黑名单命中回传模式不同）
        Assert.Null(matched);
    }

    [Fact]
    public void Evaluate_命中黑名单_返回阻断且回传匹配模式()
    {
        var decision = DangerousOperationGuard.Evaluate("准备执行 rm -rf /data", Policy(), out var matched);

        Assert.Equal(GuardDecision.Block, decision);
        Assert.Equal("rm -rf", matched);
    }

    [Fact]
    public void Evaluate_普通提示_未命中任何规则_返回默认自动应答()
    {
        var decision = DangerousOperationGuard.Evaluate("任务已完成，输出结果如下", Policy(), out var matched);

        Assert.Equal(GuardDecision.AutoAnswerDefault, decision);
        Assert.Null(matched);
    }

    [Fact]
    public void Evaluate_空文本_返回默认自动应答不抛异常()
    {
        var decision = DangerousOperationGuard.Evaluate(null, Policy(), out var matched);

        Assert.Equal(GuardDecision.AutoAnswerDefault, decision);
        Assert.Null(matched);
    }

    [Fact]
    public void Evaluate_白名单命中_优先于黑名单_返回自动允许()
    {
        var policy = Policy();
        policy.AllowedWhitelist.Add("rm -rf"); // 白名单与黑名单同含该串

        var decision = DangerousOperationGuard.Evaluate("确认 rm -rf 操作", policy, out _);

        // 代码实现：白名单先于黑名单判定，用户显式允许的短语优先放行
        Assert.Equal(GuardDecision.AutoAllow, decision);
    }

    [Fact]
    public void IsDangerous_与Evaluate_Block_等价()
    {
        Assert.True(DangerousOperationGuard.IsDangerous("删除所有文件", Policy(), out _));
        Assert.False(DangerousOperationGuard.IsDangerous("任务成功", Policy(), out _));
    }
}
