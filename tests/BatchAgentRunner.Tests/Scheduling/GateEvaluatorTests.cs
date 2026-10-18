using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using Xunit;

namespace BatchAgentRunner.Tests.Scheduling;

public class GateEvaluatorTests
{
    private static GateEvaluationContext Ctx(QualityGate gate, string workingDir) => new()
    {
        Gate = gate,
        StageOutput = "阶段产出",
        WorkingDirectory = workingDir,
        TaskId = "task-1",
        StageId = "s1"
    };

    /// <summary>内存假进程运行器：返回预设的退出码/输出，彻底消除真实进程的环境耦合与 flaky（P1-9）。</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = string.Empty;
        public string StdErr { get; init; } = string.Empty;

        public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            string fileName, string arguments, string? workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult((ExitCode, StdOut, StdErr));
    }

    [Fact]
    public async Task 命令门禁_退出码为0_通过()
    {
        var gate = new QualityGate { Kind = GateKind.Command, Command = "exit 0" };
        var evaluator = new GateEvaluator(new FakeProcessRunner { ExitCode = 0 });

        var result = await evaluator.EvaluateAsync(gate, Ctx(gate, "."), default);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task 命令门禁_退出码非0_失败并带详情()
    {
        var gate = new QualityGate { Kind = GateKind.Command, Command = "exit 3" };
        var evaluator = new GateEvaluator(new FakeProcessRunner { ExitCode = 3 });

        var result = await evaluator.EvaluateAsync(gate, Ctx(gate, "."), default);

        Assert.False(result.Passed);
        Assert.Contains("3", result.Leftover);
    }

    [Fact]
    public async Task 命令门禁_期望退出码可自定义()
    {
        var gate = new QualityGate { Kind = GateKind.Command, Command = "exit 7", ExpectedExitCode = "7" };
        var evaluator = new GateEvaluator(new FakeProcessRunner { ExitCode = 7 });

        var result = await evaluator.EvaluateAsync(gate, Ctx(gate, "."), default);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task 命令门禁_输出正则匹配判定()
    {
        var okGate = new QualityGate
        {
            Kind = GateKind.Command,
            Command = "echo all tests passed",
            OutputMatchRegex = @"all\s+tests\s+passed"
        };
        var okEvaluator = new GateEvaluator(new FakeProcessRunner { StdOut = "all tests passed" });
        Assert.True((await okEvaluator.EvaluateAsync(okGate, Ctx(okGate, "."), default)).Passed);

        var badGate = new QualityGate
        {
            Kind = GateKind.Command,
            Command = "echo something else",
            OutputMatchRegex = @"all\s+tests\s+passed"
        };
        var badEvaluator = new GateEvaluator(new FakeProcessRunner { StdOut = "something else" });
        Assert.False((await badEvaluator.EvaluateAsync(badGate, Ctx(badGate, "."), default)).Passed);
    }

    [Fact]
    public async Task 命令为空_直接通过()
    {
        var gate = new QualityGate { Kind = GateKind.Command, Command = "   " };

        Assert.True((await new GateEvaluator(new FakeProcessRunner()).EvaluateAsync(gate, Ctx(gate, "."), default)).Passed);
    }

    [Fact]
    public async Task 失败消息模板_替换占位符()
    {
        var gate = new QualityGate
        {
            Kind = GateKind.Command,
            Command = "exit 1",
            FailMessageTemplate = "构建失败：{stage.output}"
        };
        var evaluator = new GateEvaluator(new FakeProcessRunner { ExitCode = 1, StdErr = "编译错误" });

        var result = await evaluator.EvaluateAsync(gate, Ctx(gate, "."), default);

        Assert.False(result.Passed);
        Assert.StartsWith("构建失败：", result.Leftover);
    }

    [Fact]
    public async Task 审查门禁_未注入审查器_默认通过()
    {
        var gate = new QualityGate { Kind = GateKind.Review, ReviewChecklist = "检查命名" };

        Assert.True((await new GateEvaluator(new FakeProcessRunner()).EvaluateAsync(gate, Ctx(gate, "."), default)).Passed);
    }

    [Fact]
    public async Task 审查门禁_注入审查器_按其结论()
    {
        var gate = new QualityGate { Kind = GateKind.Review };
        var evaluator = new GateEvaluator(new FakeProcessRunner(), reviewer: _ => Task.FromResult(GateResult.Fail("缺少单元测试")));

        var result = await evaluator.EvaluateAsync(gate, Ctx(gate, "."), default);

        Assert.False(result.Passed);
        Assert.Equal("缺少单元测试", result.Leftover);
    }

    [Fact]
    public async Task 人工门禁_未注入审批器_默认通过()
    {
        var gate = new QualityGate { Kind = GateKind.Manual };

        Assert.True((await new GateEvaluator(new FakeProcessRunner()).EvaluateAsync(gate, Ctx(gate, "."), default)).Passed);
    }

    [Fact]
    public async Task 人工门禁_审批被拒_失败()
    {
        var gate = new QualityGate { Kind = GateKind.Manual };
        var evaluator = new GateEvaluator(new FakeProcessRunner(), manualApprover: _ => Task.FromResult(false));

        Assert.False((await evaluator.EvaluateAsync(gate, Ctx(gate, "."), default)).Passed);
    }
}
