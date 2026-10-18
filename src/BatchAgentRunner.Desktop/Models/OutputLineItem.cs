namespace BatchAgentRunner.Desktop.Models;

/// <summary>
/// 实时输出流单行（对齐 MOCKUP-执行监控-重设计-v1.html：时间 + 彩色标签胶囊 + 消息）。
/// 标签按语义着色（工具调用=主色 / 完成=成功 / 切换·额度·提示=警告 / 错误=错误），双主题自动切换。
/// </summary>
public enum OutputTagKind
{
    /// <summary>原始输出 / 通用行（中性灰胶囊）。</summary>
    Plain,

    /// <summary>工具调用（Bash / Read / Edit / Grep …），主色胶囊。</summary>
    Tool,

    /// <summary>完成 / 成功确认，成功色胶囊，消息去强调（dim）。</summary>
    Ok,

    /// <summary>普通提示（warning 级事件），警告色胶囊。</summary>
    Warn,

    /// <summary>错误 / 失败，错误色胶囊。</summary>
    Err,

    /// <summary>模型 / 账号切换，警告色胶囊（与 mockup 一致用 amber）。</summary>
    Switch,

    /// <summary>额度 / 冷却 / 免费时段跳过，警告色胶囊（与 mockup 一致用 amber）。</summary>
    Quota
}

/// <summary>实时输出流单行视图模型（仅承载展示数据，无 UI 依赖）。</summary>
public sealed class OutputLineItem
{
    public OutputLineItem(DateTimeOffset at, OutputTagKind kind, string tagText, string message, bool dim = false)
    {
        At = at;
        Kind = kind;
        TagText = tagText;
        Message = message;
        Dim = dim;
    }

    public DateTimeOffset At { get; }

    public OutputTagKind Kind { get; }

    /// <summary>标签胶囊文字（如「工具调用」「完成」「切换」「额度」）。</summary>
    public string TagText { get; }

    /// <summary>消息正文。</summary>
    public string Message { get; }

    /// <summary>是否去强调（完成确认等次要行，颜色降为次级）。</summary>
    public bool Dim { get; }

    public string TimeText => At.ToString("HH:mm:ss");
}
