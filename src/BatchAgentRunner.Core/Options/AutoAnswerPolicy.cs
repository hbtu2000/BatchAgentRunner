using System.Text.Json.Serialization;

namespace BatchAgentRunner.Core.Options;

/// <summary>危险操作防护与自动应答策略（REQ-002a / REQ-002b）。</summary>
public sealed class AutoAnswerPolicy
{
    /// <summary>命中即拒绝自动批准并中断该任务。</summary>
    [JsonPropertyName("dangerous_blacklist")]
    public List<string> DangerousBlacklist { get; set; } = new();

    /// <summary>白名单内的确认按默认应答执行。</summary>
    [JsonPropertyName("allowed_whitelist")]
    public List<string> AllowedWhitelist { get; set; } = new();

    /// <summary>默认应答注入内容（检测到 stdin 等待时写入）。</summary>
    [JsonPropertyName("default_answer")]
    public string DefaultAnswer { get; set; } = "y";

    /// <summary>无输出增长超过该秒数即判定为 stdin 等待。</summary>
    [JsonPropertyName("stdin_idle_seconds")]
    public int StdinIdleSeconds { get; set; } = 30;

    /// <summary>
    /// 识别「选择型」交互提示的关键词。提示文本包含任一关键词且能提取出编号选项时，
    /// 自动按 <see cref="DefaultChoiceIndex"/> 选择对应选项，而不是注入 <see cref="DefaultAnswer"/>。
    /// </summary>
    [JsonPropertyName("choice_prompt_patterns")]
    public List<string> ChoicePromptPatterns { get; set; } = new();

    /// <summary>
    /// 匹配选项行的正则。必须包含捕获组：第 1 组为选项编号/键，第 2 组为选项文本。
    /// 默认匹配 "1. xxx" / "1、xxx" / "1) xxx" / "[1] xxx" 等常见形态。
    /// </summary>
    [JsonPropertyName("choice_option_pattern")]
    public string ChoiceOptionPattern { get; set; } =
        @"^\s*\[?(\d+)\]?[\.、,\)\]\s]+(.+)$";

    /// <summary>选择型交互默认选择的选项索引（0-based）。0 表示第一个选项。</summary>
    [JsonPropertyName("default_choice_index")]
    public int DefaultChoiceIndex { get; set; } = 0;

    public static AutoAnswerPolicy CreateDefault() => new()
    {
        DangerousBlacklist = new List<string>
        {
            "rm -rf", "rm -r /", "del /f", "format ", "mkfs",
            "git push --force", "git push -f", "git reset --hard", "git clean -fd",
            "删除文件", "删除目录", "格式化", "强制推送",
            "修改系统配置", "注册表", "registry", "shutdown", "重启系统"
        },
        AllowedWhitelist = new List<string>
        {
            "读取文件", "写入文件", "创建文件", "运行测试", "运行命令", "读取目录"
        },
        DefaultAnswer = "y",
        StdinIdleSeconds = 30,
        ChoicePromptPatterns = new List<string>
        {
            "选择", "请选择", "选项", "scheme", "select", "choose", "please choose"
        },
        ChoiceOptionPattern = @"^\s*\[?(\d+)\]?[\.、,\)\]\s]+(.+)$",
        DefaultChoiceIndex = 0
    };
}
