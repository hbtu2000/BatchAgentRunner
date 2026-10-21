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
    public void Build_指令含换行_折叠为单行且后续参数完整()
    {
        // 2026-10-22 事故回归：指令来自 xlsx 单元格时可能自带 CR/LF。
        // CLI 是 .cmd shim 时命令行经 cmd.exe 转交，而 cmd.exe 把换行当命令分隔符 →
        // 换行后的 --model / --output-format / --include-partial-messages / --port 会被整体丢弃，
        // CLI 退回交互模式、不产出 stream-json，实时输出流一片空白。
        var request = MakeRequest() with { Instruction = "第一行\r\n  第二行\n第三行" };
        var cmd = CliCommandBuilder.Build(CliCommandBuilder.CodeBuddyTemplate, request, extra: "--flag-tail");

        Assert.DoesNotContain("\r", cmd);
        Assert.DoesNotContain("\n", cmd);
        Assert.Contains("第一行 第二行 第三行", cmd);
        Assert.Contains("--output-format stream-json", cmd);
        Assert.Contains("--include-partial-messages", cmd);
        Assert.Contains("--model gpt-4", cmd);
        Assert.EndsWith("--flag-tail", cmd.Trim());
    }

    [Fact]
    public void NormalizeToSingleLine_控制字符与连续空白折叠为单空格_并去首尾空白()
    {
        Assert.Equal(string.Empty, CliCommandBuilder.NormalizeToSingleLine(null));
        Assert.Equal(string.Empty, CliCommandBuilder.NormalizeToSingleLine("  \r\n\t "));
        Assert.Equal("a b c", CliCommandBuilder.NormalizeToSingleLine("  a\r\n\tb    c  "));
        // 中文引号等非空白字符必须逐字保留，不得影响指令语义
        Assert.Equal("请根据“方案.md”完成 Phase R8 。", CliCommandBuilder.NormalizeToSingleLine("请根据“方案.md”完成\r\n Phase R8 \n。"));
    }

    [Fact]
    public void WorkBuddyTemplate_含stream_json()
    {
        var cmd = CliCommandBuilder.Build(CliCommandBuilder.WorkBuddyTemplate, MakeRequest());

        Assert.Contains("--output-format stream-json", cmd);
        Assert.Contains("--include-partial-messages", cmd);
    }
}
