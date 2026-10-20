using System.Linq;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Models;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P1-19：执行监控页 ViewModel 白箱单测（Desktop 回归面之一，churn 热点相关）。
/// 锁住确定性的公开契约：任务列表状态筛选、实时输出流跟随模式切换。引擎事件驱动路径
/// （OnEngineEvent）由 FakeEngineHost 不触发，故不在此覆盖；其线程收敛已由 UiThreadContractTests 锁死。
/// </summary>
public class MonitorViewModelTests
{
    private static TaskRowViewModel Row(string id, TaskStatus status) =>
        new(new TaskItem { Id = id, Status = status });

    [Fact]
    public void VisibleTasks_默认全量返回所有任务()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);
        vm.Tasks.Add(Row("t1", TaskStatus.Done));
        vm.Tasks.Add(Row("t2", TaskStatus.Running));

        Assert.Equal(2, vm.VisibleTasks.Count());
    }

    [Fact]
    public void VisibleTasks_按状态筛选仅返回匹配项()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);
        vm.Tasks.Add(Row("t1", TaskStatus.Done));
        vm.Tasks.Add(Row("t2", TaskStatus.Running));
        vm.Tasks.Add(Row("t3", TaskStatus.Failed));

        vm.ActiveFilter = "完成";
        Assert.Equal(new[] { "t1" }, vm.VisibleTasks.Select(t => t.Id));

        vm.ActiveFilter = "失败";
        Assert.Equal(new[] { "t3" }, vm.VisibleTasks.Select(t => t.Id));

        vm.ActiveFilter = "执行中";
        Assert.Equal(new[] { "t2" }, vm.VisibleTasks.Select(t => t.Id));
    }

    [Fact]
    public void ShowAllOutput_切换为全部模式_显示输出含全部行()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);
        vm.OutputLines.Add(new OutputLineItem(DateTimeOffset.Now, OutputTagKind.Plain, "输出", "a", dim: false, taskId: "t1"));
        vm.OutputLines.Add(new OutputLineItem(DateTimeOffset.Now, OutputTagKind.Plain, "输出", "b", dim: false, taskId: "t2"));

        // 默认随任务联动：无跟随目标 → 显示为空
        Assert.Empty(vm.DisplayedOutputLines);

        vm.ShowAllOutput = true;
        Assert.True(vm.ShowAllOutput);
        Assert.Equal(2, vm.DisplayedOutputLines.Count);
    }

    [Fact]
    public void ShowAllOutput_切回跟随模式_显示输出清空()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);
        vm.OutputLines.Add(new OutputLineItem(DateTimeOffset.Now, OutputTagKind.Plain, "输出", "a", dim: false, taskId: "t1"));

        vm.ShowAllOutput = true;
        Assert.Single(vm.DisplayedOutputLines);

        vm.ShowAllOutput = false;
        Assert.Empty(vm.DisplayedOutputLines);
    }
}
