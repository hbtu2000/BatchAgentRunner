using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;
using Xunit;

namespace BatchAgentRunner.Tests.Domain;

/// <summary>
/// P2-6 偿还守卫：工作流域模型的深拷贝契约为 <c>record with</c> 表达式（原 8 份手写
/// <c>new() { 逐字段赋值 }</c> 已收敛为 <see cref="WorkflowTypeClones"/> 扩展方法）。
/// 锁死三类不变量：① 深拷贝——克隆与原型不得共享可变节点（模板编辑器编辑副本不得污染原模板）；
/// ② 序列化形状不变——拷贝前后 JSON 逐字一致（record 化不改变用户模板契约；假设未来回归成
/// 手写 Clone 且漏字段，JSON 比对失败）；③ 逐层拷贝——聚合节点的嵌套集合 / 字典同样不共享。
/// </summary>
public class WorkflowTypeCloneTests
{
    private static WorkflowSpec SampleWorkflow() => new()
    {
        Name = "样例工作流",
        Stages = new List<WorkflowStage>
        {
            new()
            {
                Id = "s1",
                Name = "阶段一",
                InstructionTemplate = "做事",
                MaxLoops = 2,
                Executor = new StageExecutorOverride { Model = "m1", Workspace = "ws1" },
                Gate = new QualityGate { Kind = GateKind.Command, Command = "exit 0", ExpectedExitCode = "0" }
            },
            new() { Id = "s2", Name = "阶段二", InstructionTemplate = "跟进", DependsOnStage = "s1" }
        },
        OnFailure = WorkflowOnFailure.ContinueNextStage,
        DefaultMaxLoops = 5
    };

    /// <summary>序列化形状比对（记录值相等对集合按引用比较，JSON 才是用户可见契约）。</summary>
    private static void AssertSameJson<T>(T expected, T actual) =>
        Assert.Equal(JsonDefaults.Serialize(expected), JsonDefaults.Serialize(actual));

    [Fact]
    public void 工作流克隆_序列化形状一致且逐层深拷()
    {
        var original = SampleWorkflow();

        var clone = original.Clone();

        AssertSameJson(original, clone); // 漏字段即 JSON 不一致
        Assert.NotSame(original, clone);
        Assert.NotSame(original.Stages, clone.Stages);
        Assert.NotSame(original.Stages[0], clone.Stages[0]);
        Assert.NotSame(original.Stages[0].Executor, clone.Stages[0].Executor);
        Assert.NotSame(original.Stages[0].Gate, clone.Stages[0].Gate);
    }

    [Fact]
    public void 工作流克隆_改克隆不影响原型()
    {
        var original = SampleWorkflow();

        var clone = original.Clone();
        clone.Stages[0].InstructionTemplate = "改过了";
        clone.Stages[0].Executor!.Model = "m2";
        clone.Stages[0].Gate!.Command = "exit 1";
        clone.Stages.RemoveAt(1);
        clone.Name = "改名";

        Assert.Equal("做事", original.Stages[0].InstructionTemplate);
        Assert.Equal("m1", original.Stages[0].Executor!.Model);
        Assert.Equal("exit 0", original.Stages[0].Gate!.Command);
        Assert.Equal(2, original.Stages.Count);
        Assert.Equal("样例工作流", original.Name);
    }

    [Fact]
    public void 模板克隆_变量值与文件清单和内嵌工作流均深拷()
    {
        var original = new TemplateSpec
        {
            Id = "t1",
            Name = "模板一",
            InstructionTemplate = "做 {var:x}",
            Vars = new List<TemplateVarDef>
            {
                new() { Name = "x", Values = new List<string> { "a", "b" } }
            },
            FileGlob = new List<string> { "*.cs" },
            Workflow = SampleWorkflow()
        };

        var clone = original.Clone();

        AssertSameJson(original, clone);
        Assert.NotSame(original.Vars, clone.Vars);
        Assert.NotSame(original.Vars[0].Values, clone.Vars[0].Values);
        Assert.NotSame(original.FileGlob, clone.FileGlob);
        Assert.NotSame(original.Workflow, clone.Workflow);
        Assert.NotSame(original.Workflow!.Stages, clone.Workflow!.Stages);

        clone.Vars[0].Values!.Add("c");
        clone.FileGlob!.Add("*.md");
        clone.Workflow!.Stages[0].Name = "改";

        Assert.Equal(new[] { "a", "b" }, original.Vars[0].Values);
        Assert.Single(original.FileGlob);
        Assert.Equal("阶段一", original.Workflow.Stages[0].Name);
    }

    [Fact]
    public void 模板克隆_可空字段保持可空语义()
    {
        var original = new TemplateSpec { Id = "t2" }; // FileGlob / Workflow 缺省为 null

        var clone = original.Clone();

        AssertSameJson(original, clone);
        Assert.Null(clone.FileGlob);
        Assert.Null(clone.Workflow);
        Assert.NotNull(clone.Vars);
    }

    [Fact]
    public void 变量实例克隆_字典不共享()
    {
        var original = new TemplateVars { TemplateId = "t1" };
        original.Values["k"] = "v";

        var clone = original.Clone();

        AssertSameJson(original, clone);
        Assert.NotSame(original.Values, clone.Values);

        clone.Values["k"] = "改";

        Assert.Equal("v", original.Values["k"]);
    }

    [Fact]
    public void 叶子节点克隆_值等价且实例独立()
    {
        StageExecutorOverride executor = new() { AccountId = "a1", Model = "m1", ExpertId = "e1", Workspace = "ws1" };
        QualityGate gate = new() { Kind = GateKind.Review, ReviewChecklist = "清单", FailMessageTemplate = "模板" };
        ProjectRef projectRef = new() { RepoPath = @"C:\repo", BaseBranch = "main", AutoOpenPr = true };
        SessionHandle session = new() { AdapterKind = "codebuddy", SessionId = "s", AccountId = "a1" };

        // 叶子无集合字段：记录值相等 + 实例独立双重断言
        Assert.Equal(executor, executor.Clone());
        Assert.NotSame(executor, executor.Clone());
        Assert.Equal(gate, gate.Clone());
        Assert.NotSame(gate, gate.Clone());
        Assert.Equal(projectRef, projectRef.Clone());
        Assert.NotSame(projectRef, projectRef.Clone());
        Assert.Equal(session, session.Clone());
        Assert.NotSame(session, session.Clone());
    }

    [Fact]
    public void 工作流域类型_JSON序列化契约保持规范形态()
    {
        // record 化行为零变化的不变量：snake_case 字段名 + 中文不转义 + 枚举字符串化 + null 省略。
        var spec = SampleWorkflow();
        var json = JsonDefaults.Serialize(spec);

        Assert.Contains("\"name\": \"样例工作流\"", json);
        Assert.Contains("\"stages\"", json);
        Assert.Contains("\"on_failure\": \"ContinueNextStage\"", json);
        Assert.Contains("\"default_max_loops\": 5", json);
        Assert.Contains("\"instruction_template\": \"做事\"", json);
        Assert.Contains("\"expected_exit_code\": \"0\"", json);

        var round = JsonDefaults.Deserialize<WorkflowSpec>(json);
        AssertSameJson(spec, round!);
    }
}
