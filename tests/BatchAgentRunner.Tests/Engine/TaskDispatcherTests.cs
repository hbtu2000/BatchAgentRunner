using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Events;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Infrastructure.Logging;
using Xunit;

namespace BatchAgentRunner.Tests.Engine;

/// <summary>
/// 派发协作者白箱单测（P1-1 拆分产物 ①，§3.4 <c>ITaskDispatcher</c>）。
/// 原「派发主循环 / 受控并发 / DAG 判定」只有 <see cref="BatchEngine"/> 黑箱覆盖；该协作者随
/// REQ-020 功能提交落地时未同步白箱测试，此处补齐：直接对 <see cref="TaskDispatcher"/> 注入假上下文，
/// 锁定串行 / 受控并发双路径、依赖失败三策略、悬空依赖保守阻断、按工作空间串行（REQ-020）、
/// 并行下统一切换（REQ-021）与停止 / 取消后排空在飞等调度不变量。
/// </summary>
[Trait("Category", "Invariant")]
public class TaskDispatcherTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    private static RunOptions Options(
        int concurrency = 1,
        DependencyFailurePolicy policy = DependencyFailurePolicy.BlockDependents) => new()
        {
            AgentKind = AgentKind.Mock,
            Accounts = new List<AccountConfig> { new() { Id = "acc1", DisplayName = "账号 1" } },
            ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" } },
            Concurrency = concurrency,
            DependencyFailurePolicy = policy,
            TaskFilePath = "C:/batch/tasks.json"
        };

    // ---------------- 串行路径 ----------------

    [Fact]
    public async Task 串行派发_按队列顺序逐个执行()
    {
        var h = new Harness(Options());
        h.AddTask("t-1");
        h.AddTask("t-2");
        h.AddTask("t-3");

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(new[] { "t-1", "t-2", "t-3" }, h.StartOrder);
        Assert.Empty(h.Blocked);
    }

    [Fact]
    public async Task 串行派发_执行中取消_停止派发后续任务()
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness(Options());
        h.AddTask("t-1");
        h.AddTask("t-2");
        h.AddTask("t-3");
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                cts.Cancel();
            }
        };

        await h.Dispatcher.DispatchAsync(h.Tasks, cts.Token);

        Assert.Equal(new[] { "t-1" }, h.StartOrder); // 取消在循环顶部生效，后续不再派发
    }

    [Fact]
    public async Task 串行派发_StopAfterCurrentTask置位_停止派发后续任务()
    {
        var h = new Harness(Options());
        h.AddTask("t-1");
        h.AddTask("t-2");
        h.AddTask("t-3");
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                h.StopAfterCurrentTask = true;
            }
        };

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(new[] { "t-1" }, h.StartOrder);
    }

    [Theory]
    [InlineData(TaskOutcome.Blocked)]
    [InlineData(TaskOutcome.Terminated)]
    [InlineData(TaskOutcome.StopBatch)]
    public async Task 串行派发_任务返回停止类结果_终止后续派发(TaskOutcome outcome)
    {
        var h = new Harness(Options());
        h.AddTask("t-1");
        h.AddTask("t-2");
        h.Outcomes["t-1"] = outcome;

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(new[] { "t-1" }, h.StartOrder);
    }

    // ---------------- 二期 DAG：依赖判定 ----------------

    [Fact]
    public async Task 含依赖批次且并发大于1_发强制串行通知并走串行路径()
    {
        var h = new Harness(Options(concurrency: 2));
        h.AddTask("t-1");
        h.AddTask("t-2", dependsOn: new List<string> { "t-1" });
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                task.Status = TaskStatus.Done;
            }
        };

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Contains(h.Events, e => e.Kind == BatchEventKind.Info && e.Message.Contains("强制串行派发"));
        Assert.Equal(new[] { "t-1", "t-2" }, h.StartOrder); // 拓扑序内串行执行
        Assert.DoesNotContain(h.Events, e => e.Kind == BatchEventKind.BatchBlocked);
    }

    [Fact]
    public async Task 依赖任务失败_默认策略_下游标记Blocked落盘并继续派发其它任务()
    {
        var h = new Harness(Options());
        var t1 = h.AddTask("t-1");
        var t2 = h.AddTask("t-2", dependsOn: new List<string> { "t-1" });
        h.AddTask("t-3");
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                task.Status = TaskStatus.Failed;
            }
        };

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(TaskStatus.Blocked, t2.Status);
        Assert.Contains("t-1", t2.Error);
        Assert.NotNull(t2.FinishedAt); // Finish 已调用，阻断即终态
        Assert.Contains(t2, h.TaskChanged);
        Assert.Equal(1, h.PersistCount); // 阻断路径落盘一次（写门 + 单写者语义由引擎侧保证）
        Assert.Contains(h.Events, e => e.Kind == BatchEventKind.TaskFinished && e.TaskId == "t-2");
        Assert.Equal(new[] { "t-1", "t-3" }, h.StartOrder); // 无依赖分支不受影响
        Assert.Equal(TaskStatus.Failed, t1.Status);
    }

    [Fact]
    public async Task 依赖失败策略AbortBatch_阻断下游_置批Blocked并发阻塞事件且终止整批()
    {
        var h = new Harness(Options(policy: DependencyFailurePolicy.AbortBatch));
        var t1 = h.AddTask("t-1");
        var t2 = h.AddTask("t-2", dependsOn: new List<string> { "t-1" });
        h.AddTask("t-3");
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                task.Status = TaskStatus.Failed;
            }
        };

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(TaskStatus.Failed, t1.Status); // 拓扑序下依赖先执行
        Assert.Equal(TaskStatus.Blocked, t2.Status);
        Assert.Contains(BatchStatus.Blocked, h.StatusChanges);
        var blocked = Assert.Single(h.Blocked);
        Assert.Contains("t-2", blocked.Reason);
        Assert.Contains("终止整批", blocked.Reason);
        Assert.DoesNotContain("t-3", h.StartOrder); // 终止整批：后续任务不再派发
    }

    [Fact]
    public async Task 依赖失败策略SkipDependents_忽略依赖失败_下游照常执行()
    {
        var h = new Harness(Options(policy: DependencyFailurePolicy.SkipDependents));
        h.AddTask("t-1");
        var t2 = h.AddTask("t-2", dependsOn: new List<string> { "t-1" });
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                task.Status = TaskStatus.Failed;
            }
        };

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(new[] { "t-1", "t-2" }, h.StartOrder);
        Assert.Equal(TaskStatus.Pending, t2.Status); // 协作者不替执行器写终态
        Assert.Equal(0, h.PersistCount);
    }

    [Fact]
    public async Task 悬空依赖_保守阻断且不终止整批()
    {
        var h = new Harness(Options());
        var t2 = h.AddTask("t-2", dependsOn: new List<string> { "ghost" });
        h.AddTask("t-3");

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(TaskStatus.Blocked, t2.Status);
        Assert.Contains("ghost", t2.Error);
        Assert.Equal(new[] { "t-3" }, h.StartOrder);
        Assert.DoesNotContain("t-2", h.StartOrder); // 阻断在执行前判定，不进入 RunTask
    }

    [Fact]
    public async Task 依赖NeedAttention_BlockDependents策略_阻断下游()
    {
        var h = new Harness(Options());
        h.AddTask("t-1");
        var t2 = h.AddTask("t-2", dependsOn: new List<string> { "t-1" });
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                task.Status = TaskStatus.NeedsAttention;
            }
        };

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(TaskStatus.Blocked, t2.Status);
        Assert.DoesNotContain("t-2", h.StartOrder);
    }

    [Fact]
    public async Task 依赖未到终态_保守阻断()
    {
        var h = new Harness(Options());
        h.AddTask("t-1");
        var t2 = h.AddTask("t-2", dependsOn: new List<string> { "t-1" });
        // t-1 执行后仍为 Pending（非终态）：串行拓扑序下不出现，保守按阻断处理

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(TaskStatus.Blocked, t2.Status);
        Assert.Contains("被阻断", t2.Error); // Wait 判定不记录具体依赖，理由为「（未知）」
        Assert.Equal(new[] { "t-1" }, h.StartOrder);
    }

    // ---------------- 受控并发（REQ-020 / REQ-021） ----------------

    [Fact]
    public async Task 受控并发_同工作空间串行_并行度受不同待派发工作空间数约束()
    {
        var h = new Harness(Options(concurrency: 2), gated: true);
        h.AddTask("t-1", workspace: "ws-a");
        h.AddTask("t-2", workspace: "ws-a");
        h.AddTask("t-3", workspace: "ws-b");

        var dispatch = h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);
        await h.Started("t-1").WaitAsync(GateTimeout);
        await h.Started("t-3").WaitAsync(GateTimeout);

        // ws-a 已有在飞 → t-2 不可派发；ws-b 可并行
        Assert.Equal(2, h.MaxConcurrent);
        Assert.DoesNotContain("t-2", h.StartOrder);

        h.Release("t-1");
        await h.Finished("t-1").WaitAsync(GateTimeout);
        // 仅剩 ws-a 有待派发任务 → cap = min(2, 1) = 1，t-3 仍在飞 → t-2 继续等待
        Assert.DoesNotContain("t-2", h.StartOrder);

        h.Release("t-3");
        await h.Started("t-2").WaitAsync(GateTimeout);
        Assert.Equal(new[] { "t-1", "t-3" }, h.FinishOrder); // t-2 最后收尾
        h.Release("t-2");

        await dispatch.WaitAsync(GateTimeout);
        Assert.Equal(3, h.StartOrder.Count);
    }

    [Fact]
    public async Task 受控并发_并行度不超过配置并发数()
    {
        var h = new Harness(Options(concurrency: 2), gated: true);
        h.AddTask("t-1", workspace: "ws-a");
        h.AddTask("t-2", workspace: "ws-b");
        h.AddTask("t-3", workspace: "ws-c");
        h.AddTask("t-4", workspace: "ws-d");

        var dispatch = h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);
        await h.Started("t-1").WaitAsync(GateTimeout);
        await h.Started("t-2").WaitAsync(GateTimeout);
        Assert.Equal(2, h.MaxConcurrent); // 4 个不同工作空间，cap 仍为并发数 2
        Assert.DoesNotContain("t-3", h.StartOrder);

        h.Release("t-1");
        await h.Started("t-3").WaitAsync(GateTimeout);
        Assert.Equal(2, h.MaxConcurrent);
        h.Release("t-2");
        h.Release("t-3");
        await h.Started("t-4").WaitAsync(GateTimeout);
        Assert.Equal(2, h.MaxConcurrent);
        h.Release("t-4");

        await dispatch.WaitAsync(GateTimeout);
        Assert.Equal(4, h.StartOrder.Count);
    }

    [Fact]
    public async Task 受控并发_在飞请求统一切换_在飞全部结束后执行一次切换()
    {
        var h = new Harness(Options(concurrency: 2), gated: true);
        h.AddTask("t-1", workspace: "ws-a");
        h.AddTask("t-2", workspace: "ws-b");
        h.AddTask("t-3", workspace: "ws-a");
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                // 模拟 TaskRunner 在飞请求延后切换：等 t-2 已在飞后再冻结槽位 + 派发暂停 + 记录目标
                // （同步请求会立即暂停派发导致 t-2 无法被派发，与真实时序不符）
                await h.Started("t-2");
                h.Coordinator.RequestDeferredSwitch(task.Id, "acc2");
            }
        };

        var dispatch = h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);
        await h.Started("t-1").WaitAsync(GateTimeout);
        await h.Started("t-2").WaitAsync(GateTimeout);

        h.Release("t-1");
        await h.Finished("t-1").WaitAsync(GateTimeout);
        Assert.Empty(h.UnifiedSwitches); // t-2 仍在飞 → 不触发
        Assert.DoesNotContain("t-3", h.StartOrder); // 派发暂停闸挡住新任务

        h.Release("t-2");
        await h.Switched.Task.WaitAsync(GateTimeout); // 在飞降为 0 → 执行一次统一切换
        h.Release("t-3");

        await dispatch.WaitAsync(GateTimeout);
        Assert.Equal(new[] { "acc2" }, h.UnifiedSwitches); // 全程仅一次
        Assert.Equal(3, h.StartOrder.Count); // 暂停解除后 t-3 补派
    }

    [Fact]
    public async Task 受控并发_统一切换失败_只告警且不影响派发收尾()
    {
        var h = new Harness(Options(concurrency: 2)) { SwitchThrows = true };
        h.AddTask("t-1", workspace: "ws-a");
        h.AddTask("t-2", workspace: "ws-b");
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                // 等 t-2 已在飞后再请求延后切换：切换执行时另一任务已收尾，验证失败只告警
                await h.Started("t-2");
                h.Coordinator.RequestDeferredSwitch(task.Id, "acc2");
            }
        };

        await h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);

        Assert.Equal(new[] { "acc2" }, h.UnifiedSwitches);
        Assert.Equal(2, h.StartOrder.Count);
        Assert.Contains(h.Events, e => e.Kind == BatchEventKind.Warning && e.Message.Contains("并行下统一账号切换失败"));
    }

    [Fact]
    public async Task 受控并发_停止派发后_排空在飞任务且不派发新任务()
    {
        var h = new Harness(Options(concurrency: 2), gated: true);
        h.AddTask("t-1", workspace: "ws-a");
        h.AddTask("t-2", workspace: "ws-b");
        h.AddTask("t-3", workspace: "ws-c");
        h.Outcomes["t-1"] = TaskOutcome.Blocked; // 停止类结果 → 停止派发

        var dispatch = h.Dispatcher.DispatchAsync(h.Tasks, CancellationToken.None);
        await h.Started("t-1").WaitAsync(GateTimeout);
        await h.Started("t-2").WaitAsync(GateTimeout);

        h.Release("t-1");
        await h.Finished("t-1").WaitAsync(GateTimeout);
        Assert.False(dispatch.IsCompleted); // t-2 仍在飞 → DispatchAsync 尚未返回（排空语义）

        h.Release("t-2");
        await dispatch.WaitAsync(GateTimeout);

        Assert.Equal(2, h.StartOrder.Count);
        Assert.DoesNotContain("t-3", h.StartOrder); // 停止后不派发新任务
        Assert.Contains("t-2", h.FinishOrder); // 在飞任务被排空而非丢弃
    }

    [Fact]
    public async Task 受控并发_执行中取消_停止派发新任务且排空在飞()
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness(Options(concurrency: 2), gated: true);
        h.AddTask("t-1", workspace: "ws-a");
        h.AddTask("t-2", workspace: "ws-b");
        h.AddTask("t-3", workspace: "ws-c");
        h.OnRun = async task =>
        {
            if (task.Id == "t-1")
            {
                cts.Cancel();
            }
        };

        var dispatch = h.Dispatcher.DispatchAsync(h.Tasks, cts.Token);
        await h.Started("t-1").WaitAsync(GateTimeout);
        await h.Started("t-2").WaitAsync(GateTimeout);

        h.Release("t-1");
        await h.Finished("t-1").WaitAsync(GateTimeout);
        Assert.False(dispatch.IsCompleted);

        h.Release("t-2");
        await dispatch.WaitAsync(GateTimeout);

        Assert.Equal(2, h.StartOrder.Count);
        Assert.DoesNotContain("t-3", h.StartOrder);
        Assert.Contains("t-2", h.FinishOrder);
    }

    // ---------------- 测试替身与装配 ----------------

    /// <summary>
    /// 派发协作者测试装配：可编程 <see cref="TaskDispatcherContext"/>（事件 / 状态 / 落盘全部可观测），
    /// RunTask 替身支持门控（未释放 = 在飞）、按任务覆写结果与执行钩子（置终态 / 取消 / 请求统一切换）。
    /// </summary>
    private sealed class Harness
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskCompletionSource> _started = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskCompletionSource> _finished = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _gated;
        private int _concurrent;

        public Harness(RunOptions options, bool gated = false)
        {
            _gated = gated;
            Dispatcher = new TaskDispatcher(new TaskDispatcherContext
            {
                Options = options,
                Tasks = Tasks,
                Clock = new FakeClock(At.Local(2026, 10, 15, 12, 0)),
                Logger = new NullRunLogger(),
                RunTask = RunTaskAsync,
                EnterWriteGate = () => Task.FromResult<IDisposable>(new NoopDisposable()),
                Persist = _ =>
                {
                    lock (_sync) { PersistCount++; }
                    return Task.CompletedTask;
                },
                StopAfterCurrentTask = () => StopAfterCurrentTask,
                Raise = ev =>
                {
                    lock (_sync) { Events.Add(ev); }
                },
                SetStatus = status =>
                {
                    lock (_sync) { StatusChanges.Add(status); }
                },
                RaiseBlocked = (reason, taskId) =>
                {
                    lock (_sync) { Blocked.Add((reason, taskId)); }
                },
                RaiseTaskChanged = task =>
                {
                    lock (_sync) { TaskChanged.Add(task); }
                },
                Coordinator = Coordinator,
                PerformUnifiedSwitch = (target, _) =>
                {
                    lock (_sync)
                    {
                        UnifiedSwitches.Add(target);
                        if (SwitchThrows)
                        {
                            throw new InvalidOperationException("切换通道故障");
                        }
                    }

                    Switched.TrySetResult();
                    return Task.CompletedTask;
                }
            });
        }

        public List<TaskItem> Tasks { get; } = new();

        public TaskDispatcher Dispatcher { get; }

        public DispatchCoordinator Coordinator { get; } = new();

        public List<BatchEvent> Events { get; } = new();

        public List<string> StartOrder { get; } = new();

        public List<string> FinishOrder { get; } = new();

        public List<BatchStatus> StatusChanges { get; } = new();

        public List<(string Reason, string? TaskId)> Blocked { get; } = new();

        public List<TaskItem> TaskChanged { get; } = new();

        public List<string> UnifiedSwitches { get; } = new();

        public TaskCompletionSource Switched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<string, TaskOutcome> Outcomes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool StopAfterCurrentTask { get; set; }

        public bool SwitchThrows { get; set; }

        public Func<TaskItem, Task>? OnRun { get; set; }

        public int PersistCount { get; private set; }

        public int MaxConcurrent { get; private set; }

        public TaskItem AddTask(string id, string? workspace = null, List<string>? dependsOn = null)
        {
            var task = new TaskItem
            {
                Id = id,
                Instruction = "指令 " + id,
                Workspace = workspace,
                DependsOn = dependsOn
            };
            Tasks.Add(task);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_gated)
            {
                gate.TrySetResult(); // 非门控模式：放行即完成
            }

            _gates[id] = gate;
            _started[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _finished[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return task;
        }

        public Task Started(string id) => _started[id].Task;

        public Task Finished(string id) => _finished[id].Task;

        public void Release(string id) => _gates[id].TrySetResult();

        private Task<TaskOutcome> RunTaskAsync(TaskItem task, CancellationToken ct)
        {
            lock (_sync)
            {
                StartOrder.Add(task.Id);
                _concurrent++;
                MaxConcurrent = Math.Max(MaxConcurrent, _concurrent);
            }

            _started[task.Id].TrySetResult();

            return RunCoreAsync(task);

            async Task<TaskOutcome> RunCoreAsync(TaskItem t)
            {
                try
                {
                    if (OnRun is not null)
                    {
                        await OnRun(t);
                    }

                    await _gates[t.Id].Task; // 门控：未释放前视为在飞
                    return Outcomes.TryGetValue(t.Id, out var outcome) ? outcome : TaskOutcome.Finished;
                }
                finally
                {
                    lock (_sync)
                    {
                        _concurrent--;
                        FinishOrder.Add(t.Id);
                    }

                    _finished[t.Id].TrySetResult();
                }
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
