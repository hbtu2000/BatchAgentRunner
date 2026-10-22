using System.Collections.ObjectModel;
using System.Threading.Tasks;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Core.Options;
using BatchAgentRunner.Desktop.Models;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页「账号卡 / 模型池」子编辑器（P-04 分解产物，承载原 <c>ConfigViewModel</c> 的
/// 账号域 + 模型池域）：账号集合 / 共享候选模型池 / 模型池模式（单一数据源）/ 实时可用模型下拉 /
/// 模型拉取命令 / 模型池安全迁移。模型池模式切换经 <see cref="MigrateModelPool"/> 做单向安全迁移，
/// 避免误切丢数据或双份并存（同款思路也用于 <see cref="ConfigSaveCoordinator"/> 的落盘合并）。
/// </summary>
public sealed class ModelsAccountSectionViewModel : ViewModelBase
{
    private readonly IEngineHost _host;
    private readonly ConfigStatus _status;

    private ModelPoolMode _modelPoolMode = ModelPoolMode.Shared;
    private bool _isFetchingModels;

    /// <summary>初始化命令并注入引擎宿主与配置页共享状态槽。</summary>
    public ModelsAccountSectionViewModel(IEngineHost host, ConfigStatus status)
    {
        _host = host;
        _status = status;

        AddModelCommand = new RelayCommand(() =>
            Models.Add(new ModelEditor(new ModelCandidate { Name = "model-new" })));
        RemoveModelCommand = new RelayCommand<ModelEditor>(m => { if (m is not null) Models.Remove(m); });
        MoveModelUpCommand = new RelayCommand<ModelEditor>(m =>
        {
            if (m is null) return;
            var i = Models.IndexOf(m);
            if (i > 0) Models.Move(i, i - 1);
        });
        MoveModelDownCommand = new RelayCommand<ModelEditor>(m =>
        {
            if (m is null) return;
            var i = Models.IndexOf(m);
            if (i >= 0 && i < Models.Count - 1) Models.Move(i, i + 1);
        });

        FetchModelsCommand = new AsyncRelayCommand(FetchModelsAsync);
        FetchModelsForAccountCommand = new AsyncRelayCommand<AccountEditor>(FetchModelsForAccountAsync);
    }

    /// <summary>共享模式下的候选模型池（有序，可增删与上下移动）。</summary>
    public ObservableCollection<ModelEditor> Models { get; private set; } = new();

    /// <summary>账号卡集合，每项持有账号配置与自有模型池。</summary>
    public ObservableCollection<AccountEditor> Accounts { get; private set; } = new();

    /// <summary>共享模式下，全局模型行下拉可选用的「可用模型名」（由「拉取可用模型」填充；含已配置折扣的展示）。</summary>
    public ObservableCollection<ModelOption> AvailableModels { get; } = new();

    /// <summary>是否正在拉取共享模型清单（拉取期间禁用按钮）。</summary>
    public bool IsFetchingModels
    {
        get => _isFetchingModels;
        private set => SetProperty(ref _isFetchingModels, value);
    }

    /// <summary>点击后在共享模型池追加一行空白模型。</summary>
    public IRelayCommand AddModelCommand { get; }

    /// <summary>点击后从共享模型池移除指定模型行。</summary>
    public IRelayCommand<ModelEditor> RemoveModelCommand { get; }

    /// <summary>点击后将指定模型行上移一位（已在首位则不动）。</summary>
    public IRelayCommand<ModelEditor> MoveModelUpCommand { get; }

    /// <summary>点击后将指定模型行下移一位（已在末位则不动）。</summary>
    public IRelayCommand<ModelEditor> MoveModelDownCommand { get; }

    /// <summary>拉取当前执行工具支持的模型清单（共享模式用，填充全局下拉）。</summary>
    public IAsyncRelayCommand FetchModelsCommand { get; }

    /// <summary>拉取指定账号可用的模型清单（每账号独立模式用，填充该账号卡内下拉）。</summary>
    public IAsyncRelayCommand<AccountEditor> FetchModelsForAccountCommand { get; }

    // ---------------- 模型池模式（单一数据源） ----------------

    /// <summary>模型池维护方式（共享 / 每账号独立）。切换时自动安全迁移。</summary>
    public ModelPoolMode ModelPoolMode
    {
        get => _modelPoolMode;
        set
        {
            if (SetProperty(ref _modelPoolMode, value))
            {
                OnPropertyChanged(nameof(IsSharedMode));
                OnPropertyChanged(nameof(IsPerAccountMode));
                OnPropertyChanged(nameof(SharedMode));
                OnPropertyChanged(nameof(PerAccountMode));
                MigrateModelPool(value);
            }
        }
    }

    /// <summary>当前是否为共享模型池模式（供单选按钮与显隐绑定）。</summary>
    public bool IsSharedMode => _modelPoolMode == ModelPoolMode.Shared;
    /// <summary>当前是否为每账号独立模型池模式（供界面绑定）。</summary>
    public bool IsPerAccountMode => _modelPoolMode == ModelPoolMode.PerAccount;

    /// <summary>RadioButton 双向绑定用：选中 = 切到共享模式。</summary>
    public bool SharedMode
    {
        get => IsSharedMode;
        set { if (value) ModelPoolMode = ModelPoolMode.Shared; }
    }

    /// <summary>RadioButton 双向绑定用：选中 = 切到每账号独立模式。</summary>
    public bool PerAccountMode
    {
        get => IsPerAccountMode;
        set { if (value) ModelPoolMode = ModelPoolMode.PerAccount; }
    }

    /// <summary>模型池模式切换时做安全的单向迁移，避免误切导致数据丢失或双份并存。</summary>
    private void MigrateModelPool(ModelPoolMode mode)
    {
        if (mode == ModelPoolMode.PerAccount)
        {
            // 共享 → 独立：把当前共享列表复制到尚未配置自有池的账号，使其继承既有配置后再各自发散
            foreach (var acc in Accounts)
            {
                if (acc.Models.Count == 0)
                {
                    foreach (var m in Models)
                    {
                        acc.Models.Add(new ModelEditor(new ModelCandidate
                        {
                            Name = m.Name,
                            FreeTimeWindow = m.Model.FreeTimeWindow,
                            EstimatedUnitPrice = m.Model.EstimatedUnitPrice,
                            Discount = m.Model.Discount
                        }));
                    }
                }
            }
        }
        else
        {
            // 独立 → 共享：把各账号模型按名去重合并为一份共享列表，并清空账号自有池（单一数据源）
            var union = new System.Collections.Generic.List<ModelEditor>();
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var acc in Accounts)
            {
                foreach (var m in acc.Models)
                {
                    if (seen.Add(m.Name.Trim()))
                    {
                        union.Add(new ModelEditor(new ModelCandidate { Name = m.Name }));
                    }
                }
            }

            Models = new ObservableCollection<ModelEditor>(union);
            OnPropertyChanged(nameof(Models));
            foreach (var acc in Accounts)
            {
                acc.Models.Clear();
            }
        }
    }

    /// <summary>从运行配置加载本域状态（Reload / 取消时由 <see cref="ConfigSaveCoordinator.Load"/> 调用）。</summary>
    public void LoadFrom(RunOptions o)
    {
        Models = new ObservableCollection<ModelEditor>(o.ModelCandidates.Select(m => new ModelEditor(m)));
        Accounts = new ObservableCollection<AccountEditor>(o.Accounts.Select(a => new AccountEditor(a)));
        OnPropertyChanged(nameof(Models));
        OnPropertyChanged(nameof(Accounts));

        // 模型池模式：只要任一账号配置了自有池 → 视为「每账号独立」，否则「共享」
        ModelPoolMode = o.Accounts.Any(a => a.ModelCandidates is { Count: > 0 })
            ? ModelPoolMode.PerAccount
            : ModelPoolMode.Shared;
    }

    /// <summary>把模型池写回运行配置（保存时由 <see cref="ConfigSaveCoordinator.Apply"/> 调用）：单一数据源，只写当前模式对应一份。</summary>
    public void WriteModelPool(RunOptions o)
    {
        o.ModelCandidates.Clear();
        o.Accounts.Clear();

        if (ModelPoolMode == ModelPoolMode.Shared)
        {
            // 共享：全局共享列表；各账号自有池清空，使其回退到共享列表（避免双份并存）
            foreach (var editor in Models)
            {
                editor.Apply();
                o.ModelCandidates.Add(editor.Model);
            }

            foreach (var editor in Accounts)
            {
                editor.Apply();
                editor.Account.ModelCandidates = null;
                o.Accounts.Add(editor.Account);
            }
        }
        else
        {
            // 每账号独立：全局列表清空；各账号写入各自的自有候选模型池
            foreach (var editor in Accounts)
            {
                editor.Apply();
                o.Accounts.Add(editor.Account);
            }
        }
    }

    private async Task FetchModelsAsync()
    {
        if (IsFetchingModels)
        {
            return;
        }

        IsFetchingModels = true;
        try
        {
            var list = await _host.FetchModelsAsync(null).ConfigureAwait(false);

            // P0-6：ConfigureAwait(false) 之后已在后台线程，填充 ObservableCollection 必须回到 UI 线程
            UiThread.Post(() =>
            {
                ModelCatalogFiller.Fill(AvailableModels, list, Models);
                _status.Show(list.Count > 0
                    ? $"已拉取 {list.Count} 个可用模型（来自当前执行工具），可直接在下拉中选择。"
                    : "未能从 CLI 读取模型列表（可能当前 CLI 版本不支持），请直接输入模型名。");
            });
        }
        catch (Exception ex)
        {
            UiThread.Post(() => _status.Show($"拉取模型失败：{ex.Message}"));
        }
        finally
        {
            UiThread.Post(() => IsFetchingModels = false);
        }
    }

    private async Task FetchModelsForAccountAsync(AccountEditor? editor)
    {
        if (editor is null || editor.IsFetchingModels)
        {
            return;
        }

        editor.IsFetchingModels = true;
        try
        {
            var list = await _host.FetchModelsAsync(editor.Account).ConfigureAwait(false);

            UiThread.Post(() =>
            {
                ModelCatalogFiller.Fill(editor.AvailableModels, list, editor.Models);
                _status.Show(list.Count > 0
                    ? $"已拉取账号「{editor.Account.Label}」的 {list.Count} 个可用模型。"
                    : $"未能读取账号「{editor.Account.Label}」的模型列表，请直接输入模型名。");
            });
        }
        catch (Exception ex)
        {
            UiThread.Post(() => _status.Show($"拉取失败：{ex.Message}"));
        }
        finally
        {
            UiThread.Post(() => editor.IsFetchingModels = false);
        }
    }
}
