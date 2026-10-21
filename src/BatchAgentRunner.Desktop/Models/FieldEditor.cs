using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.Models;

/// <summary>
/// 可校验编辑字段（P2-15 偿还）：把「领域字段 ↔ 编辑文本 → 解析回写」三段式收敛为一处——
/// 原 <see cref="ModelEditor"/> / <see cref="FallbackModelEditor"/> / <see cref="NotificationRuleEditor"/>
/// 各自手写「字符串属性 + <c>TryParse</c> + <c>Apply()</c>」，且解析失败一律静默置 null / 静默回落默认值，
/// 用户无法得知输入未被采纳。本类型使解析失败显式可见（<see cref="HasError"/> / <see cref="Error"/>），
/// 并由编辑器在 <c>Apply()</c> 对错误态保留原值而非静默覆盖。
/// </summary>
/// <remarks>
/// 语义约定：空文本 = 「未配置」（合法，<see cref="Value"/> 为 <c>default</c>）；
/// 非空但无法解析 = 错误态（<see cref="HasError"/>，界面显示 <see cref="Error"/>）。
/// </remarks>
public sealed class FieldEditor<TValue> : ObservableObject
{
    private readonly Func<string, (bool Ok, TValue? Value)> _tryParse;
    private string _text;

    public FieldEditor(string displayName, string text, Func<string, (bool Ok, TValue? Value)> tryParse)
    {
        DisplayName = displayName;
        _text = text;
        _tryParse = tryParse;
    }

    /// <summary>字段人话名（校验反馈文案用）。</summary>
    public string DisplayName { get; }

    /// <summary>编辑文本（与输入框双向绑定的入口；每次变更联动刷新 <see cref="HasError"/> / <see cref="Error"/>）。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(Error));
            }
        }
    }

    /// <summary>非空但无法解析（界面据此显示校验反馈；空文本不算错误）。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(_text) && !_tryParse(_text).Ok;

    /// <summary>校验反馈文案（无错误时为 null，可直接绑 ToolTip / 错误文本）。</summary>
    public string? Error => HasError ? $"{DisplayName}格式无效" : null;

    /// <summary>解析值：空文本或解析失败均返回 <c>default</c>（调用方 <c>Apply()</c> 前应先判 <see cref="HasError"/>）。</summary>
    public TValue? Value => _tryParse(_text) is { Ok: true } parsed ? parsed.Value : default;
}

/// <summary>编辑字段的通用解析器（CA1000：静态成员不得驻留泛型类型，故独立非泛型持有）。</summary>
public static class FieldParsers
{
    /// <summary>可空十进制解析（留空 = 未配置；「abc」= 错误态）。</summary>
    public static (bool Ok, decimal? Value) DecimalOrNull(string text) =>
        decimal.TryParse(text, out var value) ? (true, value) : (false, (decimal?)null);

    /// <summary>可空整数解析（留空 = 未配置；「abc」= 错误态）。</summary>
    public static (bool Ok, int? Value) IntOrNull(string text) =>
        int.TryParse(text, out var value) ? (true, value) : (false, (int?)null);
}
