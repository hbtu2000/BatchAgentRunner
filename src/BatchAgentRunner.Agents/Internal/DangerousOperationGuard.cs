using BatchAgentRunner.Core.Options;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>危险操作防护的判定结果。</summary>
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
    /// <summary>按白名单、黑名单依次判定提示文本，命中黑名单时回填命中的模式。</summary>
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

    /// <summary>判断提示文本是否命中危险黑名单。</summary>
    /// <param name="text">待判定的交互提示文本。</param>
    /// <param name="policy">自动应答策略，提供白名单与黑名单。</param>
    /// <param name="matchedPattern">命中的黑名单模式；未命中为 null。</param>
    public static bool IsDangerous(string? text, AutoAnswerPolicy policy, out string? matchedPattern) =>
        Evaluate(text, policy, out matchedPattern) == GuardDecision.Block;
}
