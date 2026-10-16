using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Agents.Internal;

public enum GuardDecision
{
    /// <summary>白名单内：按默认应答自动执行。</summary>
    AutoAllow,

    /// <summary>未命中任何规则：按默认应答自动执行并记日志。</summary>
    AutoAnswerDefault,

    /// <summary>命中危险黑名单：绝不自动批准，中断该任务（REQ-002b）。</summary>
    Block
}

/// <summary>危险操作防护（REQ-002b）。无人值守下自动批准可能把单次误操作放大为整批破坏。</summary>
public static class DangerousOperationGuard
{
    public static GuardDecision Evaluate(string? text, AutoAnswerPolicy policy, out string? matchedPattern)
    {
        matchedPattern = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return GuardDecision.AutoAnswerDefault;
        }

        // 白名单优先（用户显式允许的确认类型）
        foreach (var pattern in policy.AllowedWhitelist)
        {
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return GuardDecision.AutoAllow;
            }
        }

        foreach (var pattern in policy.DangerousBlacklist)
        {
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                matchedPattern = pattern;
                return GuardDecision.Block;
            }
        }

        return GuardDecision.AutoAnswerDefault;
    }

    public static bool IsDangerous(string? text, AutoAnswerPolicy policy, out string? matchedPattern) =>
        Evaluate(text, policy, out matchedPattern) == GuardDecision.Block;
}
