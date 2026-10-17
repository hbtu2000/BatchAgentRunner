using System.Collections.ObjectModel;
using System.IO;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Scheduling;
using BatchAgentRunner.Core.Serialization;
using BatchAgentRunner.Desktop.Services;
using BatchAgentRunner.Infrastructure.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 模板库页（二期 REQ-016 + 场景模板库 S1–S16）：选择模板 → 编辑变量 →
/// 展开预览 → 生成任务文件（JSON，产物与手写任务字节级等价）。
/// </summary>
public sealed class TemplateEditorViewModel : ViewModelBase
{
    private readonly FileWorkflowTemplateStore _store;
    private readonly TemplateExpander _expander = new();

    private ObservableCollection<TemplateRowViewModel> _templates = new();
    private TemplateRowViewModel? _selectedTemplate;
    private string _baseDir = string.Empty;
    private string _outputPath = string.Empty;
    private string _statusMessage = string.Empty;

    public TemplateEditorViewModel(EngineHost host)
    {
        _store = new FileWorkflowTemplateStore();
        _baseDir = host.Options.DefaultWorkspace ?? Directory.GetCurrentDirectory();
        var generatedDir = Path.Combine(host.ConfigDirectory, "generated");
        _outputPath = Path.Combine(generatedDir, "template-output.tasks.json");
        PreviewTasks = new ObservableCollection<PreviewTaskRowViewModel>();
        VarInputs = new ObservableCollection<TemplateVarInputViewModel>();

        LoadCommand = new AsyncRelayCommand(LoadAsync);
        PreviewCommand = new RelayCommand(Preview);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => _selectedTemplate is not null);
    }

    public IAsyncRelayCommand LoadCommand { get; }

    public IRelayCommand PreviewCommand { get; }

    public IAsyncRelayCommand GenerateCommand { get; }

    /// <summary>模板清单（内置 S1–S16 + 用户自定义）。</summary>
    public ObservableCollection<TemplateRowViewModel> Templates
    {
        get => _templates;
        private set => SetProperty(ref _templates, value);
    }

    public ObservableCollection<TemplateVarInputViewModel> VarInputs { get; }

    public ObservableCollection<PreviewTaskRowViewModel> PreviewTasks { get; }

    public TemplateRowViewModel? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
            {
                OnPropertyChanged(nameof(SelectedDescription));
                OnPropertyChanged(nameof(SelectedWorkflowText));
                OnPropertyChanged(nameof(SelectedChainText));
                LoadVarInputs();
                PreviewTasks.Clear();
                GenerateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedDescription => _selectedTemplate?.Spec.InstructionTemplate ?? string.Empty;

    public string SelectedWorkflowText => _selectedTemplate?.Spec.Workflow is { } wf
        ? $"工作流模板：{wf.Stages.Count} 个阶段"
        : "单指令模板";

    public string SelectedChainText => _selectedTemplate?.Spec.ChainDependency == true
        ? "链式依赖：开启（任务依次 depends_on 前一个）"
        : "链式依赖：关闭";

    /// <summary>变量取值基准目录（FileList / CsvColumn 相对路径的解析根）。</summary>
    public string BaseDir
    {
        get => _baseDir;
        set => SetProperty(ref _baseDir, value);
    }

    /// <summary>生成任务文件的输出路径（JSON 数组，可直接在高级配置中选用）。</summary>
    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>导航进入时刷新模板清单。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var specs = await _store.ListAsync().ConfigureAwait(true);
            Templates = new ObservableCollection<TemplateRowViewModel>(
                specs.Select(s => new TemplateRowViewModel(s)));
            StatusMessage = $"已加载 {Templates.Count} 个模板";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载模板失败：{ex.Message}";
        }
    }

    /// <summary>用当前变量取值展开模板（不落盘）。</summary>
    public void Preview()
    {
        PreviewTasks.Clear();
        if (_selectedTemplate is null)
        {
            return;
        }

        try
        {
            var tasks = _expander.Expand(BuildSpec(), _baseDir);
            foreach (var t in tasks)
            {
                PreviewTasks.Add(new PreviewTaskRowViewModel(t));
            }

            StatusMessage = $"展开得到 {tasks.Count} 条任务（预览未落盘）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"展开失败：{ex.Message}";
        }
    }

    private async Task GenerateAsync()
    {
        if (_selectedTemplate is null)
        {
            return;
        }

        try
        {
            var tasks = _expander.Expand(BuildSpec(), _baseDir);
            var dir = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(_outputPath, JsonDefaults.Serialize(tasks)).ConfigureAwait(true);
            StatusMessage = $"已生成 {tasks.Count} 条任务 → {_outputPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"生成失败：{ex.Message}";
        }
    }

    /// <summary>把界面变量输入写回模板副本（不改动仓库里的模板定义）。</summary>
    private TemplateSpec BuildSpec()
    {
        var spec = _selectedTemplate!.Spec.Clone();
        foreach (var input in VarInputs)
        {
            var def = spec.Vars.FirstOrDefault(v => string.Equals(v.Name, input.Name, StringComparison.Ordinal));
            if (def is null)
            {
                continue;
            }

            def.Default = input.Value;
            def.Values = input.ValuesText
                .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return spec;
    }

    private void LoadVarInputs()
    {
        VarInputs.Clear();
        if (_selectedTemplate is null)
        {
            return;
        }

        foreach (var def in _selectedTemplate.Spec.Vars)
        {
            VarInputs.Add(new TemplateVarInputViewModel(def));
        }
    }
}

/// <summary>模板清单行。</summary>
public sealed class TemplateRowViewModel : ObservableObject
{
    public TemplateRowViewModel(TemplateSpec spec) => Spec = spec;

    public TemplateSpec Spec { get; }

    public string Id => Spec.Id;

    public string Name => Spec.Name;

    public string VarSummary => Spec.Vars.Count == 0 ? "无变量" : $"{Spec.Vars.Count} 个变量";

    public bool IsWorkflow => Spec.Workflow is { Stages.Count: > 0 };

    public string KindText => IsWorkflow ? "工作流" : "单指令";
}

/// <summary>模板变量输入行（默认值 + 笛卡尔积多值）。</summary>
public sealed class TemplateVarInputViewModel : ObservableObject
{
    private string _value;
    private string _valuesText;

    public TemplateVarInputViewModel(TemplateVarDef def)
    {
        Def = def;
        _value = def.Default ?? string.Empty;
        _valuesText = def.Values is { Count: > 0 } ? string.Join(",", def.Values) : string.Empty;
    }

    public TemplateVarDef Def { get; }

    public string Name => Def.Name;

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

/// <summary>展开预览任务行。</summary>
public sealed class PreviewTaskRowViewModel : ObservableObject
{
    public PreviewTaskRowViewModel(TaskItem task) => Task = task;

    public TaskItem Task { get; }

    public string Id => Task.Id;

    public string Instruction => Task.Instruction;

    public string DependsText => Task.DependsOn is { Count: > 0 }
        ? "依赖：" + string.Join("、", Task.DependsOn)
        : string.Empty;

    public bool HasDepends => Task.DependsOn is { Count: > 0 };

    public string StageText => Task.IsWorkflow ? $"{Task.Workflow!.Stages.Count} 阶段" : "单指令";
}
