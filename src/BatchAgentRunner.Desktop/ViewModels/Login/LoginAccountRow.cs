using BatchAgentRunner.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>已登录账号行（账号池条目 + Vault 快照状态 + 一键操作）。</summary>
/// <remarks>
/// P-06：从 <see cref="LoginViewModel"/> 抽出为独立文件（分解前内联于 facade）。
/// 行命令经 owner（facade）路由到 <see cref="AccountPoolCoordinator"/> 的对应操作，
/// 公开构造签名与分解前逐字节一致（测试直接构造行对象）。
/// </remarks>
public sealed class LoginAccountRow : ObservableObject
{
    private readonly LoginViewModel _owner;
    private string _snapshotStatus;
    private bool _isConfirmingDelete;

    /// <summary>以账号配置、快照描述与 Live 标记初始化行及其命令。</summary>
    public LoginAccountRow(AccountConfig account, string snapshotStatus, bool isCurrentLive, LoginViewModel owner)
    {
        Account = account;
        _snapshotStatus = snapshotStatus;
        IsCurrentLive = isCurrentLive;
        _owner = owner;
        SwitchCommand = new AsyncRelayCommand(() => owner.SwitchToAsync(this));
        ReloginCommand = new AsyncRelayCommand(() => owner.ReLoginAsync(this));
        DeleteCommand = new AsyncRelayCommand(() => owner.DeleteAsync(this));
        CancelDeleteCommand = new RelayCommand(() => owner.CancelDelete(this));
    }

    /// <summary>账号配置实体（Id、显示名、凭据引用等）。</summary>
    public AccountConfig Account { get; }

    /// <summary>账号显示名；空白时退回账号 Id。</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Account.DisplayName) ? Account.Id : Account.DisplayName;

    /// <summary>账号唯一标识（来自账号配置）。</summary>
    public string Id => Account.Id;

    /// <summary>Vault 快照的可读描述，切换或重建后更新。</summary>
    public string SnapshotStatus
    {
        get => _snapshotStatus;
        set => SetProperty(ref _snapshotStatus, value);
    }

    /// <summary>是否 CLI 当前正在使用的身份（构造时确定）。</summary>
    public bool IsCurrentLive { get; }

    /// <summary>两段式删除确认：第一次点只亮确认按钮，第二次点才真正删除。</summary>
    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set
        {
            if (SetProperty(ref _isConfirmingDelete, value))
            {
                OnPropertyChanged(nameof(IsNotConfirmingDelete));
            }
        }
    }

    /// <summary>是否处于普通（非删除确认）状态，供按钮显隐绑定。</summary>
    public bool IsNotConfirmingDelete => !_isConfirmingDelete;

    /// <summary>一键切换：把该账号的 Vault 快照回填为 CLI 当前身份。</summary>
    public IAsyncRelayCommand SwitchCommand { get; }

    /// <summary>一键重登：对同一个账号重新走一遍登录流程（额度/过期场景）。</summary>
    public IAsyncRelayCommand ReloginCommand { get; }

    /// <summary>删除账号：清掉 Vault 备份快照并从账号池移除。</summary>
    public IAsyncRelayCommand DeleteCommand { get; }

    /// <summary>撤销删除确认（回到普通状态）。</summary>
    public IRelayCommand CancelDeleteCommand { get; }
}
