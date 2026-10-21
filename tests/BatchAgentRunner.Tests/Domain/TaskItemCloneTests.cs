using System.Reflection;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Serialization;
using Xunit;

namespace BatchAgentRunner.Tests.Domain;

/// <summary>
/// P2-6 / ARCH P-08 偿还守卫：<see cref="TaskItem.Clone"/> 是全仓唯一剩余的手写深拷贝点
/// （工作流域 8 份已收敛为 <c>record with</c>，见 <see cref="WorkflowTypeCloneTests"/>；
/// <c>TaskItem</c> 因承载 <see cref="System.ComponentModel.INotifyPropertyChanged"/> 通知字段而无法记录化，
/// 故保留手写拷贝，由本测试锁死「改字段必漏改」这一根因）。
///
/// 三条不变量：
/// ① **字段完整性**：反射遍历全部可写属性，克隆后逐一值相等——新增字段一旦漏进 <c>Clone()</c> 即失败；
/// ② **深拷贝**：引用类型属性不得与原实例共享（集合 / 聚合节点逐层拷贝）；
/// ③ **序列化形状不变**：克隆前后 JSON 逐字一致（Clone 用于快照与重跑，形状漂移即用户可见契约破坏）。
/// </summary>
public class TaskItemCloneTests
{
    /// <summary>克隆前后的序列化形状比对（值相等对集合按引用比较，JSON 才是用户可见契约）。</summary>
    private static void AssertSameJson(TaskItem expected, TaskItem actual) =>
        Assert.Equal(JsonDefaults.Serialize(expected), JsonDefaults.Serialize(actual));

    [Fact]
    public void 任务克隆_全部可写属性逐字段值等价且不共享可变引用()
    {
        var original = SampleTask();

        var clone = original.Clone();

        var problems = new List<string>();
        foreach (var p in WritableProperties())
        {
            var before = p.GetValue(original);
            var after = p.GetValue(clone);

            if (!SameValue(before, after))
            {
                problems.Add($"{p.Name} 值不等价（原：{Describe(before)} → 克隆：{Describe(after)}）");
                continue;
            }

            // 字符串 / 值类型无共享可变状态问题；其余引用类型必须逐层拷贝。
            if (before is not null && p.PropertyType != typeof(string) && p.PropertyType.IsClass &&
                ReferenceEquals(before, after))
            {
                problems.Add($"{p.Name} 与原实例共享引用（深拷贝漏拷）");
            }
        }

        Assert.True(
            problems.Count == 0,
            "TaskItem.Clone 字段完整性违约（ARCH P-08）：\n" + string.Join('\n', problems) +
            "\n处置：在 TaskItem.Clone() 中补齐该字段；新增字段应同步 Clone，本守卫会自动覆盖。");
    }

    [Fact]
    public void 任务克隆_改克隆不影响原型()
    {
        var original = SampleTask();
        original.Result = "原产出";
        original.StageResults![0].OutputSummary = "原摘要";
        original.Workflow!.Stages[0].Name = "阶段一";

        var clone = original.Clone();
        clone.Result = "改过的产出";
        clone.DependsOn!.Add("t-9");
        clone.StageResults![0].OutputSummary = "改过的摘要";
        clone.Workflow!.Stages[0].Name = "改过的阶段";

        Assert.Equal("原产出", original.Result);
        Assert.Single(original.DependsOn!);
        Assert.Equal("原摘要", original.StageResults![0].OutputSummary);
        Assert.Equal("阶段一", original.Workflow!.Stages[0].Name);
    }

    [Fact]
    public void 任务克隆_序列化形状一致()
    {
        var original = SampleTask();

        var clone = original.Clone();

        AssertSameJson(original, clone);
        Assert.NotSame(original, clone);
    }

    [Fact]
    public void 任务克隆_可空字段保持可空语义()
    {
        var original = new TaskItem { Id = "t1", Instruction = "做事" }; // 其余扩展字段缺省为 null

        var clone = original.Clone();

        AssertSameJson(original, clone);
        Assert.Null(clone.Workflow);
        Assert.Null(clone.DependsOn);
        Assert.Null(clone.ProjectRef);
        Assert.Null(clone.TemplateVars);
        Assert.Null(clone.StageResults);
        Assert.Null(clone.Session);
        Assert.Null(clone.MaxLoops);
        Assert.Null(clone.BatchSize);
    }

    /// <summary>反射取全部可写实例属性——新增属性自动进入 ① 的比对，无需改测试。</summary>
    private static PropertyInfo[] WritableProperties() =>
        typeof(TaskItem)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>为全部可写属性赋非默认样本值（漏拷字段不会因"恰好都是默认值"而蒙混过关）。</summary>
    private static TaskItem SampleTask()
    {
        var task = new TaskItem();
        foreach (var p in WritableProperties())
        {
            p.SetValue(task, SampleValue(p.PropertyType, p.Name));
        }

        return task;
    }

    private static object? SampleValue(Type type, string propertyName) =>
        type == typeof(string) ? $"样本-{propertyName}" :
        type == typeof(int) ? 7 :
        type == typeof(int?) ? 7 :
        type == typeof(bool) ? true :
        type == typeof(double) ? 2.5d :
        type == typeof(decimal?) ? 1.5m :
        type == typeof(DateTimeOffset?) ? new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero) :
        type == typeof(TaskStatus) ? TaskStatus.NeedsAttention :
        type == typeof(List<string>) ? new List<string> { "t-1" } :
        type == typeof(List<StageResult>) ? new List<StageResult>
        {
            new() { StageId = "s1", Name = "阶段一", OutputSummary = "原摘要", Status = TaskStatus.Done }
        } :
        type == typeof(WorkflowSpec) ? new WorkflowSpec
        {
            Name = "工作流",
            Stages = new List<WorkflowStage> { new() { Id = "s1", Name = "阶段一", InstructionTemplate = "做事" } }
        } :
        type == typeof(ProjectRef) ? new ProjectRef { RepoPath = "repo", BaseBranch = "main" } :
        type == typeof(TemplateVars) ? new TemplateVars { TemplateId = "tpl", Values = new() { ["k"] = "v" } } :
        type == typeof(SessionHandle) ? new SessionHandle { AdapterKind = "codebuddy", SessionId = "sid" } :
        throw new InvalidOperationException(
            $"TaskItem 新增了本守卫未知的属性类型 {type.Name}（{propertyName}）：请在 SampleValue 中补样本值，并确认 Clone() 已拷贝该字段。");

    /// <summary>值 / 字符串按相等比对；聚合节点按 JSON 结构比对（记录值相等对集合按引用比较，JSON 才是用户可见契约）。</summary>
    private static bool SameValue(object? before, object? after)
    {
        if (before is null || after is null)
        {
            return before is null && after is null;
        }

        return before is string or ValueType
            ? before.Equals(after)
            : JsonDefaults.Serialize(before) == JsonDefaults.Serialize(after);
    }

    private static string Describe(object? value) => value?.ToString() ?? "<null>";
}
