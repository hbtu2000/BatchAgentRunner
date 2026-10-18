using BatchAgentRunner.Agents.Internal;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Tests.Agents;

/// <summary>
/// P1-15 高危盲区：<see cref="CliCommandBuilder"/> 是 CLI 参数的唯一拼装点（注入面），
/// 其占位符替换与引号转义必须经断言锁住，否则参数注入/转义错误会直达真实 CLI。
/// </summary>
public class CliCommandBuilderTests
{
    private static AgentRequest MakeRequest(ExpertRef? expert = null, string? workingDirectory = null) =>
        new(
            TaskId: "t1",
            Instruction: "请实现\"登录\"模块",
            Account: new AccountConfig { Id = "acc-1" },
            Model: new ModelCandidate { Name = "gpt-4" },
            Expert: expert ?? ExpertRef.Default(),
            WorkingDirectory: workingDirectory ?? @"C:\work\proj",
            Timeout: TimeSpan.FromMinutes(5),
            AutoAnswer: new AutoAnswerPolicy());

    [Fact]
    public void Build_空模板_使用默认模板_引号包裹指令与模型()
    {
        var cmd = CliCommandBuilder.Build(null, MakeRequest());

        Assert.Contains("--print \"请实现\\\"登录\\\"模块\"", cmd);
        Assert.Contains("--model gpt-4", cmd);
    }

    [Fact]
    public void Build_额外参数_append到模板末尾()
    {
        var cmd = CliCommandBuilder.Build(null, MakeRequest(), extra: "--verbose");

        Assert.Contains("--verbose", cmd);
        Assert.EndsWith("--verbose", cmd.Trim());
    }

    [Fact]
    public void Build_专家未指定_注入点为空字符串()
    {
        // 模板显式包含 {expert}：未指定专家时必须输出空串，而非 "默认智能体" 之类文本（否则破坏 CLI 语法）
        const string template = "run {prompt} --model {model} --expert {expert}";
        var cmd = CliCommandBuilder.Build(template, MakeRequest());

        Assert.Contains("--expert ", cmd);
        Assert.DoesNotContain("--expert 默认", cmd);
        Assert.DoesNotContain("默认智能体", cmd);
    }

    [Fact]
    public void Build_专家已指定_注入专家id()
    {
        const string template = "run {prompt} --expert {expert}";
        var request = MakeRequest(expert: new ExpertRef("sec-reviewer"));
        var cmd = CliCommandBuilder.Build(template, request);

        Assert.Contains("--expert sec-reviewer", cmd);
    }

    [Fact]
    public void Build_工作空间_始终引号包裹()
    {
        const string template = "run {prompt} --ws {workspace}";
        var request = MakeRequest(workingDirectory: @"C:\path with spaces\proj");
        var cmd = CliCommandBuilder.Build(template, request);

        Assert.Contains("--ws \"C:\\path with spaces\\proj\"", cmd);
    }

    [Fact]
    public void Quote_内部双引号被转义()
    {
        Assert.Equal("\"a\\\"b\"", CliCommandBuilder.Quote("a\"b"));
        Assert.Equal("\"plain\"", CliCommandBuilder.Quote("plain"));
    }

    [Fact]
    public void CodeBuddyTemplate_含stream_json与partial_messages()
    {
        var cmd = CliCommandBuilder.Build(CliCommandBuilder.CodeBuddyTemplate, MakeRequest());

        Assert.Contains("-p ", cmd);
        Assert.Contains("--output-format stream-json", cmd);
        Assert.Contains("--include-partial-messages", cmd);
    }

    [Fact]
    public void WorkBuddyTemplate_含stream_json()
    {
        var cmd = CliCommandBuilder.Build(CliCommandBuilder.WorkBuddyTemplate, MakeRequest());

        Assert.Contains("--output-format stream-json", cmd);
        Assert.Contains("--include-partial-messages", cmd);
    }
}
