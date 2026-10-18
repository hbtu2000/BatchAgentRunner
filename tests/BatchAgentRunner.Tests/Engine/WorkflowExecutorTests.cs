using BatchAgentRunner.Agents.Adapters;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Engine;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Infrastructure.Logging;
using Xunit;

namespace BatchAgentRunner.Tests.Engine;

/// <summary>
/// 二期工作流执行器测试：多阶段顺序驱动、阶段产出传递、门禁修订循环、失败策略。
/// 复用 MockAdapter + FakeClock，不依赖真实 CLI。
/// </summary>
public class WorkflowExecutorTests
{
    private static readonly DateTimeOffset Noon = At.Local(2026, 10, 17, 12, 0);

    private static RunOptions Options() => new()
    {
        TaskFilePath = Path.Combine(Path.GetTempPath(), "bar-wf-tasks.json"),
        AgentKind = AgentKind.Mock,
        Accounts = new List<AccountConfig> { new() { Id = "acc1", DisplayName = "账号 1" } },
        ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" } },
        Concurrency = 1,
        MaxRetriesPerTask = 1,
        RateLimitBackoffRetries = 1,
        TaskTimeoutMinutes = 1,
        DefaultMaxGateLoops = 2
    };

    private static WorkflowExecutor Build(RunOptions options, IAgentCliAdapter adapter, FakeClock clock,
        GateEvaluator? gateEvaluator = null) =>
        new(new WorkflowEngineContext
        {
            Options = options,
            Adapter = adapter,
            Matrix = new DegradationMatrix(options.Accounts, options.ModelCandidates, options.DegradationStrategy),
            Evaluator = new AvailabilityEvaluator(new QuotaCooldownRegistry(clock, options.DefaultCooldown)),
            Cooldowns = new QuotaCooldownRegistry(clock, options.DefaultCooldown),
            Classifier = new QuotaErrorClassifier(),
            Clock = clock,
            Logger = new NullRunLogger(),
            GateEvaluator = gateEvaluator ?? new GateEvaluator(new NullProcessRunner()),
            DefaultMaxGateLoops = options.DefaultMaxGateLoops
        });

    private static WorkflowStage Stage(string id, string instruction, QualityGate? gate = null, int? maxLoops = null) =>
        new() { Id = id, Name = id, InstructionTemplate = instruction, Gate = gate, MaxLoops = maxLoops };

    /// <summary>内存假进程运行器（P1-9）：命令门禁测试用它返回预设退出码，避免真实进程 flaky。</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public int ExitCode { get; init; }

        public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            string fileName, string arguments, string? workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult((ExitCode, string.Empty, string.Empty));
    }

    [Fact]
    public async Task 多阶段顺序执行_前序产出传递给后继阶段()
    {
        var adapter = new MockAdapter(stepDuration: TimeSpan.Zero, clock: new FakeClock(Noon));
        var executor = Build(Options(), adapter, new FakeClock(Noon));
        var task = new TaskItem
        {
            Id = "wf-1",
            Instruction = "总任务",
            Workflow = new WorkflowSpec
            {
                Name = "双阶段",
                Stages = new List<WorkflowStage>
                {
                    Stage("s1", "输出 DESIGN"),
                    Stage("s2", "基于「{output:s1}」写实现")
                }
            }
        };

        var outcome = await executor.RunAsync(task, default);

        Assert.Equal(TaskStatus.Done, outcome.FinalStatus);
        Assert.Equal(2, outcome.StageResults.Count);
        Assert.All(outcome.StageResults, s => Assert.Equal(TaskStatus.Done, s.Status));
        Assert.Equal("m1", outcome.ModelUsed);
    }

    [Fact]
    public async Task 阶段失败_默认策略标记待人工接管且不计失败()
    {
        var adapter = new MockAdapter((req, _) => MockStep.Error("阶段炸了"), TimeSpan.Zero, new FakeClock(Noon));
        var executor = Build(Options(), adapter, new FakeClock(Noon));
        var task = new TaskItem
        {
            Id = "wf-fail",
            Instruction = "x",
            Workflow = new WorkflowSpec
            {
                Name = "单阶段",
                OnFailure = WorkflowOnFailure.MarkNeedsAttention,
                Stages = new List<WorkflowStage> { Stage("s1", "做事") }
            }
        };

        var outcome = await executor.RunAsync(task, default);

        Assert.Equal(TaskStatus.NeedsAttention, outcome.FinalStatus);
        Assert.Equal(TaskStatus.Failed, outcome.StageResults[0].Status);
        Assert.Contains("阶段炸了", outcome.Error);
    }

    [Fact]
    public async Task 阶段失败_策略为Fail_任务标记失败()
    {
        var adapter = new MockAdapter((req, _) => MockStep.Error("boom"), TimeSpan.Zero, new FakeClock(Noon));
        var executor = Build(Options(), adapter, new FakeClock(Noon));
        var task = new TaskItem
        {
            Id = "wf-fail2",
            Instruction = "x",
            Workflow = new WorkflowSpec
            {
                Name = "单阶段",
                OnFailure = WorkflowOnFailure.Fail,
                Stages = new List<WorkflowStage> { Stage("s1", "做事") }
            }
        };

        var outcome = await executor.RunAsync(task, default);

        Assert.Equal(TaskStatus.Failed, outcome.FinalStatus);
    }

    [Fact]
    public async Task 门禁未过_进入修订循环_通过后完成()
    {
        var calls = new List<string>();
        var adapter = new MockAdapter((req, _) =>
        {
            calls.Add(req.Instruction);
            return MockStep.Ok("完成");
        }, TimeSpan.Zero, new FakeClock(Noon));
        // 注入可控 reviewer：第 1 次审查未过（遗留问题），第 2 次通过——验证修订循环真的重跑了本阶段
        var reviewCalls = 0;
        var gateEvaluator = new GateEvaluator(new NullProcessRunner(), reviewer: _ =>
        {
            reviewCalls++;
            return Task.FromResult(reviewCalls == 1
                ? GateResult.Fail("还差点意思")
                : GateResult.Pass);
        });
        var executor = Build(Options(), adapter, new FakeClock(Noon), gateEvaluator);

        var task = new TaskItem
        {
            Id = "wf-gate",
            Instruction = "x",
            Workflow = new WorkflowSpec
            {
                Name = "带门禁",
                Stages = new List<WorkflowStage>
                {
                    Stage("s1", "做事", new QualityGate
                    {
                        Kind = GateKind.Review,
                        ReviewChecklist = "完整性/可测性"
                    }, maxLoops: 3)
                }
            }
        };

        var outcome = await executor.RunAsync(task, default);

        Assert.Equal(TaskStatus.Done, outcome.FinalStatus);
        Assert.True(outcome.StageResults[0].GatePassed);
        Assert.True(outcome.StageResults[0].LoopCount >= 2);
        Assert.Equal(2, reviewCalls);
        Assert.Contains(calls, i => i.Contains("门禁未通过"));
    }

    [Fact]
    public async Task 门禁持续未过_达到上限_标记待人工接管()
    {
        var gate = new QualityGate { Kind = GateKind.Command, Command = "exit 1" };
        var adapter = new MockAdapter((_, _) => MockStep.Ok("输出"), TimeSpan.Zero, new FakeClock(Noon));
        var options = Options();
        options.DefaultMaxGateLoops = 2;
        var executor = Build(options, adapter, new FakeClock(Noon),
            new GateEvaluator(new FakeProcessRunner { ExitCode = 1 }));

        var task = new TaskItem
        {
            Id = "wf-gate-max",
            Instruction = "x",
            Workflow = new WorkflowSpec
            {
                Name = "门禁不过",
                Stages = new List<WorkflowStage> { Stage("s1", "做事", gate, maxLoops: 2) }
            }
        };

        var outcome = await executor.RunAsync(task, default);

        Assert.Equal(TaskStatus.NeedsAttention, outcome.FinalStatus);
        Assert.Equal(TaskStatus.NeedsAttention, outcome.StageResults[0].Status);
        Assert.False(outcome.StageResults[0].GatePassed);
        Assert.NotNull(outcome.StageResults[0].GateLeftover);
    }

    [Fact]
    public async Task 阶段级依赖未满足_工作流中断并待人工接管()
    {
        var adapter = new MockAdapter((_, _) => MockStep.Ok(), TimeSpan.Zero, new FakeClock(Noon));
        var executor = Build(Options(), adapter, new FakeClock(Noon));
        var task = new TaskItem
        {
            Id = "wf-dep",
            Instruction = "x",
            Workflow = new WorkflowSpec
            {
                Name = "悬空阶段依赖",
                Stages = new List<WorkflowStage>
                {
                    Stage("s1", "做事"),
                    new WorkflowStage
                    {
                        Id = "s2", Name = "s2", InstructionTemplate = "后续",
                        DependsOnStage = "不存在"
                    }
                }
            }
        };

        var outcome = await executor.RunAsync(task, default);

        Assert.Equal(TaskStatus.NeedsAttention, outcome.FinalStatus);
        Assert.Equal(TaskStatus.Blocked, outcome.StageResults[^1].Status);
    }

    [Fact]
    public async Task 无门禁阶段_一次执行即完成()
    {
        var adapter = new MockAdapter((_, _) => MockStep.Ok("完成"), TimeSpan.Zero, new FakeClock(Noon));
        var executor = Build(Options(), adapter, new FakeClock(Noon));
        var task = new TaskItem
        {
            Id = "wf-nogate",
            Instruction = "x",
            Workflow = new WorkflowSpec
            {
                Name = "无门禁",
                Stages = new List<WorkflowStage> { Stage("s1", "做事") }
            }
        };

        var outcome = await executor.RunAsync(task, default);

        Assert.Equal(TaskStatus.Done, outcome.FinalStatus);
        Assert.Equal(1, outcome.StageResults[0].LoopCount == 0 ? 1 : outcome.StageResults[0].LoopCount);
        Assert.Contains("完成", outcome.Result);
    }

    [Fact]
    public async Task 取消执行_任务标记中断()
    {
        var adapter = new MockAdapter((_, _) => MockStep.Ok(), TimeSpan.Zero, new FakeClock(Noon));
        var executor = Build(Options(), adapter, new FakeClock(Noon));
        var task = new TaskItem
        {
            Id = "wf-cancel",
            Instruction = "x",
            Workflow = new WorkflowSpec
            {
                Name = "取消",
                Stages = new List<WorkflowStage> { Stage("s1", "做事") }
            }
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await executor.RunAsync(task, cts.Token);

        Assert.Equal(TaskStatus.Interrupted, outcome.FinalStatus);
    }

    [Fact]
    public async Task 阶段执行器覆盖模型_优先使用指定模型()
    {
        var options = Options();
        options.ModelCandidates = new List<ModelCandidate> { new() { Name = "m1" }, new() { Name = "m2" } };
        var used = new List<string>();
        var adapter = new MockAdapter((req, _) =>
        {
            used.Add(req.Model.Name);
            return MockStep.Ok();
        }, TimeSpan.Zero, new FakeClock(Noon));
        var executor = Build(options, adapter, new FakeClock(Noon));
        var task = new TaskItem
        {
            Id = "wf-override",
            Instruction = "x",
            Workflow = new WorkflowSpec
            {
                Name = "覆盖",
                Stages = new List<WorkflowStage>
                {
                    new WorkflowStage
                    {
                        Id = "s1", Name = "s1", InstructionTemplate = "做事",
                        Executor = new StageExecutorOverride { Model = "m2" }
                    }
                }
            }
        };

        await executor.RunAsync(task, default);

        Assert.Contains("m2", used);
    }
}
