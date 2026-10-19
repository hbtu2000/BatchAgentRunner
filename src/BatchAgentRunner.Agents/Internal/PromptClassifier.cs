using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>交互提示类型。</summary>
public enum PromptKind
{
    /// <summary>普通确认型提示，注入 <see cref="AutoAnswerPolicy.DefaultAnswer"/>。</summary>
    Confirm,

    /// <summary>选择型提示，按默认索引选择对应选项。</summary>
    Choice
}

/// <summary>交互提示分类结果。</summary>
public sealed record PromptClassification(
    PromptKind Kind,
    string Answer,
    string? MatchedPrompt = null,
    IReadOnlyList<string>? Choices = null,
    int SelectedIndex = -1)
{
    /// <summary>用于日志的简短描述。</summary>
    public string Description => Kind switch
    {
        PromptKind.Choice when Choices is { Count: > 0 }
            => $"选择「{Choices[SelectedIndex]}」（索引 {SelectedIndex}）",
        _ => $"默认应答「{Answer}」"
    };
}

/// <summary>
/// 交互提示分类器。在 <see cref="DangerousOperationGuard"/> 放行后，判断当前 stdin 等待
/// 是「确认型」还是「选择型」，并给出应注入的应答内容（PRD REQ-002a）。
/// </summary>
public static class PromptClassifier
{
    /// <summary>
    /// 分析提示文本，返回应自动回复的内容。
    /// 安全判断（危险黑名单/白名单）由调用方先行处理，本方法只负责区分确认/选择。
    /// </summary>
    public static PromptClassification Classify(string? prompt, AutoAnswerPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new PromptClassification(PromptKind.Confirm, policy.DefaultAnswer);
        }

        var text = prompt;
        var isChoicePrompt = policy.ChoicePromptPatterns
            .Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

        if (isChoicePrompt && TryExtractChoices(text, policy.ChoiceOptionPattern, out var choices) && choices.Count > 0)
        {
            var index = Math.Clamp(policy.DefaultChoiceIndex, 0, choices.Count - 1);
            return new PromptClassification(
                PromptKind.Choice,
                choices[index].Key,
                text.Trim(),
                choices.Select(c => c.Label).ToList(),
                index);
        }

        return new PromptClassification(PromptKind.Confirm, policy.DefaultAnswer);
    }

    private sealed record ChoiceOption(string Key, string Label);

    // RegexOptions.Compiled 会把正则编译进一个「动态程序集」，且该程序集在进程/AppDomain 卸载前永不回收。
    // 若每次调用都 new，长时间 CLI 会话里 Classify 被反复调用会累积成百上千个动态程序集，撑爆加载器堆，
    // 最终在程序集卸载/进程退出时触发 Internal CLR error（0x80131506）一类致命崩溃。
    // 因此按 pattern 缓存编译后的 Regex 实例，复用同一动态程序集。
    private static readonly ConcurrentDictionary<string, Regex> ChoiceRegexCache = new();

    private static bool TryExtractChoices(string text, string pattern, out IReadOnlyList<ChoiceOption> choices)
    {
        choices = Array.Empty<ChoiceOption>();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            var regex = ChoiceRegexCache
                .GetOrAdd(pattern, p => new Regex(p, RegexOptions.Multiline | RegexOptions.Compiled));
            var list = new List<ChoiceOption>();
            foreach (Match m in regex.Matches(text))
            {
                if (!m.Success || m.Groups.Count < 3)
                {
                    continue;
                }

                var key = m.Groups[1].Value.Trim();
                var label = m.Groups[2].Value.Trim();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                list.Add(new ChoiceOption(key, label));
            }

            choices = list;
            return list.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
