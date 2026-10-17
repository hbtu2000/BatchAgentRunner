using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using Xunit;

namespace BatchAgentRunner.Tests.Scheduling;

public class DagSchedulerTests
{
    private static TaskItem T(string id, params string[] deps) => new()
    {
        Id = id,
        Instruction = "指令 " + id,
        DependsOn = deps.Length == 0 ? null : new List<string>(deps)
    };

    [Fact]
    public void TopoSort_无依赖_保持输入顺序()
    {
        var tasks = new[] { T("a"), T("b"), T("c") };

        var ordered = DagScheduler.TopoSort(tasks).Select(t => t.Id).ToArray();

        Assert.Equal(new[] { "a", "b", "c" }, ordered);
    }

    [Fact]
    public void TopoSort_有依赖_依赖先于下游执行()
    {
        // 输入故意反序：c 依赖 b，b 依赖 a
        var tasks = new[] { T("c", "b"), T("b", "a"), T("a") };

        var ordered = DagScheduler.TopoSort(tasks).Select(t => t.Id).ToArray();

        Assert.Equal(new[] { "a", "b", "c" }, ordered);
    }

    [Fact]
    public void TopoSort_菱形依赖_每任务只出现一次()
    {
        // a → (b, c) → d
        var tasks = new[] { T("d", "b", "c"), T("b", "a"), T("c", "a"), T("a") };

        var ordered = DagScheduler.TopoSort(tasks).Select(t => t.Id).ToArray();

        Assert.Equal(4, ordered.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(IndexOf(ordered, "a") < IndexOf(ordered, "b"));
        Assert.True(IndexOf(ordered, "a") < IndexOf(ordered, "c"));
        Assert.True(IndexOf(ordered, "b") < IndexOf(ordered, "d"));
        Assert.True(IndexOf(ordered, "c") < IndexOf(ordered, "d"));
    }

    [Fact]
    public void TopoSort_存在环_抛出DagCycleException()
    {
        // a → b → c → a
        var tasks = new[] { T("a", "c"), T("b", "a"), T("c", "b") };

        var ex = Assert.Throws<DagCycleException>(() => DagScheduler.TopoSort(tasks));
        Assert.Contains("循环依赖", ex.Message);
    }

    [Fact]
    public void TopoSort_自依赖_也判定为环()
    {
        var tasks = new[] { T("a", "a") };

        Assert.Throws<DagCycleException>(() => DagScheduler.TopoSort(tasks));
    }

    [Fact]
    public void TopoSort_悬空依赖_视为无依赖不阻断排序()
    {
        // b 依赖不存在的 x
        var tasks = new[] { T("b", "x"), T("a") };

        var ordered = DagScheduler.TopoSort(tasks).Select(t => t.Id).ToArray();

        Assert.Equal(2, ordered.Length);
        Assert.Contains("b", ordered);
    }

    [Fact]
    public void DependenciesSatisfied_无依赖_恒为真()
    {
        Assert.True(DagScheduler.DependenciesSatisfied(T("a"), new Dictionary<string, TaskStatus>()));
    }

    [Fact]
    public void DependenciesSatisfied_依赖全部Done_返回真()
    {
        var status = new Dictionary<string, TaskStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = TaskStatus.Done,
            ["b"] = TaskStatus.Done
        };

        Assert.True(DagScheduler.DependenciesSatisfied(T("c", "a", "b"), status));
    }

    [Fact]
    public void DependenciesSatisfied_依赖Failed_返回假()
    {
        var status = new Dictionary<string, TaskStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = TaskStatus.Done,
            ["b"] = TaskStatus.Failed
        };

        Assert.False(DagScheduler.DependenciesSatisfied(T("c", "a", "b"), status));
    }

    [Fact]
    public void DependenciesSatisfied_依赖Blocked_返回假()
    {
        var status = new Dictionary<string, TaskStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = TaskStatus.Blocked
        };

        Assert.False(DagScheduler.DependenciesSatisfied(T("b", "a"), status));
    }

    [Fact]
    public void DependenciesSatisfied_依赖尚未终态_返回假()
    {
        var status = new Dictionary<string, TaskStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = TaskStatus.Running
        };

        Assert.False(DagScheduler.DependenciesSatisfied(T("b", "a"), status));
    }

    [Fact]
    public void DependenciesSatisfied_依赖NeedsAttention_默认放行_可选阻断()
    {
        var status = new Dictionary<string, TaskStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = TaskStatus.NeedsAttention
        };

        Assert.True(DagScheduler.DependenciesSatisfied(T("b", "a"), status));
        Assert.False(DagScheduler.DependenciesSatisfied(T("b", "a"), status, blockOnNeedsAttention: true));
    }

    [Fact]
    public void DependenciesSatisfied_悬空依赖_保守阻断()
    {
        Assert.False(DagScheduler.DependenciesSatisfied(T("b", "missing"),
            new Dictionary<string, TaskStatus>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void GetBlockedDependents_返回所有待命下游()
    {
        var all = new[] { T("a"), T("b", "a"), T("c", "b"), T("d") };

        var blocked = DagScheduler.GetBlockedDependents(T("a"), all, DependencyFailurePolicy.BlockDependents)
            .Select(t => t.Id).ToArray();

        // b 直接依赖 a；c 依赖 b（此时 b 仍 Pending，也在结果中，便于逐层阻断）
        Assert.Contains("b", blocked);
        Assert.Contains("c", blocked);
        Assert.DoesNotContain("d", blocked);
    }

    [Fact]
    public void GetBlockedDependents_非阻断策略_返回空()
    {
        var all = new[] { T("a"), T("b", "a") };

        Assert.Empty(DagScheduler.GetBlockedDependents(T("a"), all, DependencyFailurePolicy.AbortBatch));
        Assert.Empty(DagScheduler.GetBlockedDependents(T("a"), all, DependencyFailurePolicy.SkipDependents));
    }

    private static int IndexOf(IReadOnlyList<string> list, string id) =>
        list.ToList().FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
}
