using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Helpers;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「安全与交互」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的
/// 自动应答域）：危险黑名单 / 允许白名单 / 默认应答 / 静默判定秒数 / 选择型默认索引。
/// 黑白名单经 <see cref="Helpers.MultiValueText"/> 与行文本互转（每行一条）。
/// </summary>
public sealed class SafetySectionViewModel : ViewModelBase
{
    private string _whitelistText = string.Empty;
    private string _blacklistText = string.Empty;
    private string _defaultAnswerText = "y";
    private int _silenceSeconds = 30;
    private int _choiceDefaultIndex;

    /// <summary>危险操作黑名单（命中即拒绝自动批准并终止任务，PRD 第 8 章）。</summary>
    public string BlacklistText
    {
        get => _blacklistText;
        set => SetProperty(ref _blacklistText, value);
    }

    /// <summary>允许自动批准的命令白名单（每行一条，空行忽略）。</summary>
    public string WhitelistText
    {
        get => _whitelistText;
        set => SetProperty(ref _whitelistText, value);
    }

    /// <summary>普通确认型交互注入 stdin 的默认内容。</summary>
    public string DefaultAnswerText
    {
        get => _defaultAnswerText;
        set => SetProperty(ref _defaultAnswerText, value);
    }

    /// <summary>静默多少秒判定为「在等确认」。</summary>
    public int SilenceSeconds
    {
        get => _silenceSeconds;
        set => SetProperty(ref _silenceSeconds, value);
    }

    /// <summary>选择型交互默认选择的选项索引（0-based）。</summary>
    public int ChoiceDefaultIndex
    {
        get => _choiceDefaultIndex;
        set => SetProperty(ref _choiceDefaultIndex, value);
    }

    /// <summary>从运行配置加载本域状态。</summary>
    public void LoadFrom(RunOptions o)
    {
        WhitelistText = MultiValueText.Join(o.AutoAnswer.AllowedWhitelist);
        BlacklistText = MultiValueText.Join(o.AutoAnswer.DangerousBlacklist);
        DefaultAnswerText = o.AutoAnswer.DefaultAnswer ?? "y";
        SilenceSeconds = o.AutoAnswer.StdinIdleSeconds;
        ChoiceDefaultIndex = o.AutoAnswer.DefaultChoiceIndex;
    }

    /// <summary>把本域状态写回运行配置（空白回落默认应答见 <see cref="ConfigFieldRules"/>）。</summary>
    public void ApplyTo(RunOptions o)
    {
        o.AutoAnswer.AllowedWhitelist = MultiValueText.Split(WhitelistText, '\n').ToList();
        o.AutoAnswer.DangerousBlacklist = MultiValueText.Split(BlacklistText, '\n').ToList();
        o.AutoAnswer.DefaultAnswer = ConfigFieldRules.OrFallback(DefaultAnswerText, "y");
        o.AutoAnswer.StdinIdleSeconds = ConfigFieldRules.AtLeastZero(SilenceSeconds);
        o.AutoAnswer.DefaultChoiceIndex = ConfigFieldRules.AtLeastZero(ChoiceDefaultIndex);
    }
}
