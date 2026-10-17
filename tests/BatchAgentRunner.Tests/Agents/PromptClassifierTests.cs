using BatchAgentRunner.Agents.Internal;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Tests.Agents;

public class PromptClassifierTests
{
    private static AutoAnswerPolicy PolicyWithChoicePatterns(params string[] patterns) => new()
    {
        DefaultAnswer = "y",
        ChoicePromptPatterns = patterns.ToList(),
        ChoiceOptionPattern = @"^\s*\[?(\d+)\]?[\.、,\)\]\s]+(.+)$",
        DefaultChoiceIndex = 0
    };

    [Fact]
    public void Classify_空文本_走默认确认应答()
    {
        var policy = PolicyWithChoicePatterns("选择");

        var result = PromptClassifier.Classify(null, policy);

        Assert.Equal(PromptKind.Confirm, result.Kind);
        Assert.Equal("y", result.Answer);
    }

    [Fact]
    public void Classify_普通确认_无选择关键词_走默认应答()
    {
        var policy = PolicyWithChoicePatterns("选择");

        var result = PromptClassifier.Classify("是否继续? [Y/n]", policy);

        Assert.Equal(PromptKind.Confirm, result.Kind);
        Assert.Equal("y", result.Answer);
    }

    [Theory]
    [InlineData("请选择：\n1. 方案 A\n2. 方案 B", "1", "方案 A")]
    [InlineData("请选择以下选项：\n1、选项甲\n2、选项乙", "1", "选项甲")]
    [InlineData("select one:\n1) apple\n2) banana", "1", "apple")]
    [InlineData("choose:\n[1] red\n[2] green", "1", "red")]
    public void Classify_选择型提示_自动选第一个选项(string prompt, string expectedAnswer, string expectedLabel)
    {
        var policy = PolicyWithChoicePatterns("选择", "select", "choose");

        var result = PromptClassifier.Classify(prompt, policy);

        Assert.Equal(PromptKind.Choice, result.Kind);
        Assert.Equal(expectedAnswer, result.Answer);
        Assert.NotNull(result.Choices);
        Assert.True(result.Choices!.Count >= 2);
        Assert.Equal(expectedLabel, result.Choices[result.SelectedIndex]);
        Assert.Contains(expectedLabel, result.Description);
    }

    [Fact]
    public void Classify_选择关键词存在但无编号选项_降级为确认应答()
    {
        var policy = PolicyWithChoicePatterns("选择");

        var result = PromptClassifier.Classify("请选择：是 / 否", policy);

        Assert.Equal(PromptKind.Confirm, result.Kind);
        Assert.Equal("y", result.Answer);
    }

    [Fact]
    public void Classify_自定义默认索引_选择对应选项()
    {
        var policy = new AutoAnswerPolicy
        {
            DefaultAnswer = "y",
            ChoicePromptPatterns = new List<string> { "选择" },
            ChoiceOptionPattern = @"^\s*\[?(\d+)\]?[\.、,\)\]\s]+(.+)$",
            DefaultChoiceIndex = 1
        };

        var result = PromptClassifier.Classify("请选择：\n1. A\n2. B\n3. C", policy);

        Assert.Equal(PromptKind.Choice, result.Kind);
        Assert.Equal("2", result.Answer);
        Assert.Equal(1, result.SelectedIndex);
        Assert.Equal("B", result.Choices![result.SelectedIndex]);
    }

    [Fact]
    public void Classify_默认索引越界_自动钳位到最后一个选项()
    {
        var policy = new AutoAnswerPolicy
        {
            DefaultAnswer = "y",
            ChoicePromptPatterns = new List<string> { "选择" },
            ChoiceOptionPattern = @"^\s*\[?(\d+)\]?[\.、,\)\]\s]+(.+)$",
            DefaultChoiceIndex = 100
        };

        var result = PromptClassifier.Classify("请选择：\n1. A\n2. B", policy);

        Assert.Equal(PromptKind.Choice, result.Kind);
        Assert.Equal("2", result.Answer);
        Assert.Equal(1, result.SelectedIndex);
    }

    [Fact]
    public void Classify_选项行带Markdown加粗_仍能提取()
    {
        var policy = PolicyWithChoicePatterns("选择");
        var prompt = "请选择：\n1. **重构目标**——全面评估\n2. **约束边界**——能否改动 API";

        var result = PromptClassifier.Classify(prompt, policy);

        Assert.Equal(PromptKind.Choice, result.Kind);
        Assert.Equal("1", result.Answer);
        Assert.Contains("重构目标", result.Choices![0]);
    }
}
