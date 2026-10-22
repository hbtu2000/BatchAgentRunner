using BatchAgentRunner.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>模板变量输入行（默认值 + 笛卡尔积多值）。</summary>
public sealed class TemplateVarInputViewModel : ObservableObject
{
    private string _value;
    private string _valuesText;

    /// <summary>以变量定义初始化输入行，并预填默认值与多值。</summary>
    public TemplateVarInputViewModel(TemplateVarDef def)
    {
        Def = def;
        _value = def.Default ?? string.Empty;
        _valuesText = def.Values is { Count: > 0 } ? string.Join(",", def.Values) : string.Empty;
    }

    /// <summary>变量定义实体。</summary>
    public TemplateVarDef Def { get; }

    /// <summary>变量名。</summary>
    public string Name => Def.Name;

    /// <summary>变量取值来源的可读文案（字面量/文件清单/CSV 列）。</summary>
    public string SourceText => Def.Source switch
    {
        VarSource.Literal => "字面量",
        VarSource.FileList => "文件清单",
        VarSource.CsvColumn => "CSV 列",
        _ => Def.Source.ToString()
    };

    /// <summary>变量取值（写入 def.Default）。</summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>多值（逗号分隔，Source=Literal 时做笛卡尔积；写入 def.Values）。</summary>
    public string ValuesText
    {
        get => _valuesText;
        set => SetProperty(ref _valuesText, value);
    }
}
