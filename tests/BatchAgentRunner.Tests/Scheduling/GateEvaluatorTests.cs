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

    [Fact]
    public async Task 命令门禁_退出码为0_通过()
    {
        var gate = new QualityGate { Kind = GateKind.Command, Command = "exit 0" };

        var result = await new GateEvaluator().EvaluateAsync(gate, Ctx(gate, Directory.GetCurrentDirectory()), default);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task 命令门禁_退出码非0_失败并带详情()
    {
        var gate = new QualityGate { Kind = GateKind.Command, Command = "exit 3" };

        var result = await new GateEvaluator().EvaluateAsync(gate, Ctx(gate, Directory.GetCurrentDirectory()), default);

        Assert.False(result.Passed);
        Assert.Contains("3", result.Leftover);
    }

    [Fact]
    public async Task 命令门禁_期望退出码可自定义()
    {
        var gate = new QualityGate { Kind = GateKind.Command, Command = "exit 7", ExpectedExitCode = "7" };

        var result = await new GateEvaluator().EvaluateAsync(gate, Ctx(gate, Directory.GetCurrentDirectory()), default);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task 命令门禁_输出正则匹配判定()
    {
        var ok = new QualityGate
        {
            Kind = GateKind.Command,
            Command = "echo all tests passed",
            OutputMatchRegex = @"all\s+tests\s+passed"
        };
        var bad = new QualityGate
        {
            Kind = GateKind.Command,
            Command = "echo something else",
            OutputMatchRegex = @"all\s+tests\s+passed"
        };
        var evaluator = new GateEvaluator();
        var dir = Directory.GetCurrentDirectory();

        Assert.True((await evaluator.EvaluateAsync(ok, Ctx(ok, dir), default)).Passed);
        Assert.False((await evaluator.EvaluateAsync(bad, Ctx(bad, dir), default)).Passed);
    }

    [Fact]
    public async Task 命令为空_直接通过()
    {
        var gate = new QualityGate { Kind = GateKind.Command, Command = "   " };

        Assert.True((await new GateEvaluator().EvaluateAsync(gate, Ctx(gate, "."), default)).Passed);
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

        var result = await new GateEvaluator().EvaluateAsync(gate, Ctx(gate, Directory.GetCurrentDirectory()), default);

        Assert.False(result.Passed);
        Assert.StartsWith("构建失败：", result.Leftover);
    }

    [Fact]
    public async Task 审查门禁_未注入审查器_默认通过()
    {
        var gate = new QualityGate { Kind = GateKind.Review, ReviewChecklist = "检查命名" };

        Assert.True((await new GateEvaluator().EvaluateAsync(gate, Ctx(gate, "."), default)).Passed);
    }

    [Fact]
    public async Task 审查门禁_注入审查器_按其结论()
    {
        var gate = new QualityGate { Kind = GateKind.Review };
        var evaluator = new GateEvaluator(reviewer: _ => Task.FromResult(GateResult.Fail("缺少单元测试")));

        var result = await evaluator.EvaluateAsync(gate, Ctx(gate, "."), default);

        Assert.False(result.Passed);
        Assert.Equal("缺少单元测试", result.Leftover);
    }

    [Fact]
    public async Task 人工门禁_未注入审批器_默认通过()
    {
        var gate = new QualityGate { Kind = GateKind.Manual };

        Assert.True((await new GateEvaluator().EvaluateAsync(gate, Ctx(gate, "."), default)).Passed);
    }

    [Fact]
    public async Task 人工门禁_审批被拒_失败()
    {
        var gate = new QualityGate { Kind = GateKind.Manual };
        var evaluator = new GateEvaluator(manualApprover: _ => Task.FromResult(false));

        Assert.False((await evaluator.EvaluateAsync(gate, Ctx(gate, "."), default)).Passed);
    }
}
