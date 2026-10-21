using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Desktop.Models;
using BatchAgentRunner.Desktop.Tests.Helpers;
using BatchAgentRunner.Desktop.Tests.Infrastructure;
using BatchAgentRunner.Desktop.ViewModels;
using Xunit;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Desktop.Tests.ViewModels;

/// <summary>
/// P-06 准入契约：执行监控页 facade（P-06 分解收口产物）公开契约锁。
/// <para>
/// 原 <c>MonitorViewModel</c> 是 983 行上帝类，P-06 已按「状态持有 + 绑定转发 + 事件编排」
/// 下沉为 Monitor 命名空间下的协作者（OutputStreamCoordinator / BatchStatsState / BatchStatusState /
/// EventToastBoard / TaskListCoordinator / BatchStatistics / OutputTagClassifier），facade 仅转发。
/// 本测试锁死 facade 的对外契约在分解前后逐字节等价：集合透传、统计/状态/输出流属性转发、
/// 引擎事件路由（StatusChanged / TaskOutput / TaskStarted / BatchWaiting / BatchResumed）、
/// 选中任务派生属性、命令可用性。配合 <see cref="MonitorLoginViewBindingContractTests"/>
/// （XAML 绑定路径静态可解析）共同构成 R-4（VM 分解破坏 UI 行为）的机器防线。
/// </para>
/// </summary>
public class MonitorViewModelContractTests : DesktopHeadlessTestBase
{
    private static TaskRowViewModel Row(string id, TaskStatus status) =>
        new(new TaskItem { Id = id, Status = status });

    [AvaloniaFact]
    public void 构造时_统计状态与批次状态取默认值()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);

        // 统计默认（无任务）
        Assert.Equal(0, vm.Total);
        Assert.Equal(0, vm.Done);
        Assert.Equal(0, vm.Running);
        Assert.Equal(0, vm.Skipped);
        Assert.Equal(0.0, vm.ProgressPercent);
        Assert.Equal("0 / 0", vm.ProgressText);
        Assert.Equal("—", vm.EtaText);
        Assert.Equal("无", vm.CurrentTaskText);

        // 批次状态默认（宿主 Idle）
        Assert.Equal(BatchStatus.Idle, vm.Status);
        Assert.Equal("空闲", vm.StatusText);
        Assert.False(vm.IsWaiting);
        Assert.NotNull(vm.StatusBrush);

        // 命令可用性默认（宿主未运行 + 任务文件为空 → 不可开始）
        Assert.False(vm.CanStart);
        Assert.False(vm.CanTerminate);
        Assert.False(vm.CanCancelWaiting);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void 集合属性_透传协作者同一实例()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);

        // 反复读取返回同一实例（XAML 绑定与集合通知契约）
        Assert.Same(vm.Tasks, vm.Tasks);
        Assert.Same(vm.Toasts, vm.Toasts);
        Assert.Same(vm.OutputLines, vm.OutputLines);
        Assert.Same(vm.DisplayedOutputLines, vm.DisplayedOutputLines);
        Assert.Same(vm.SelectedStages, vm.SelectedStages);
        Assert.Same(vm.DagRows, vm.DagRows);

        // 经 facade 集合写入即可见（数据源是协作者集合本身）
        vm.Tasks.Add(Row("t1", TaskStatus.Done));
        Assert.Single(vm.Tasks);
    }

    [AvaloniaFact]
    public void 引擎事件_StatusChanged_驱动批次状态与派发暂停解除()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);

        fake.RaiseEvent(new BatchEvent(BatchEventKind.StatusChanged, DateTimeOffset.Now, string.Empty)
        { BatchStatus = BatchStatus.Running });
        Assert.Equal(BatchStatus.Running, vm.Status);
        Assert.Equal("运行中", vm.StatusText);
        Assert.False(vm.IsWaiting);
        // CanTerminate 读宿主 IsRunning（Fake 恒 false）——与分解前语义一致
        Assert.False(vm.CanTerminate);

        fake.RaiseEvent(new BatchEvent(BatchEventKind.StatusChanged, DateTimeOffset.Now, string.Empty)
        { BatchStatus = BatchStatus.Waiting });
        Assert.Equal(BatchStatus.Waiting, vm.Status);
        Assert.True(vm.IsWaiting);
        Assert.True(vm.CanCancelWaiting);
        Assert.True(vm.CancelWaitingCommand.CanExecute(null));

        // SlotFrozen → 派发暂停；终态（Completed）→ 解除暂停
        fake.RaiseEvent(new BatchEvent(BatchEventKind.SlotFrozen, DateTimeOffset.Now, "槽位冻结"));
        Assert.True(vm.DispatchPaused);

        fake.RaiseEvent(new BatchEvent(BatchEventKind.StatusChanged, DateTimeOffset.Now, string.Empty)
        { BatchStatus = BatchStatus.Completed });
        Assert.False(vm.DispatchPaused);
        Assert.Equal("已完成", vm.StatusText);
    }

    [AvaloniaFact]
    public void 引擎事件_TaskOutput与TaskStarted_写入输出流并按任务联动()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);
        vm.Tasks.Add(Row("t1", TaskStatus.Running)); // 跟随标题经 taskItems 委托解析目标任务

        fake.RaiseEvent(new BatchEvent(BatchEventKind.TaskOutput, DateTimeOffset.Now, "hello", TaskId: "t1"));

        // 全局缓冲写入；默认无跟随目标 → 展示为空
        Assert.Single(vm.OutputLines);
        Assert.Empty(vm.DisplayedOutputLines);

        // TaskStarted → 自动跟随该任务 → 重建展示视图（含此前写入的 t1 行）
        fake.RaiseEvent(new BatchEvent(BatchEventKind.TaskStarted, DateTimeOffset.Now, "开始", TaskId: "t1"));
        Assert.Equal(2, vm.DisplayedOutputLines.Count);
        Assert.Equal("hello", vm.DisplayedOutputLines[0].Message);
        Assert.Equal("开始", vm.DisplayedOutputLines[1].Message); // 调度类事件经 AppendEvent 并入
        Assert.Equal("实时输出流 · t1", vm.OutputFollowTitle);
        Assert.True(vm.HasDisplayedOutput);

        // 全部模式 → 展示等于全局缓冲
        vm.ShowAllOutput = true;
        Assert.Equal(2, vm.DisplayedOutputLines.Count);
        Assert.Equal("实时输出流 · 全部任务", vm.OutputFollowTitle);

        // 调度类事件（非 TaskOutput/StatusChanged）并入提示板
        Assert.Contains(vm.Toasts, t => t.Message == "开始");
    }

    [AvaloniaFact]
    public void 引擎事件_BatchWaiting写入倒计时_BatchResumed清空()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);

        fake.RaiseEvent(new BatchEvent(BatchEventKind.BatchWaiting, DateTimeOffset.Now, "等待恢复")
        { ResumeAt = DateTimeOffset.Now.AddSeconds(90) });
        Assert.Contains("等待至", vm.WaitingText);
        Assert.Contains("剩余", vm.WaitingText);

        fake.RaiseEvent(new BatchEvent(BatchEventKind.BatchResumed, DateTimeOffset.Now, "已恢复"));
        Assert.Equal(string.Empty, vm.WaitingText);
    }

    [AvaloniaFact]
    public void 选中任务_派生属性与阶段树输出流联动()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);

        var item = new TaskItem
        {
            Id = "t1",
            Instruction = "指令A",
            Result = "结果A",
            Error = "错误A",
            StageResults = new List<StageResult>()
        };
        item.StageResults.Add(new StageResult { StageId = "s1" });
        vm.Tasks.Add(new TaskRowViewModel(item));

        vm.SelectedTask = vm.Tasks.Single();

        Assert.True(vm.HasSelectedTask);
        Assert.Equal("指令A", vm.SelectedInstruction);
        Assert.Equal("结果A", vm.SelectedResult);
        Assert.Equal("错误A", vm.SelectedError);
        Assert.True(vm.SelectedHasError);
        Assert.Single(vm.SelectedStages); // 阶段树经 TaskListCoordinator 重建

        // 选中即固定输出流跟随目标
        fake.RaiseEvent(new BatchEvent(BatchEventKind.TaskOutput, DateTimeOffset.Now, "x", TaskId: "t1"));
        Assert.Single(vm.DisplayedOutputLines);
    }

    [AvaloniaFact]
    public void 选中工作流任务_模板溯源属性可解析()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);

        var workflowItem = new TaskItem
        {
            Id = "w1",
            Workflow = new WorkflowSpec { Stages = new List<WorkflowStage>() },
            TemplateVars = new TemplateVars { TemplateId = "tpl-1" }
        };
        workflowItem.Workflow.Stages.Add(new WorkflowStage());
        vm.Tasks.Add(new TaskRowViewModel(workflowItem));

        vm.SelectedTask = vm.Tasks.Single();
        Assert.True(vm.SelectedIsWorkflow);
        Assert.True(vm.HasSelectedWorkflow);
        Assert.Equal("tpl-1", vm.SelectedTemplateId);
        Assert.True(vm.HasSelectedTemplate);
    }

    [AvaloniaFact]
    public void 引擎事件带任务_统计转发与看板重建()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);

        var t1 = new TaskItem { Id = "t1", Status = TaskStatus.Running, AccountUsed = "acc-1", ModelUsed = "m1" };
        var t2 = new TaskItem { Id = "t2", Status = TaskStatus.Pending, DependsOn = new List<string> { "t1" } };

        fake.RaiseEvent(new BatchEvent(BatchEventKind.TaskStarted, DateTimeOffset.Now, "开始", TaskId: "t1") { Task = t1 });
        fake.RaiseEvent(new BatchEvent(BatchEventKind.TaskStarted, DateTimeOffset.Now, "开始", TaskId: "t2") { Task = t2 });

        // 行同步（Upsert）→ facade 集合可见
        Assert.Equal(2, vm.Tasks.Count);

        // 统计经 BatchStatsState 转发到 facade（Recalculate 链路）
        Assert.Equal(2, vm.Total);
        Assert.Equal(1, vm.Running);
        Assert.Equal(1, vm.Pending);
        Assert.Contains("t1", vm.CurrentTaskText);
        Assert.Contains("acc-1", vm.CurrentComboText);
        Assert.Equal(0.0, vm.ProgressPercent, 3); // Done+Failed+Interrupted=0 / Total=2

        // DAG 看板：t2 依赖 t1 → 两行、HasDag 为真
        Assert.True(vm.HasDag);
        Assert.Equal(2, vm.DagRows.Count);
    }

    [AvaloniaFact]
    public void ForceRerun与WaitIfUnavailable_勾选写回宿主配置或保持本地()
    {
        var fake = new FakeEngineHost(new Core.Options.RunOptions { ForceRerun = false });
        var vm = new MonitorViewModel(fake);

        Assert.False(vm.ForceRerun);
        vm.ForceRerun = true;
        Assert.True(fake.Options.ForceRerun); // 写回宿主配置（与原实现一致）

        Assert.True(vm.WaitIfUnavailable); // 默认勾选
        vm.WaitIfUnavailable = false;
        Assert.False(vm.WaitIfUnavailable);
    }

    [AvaloniaFact]
    public void 加载命令_未选任务文件时给出警告提示()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);

        // TaskFilePath 为空 → NotifyIssue（状态栏 + 提示板双写，UiThread 在 UI 线程上同步执行）
        vm.LoadCommand.Execute(null);

        Assert.Contains("请先在配置页选择任务文件", vm.StatusMessage);
        Assert.Contains(vm.Toasts, t => t.Message.Contains("请先在配置页选择任务文件"));
    }

    [AvaloniaFact]
    public void VisibleTasks_筛选切换时通知可见集合()
    {
        var fake = new FakeEngineHost();
        var vm = new MonitorViewModel(fake);
        vm.Tasks.Add(Row("t1", TaskStatus.Done));
        vm.Tasks.Add(Row("t2", TaskStatus.Failed));

        Assert.Equal("全部", vm.ActiveFilter);
        Assert.Equal(2, vm.VisibleTasks.Count());

        vm.SetFilterCommand.Execute("失败");
        Assert.Equal("失败", vm.ActiveFilter);
        Assert.Equal(new[] { "t2" }, vm.VisibleTasks.Select(t => t.Id));

        vm.SetFilterCommand.Execute(null); // null → 回退「全部」
        Assert.Equal("全部", vm.ActiveFilter);
        Assert.Equal(2, vm.VisibleTasks.Count());
    }
}
