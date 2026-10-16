using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 配置中心：把原先分散在侧边栏的三个配置入口
/// （账号登录 / 配置向导 / 高级配置）合并到一处，通过顶部标签切换子视图。
/// </summary>
public sealed class ConfigHubViewModel : ViewModelBase
{
    private readonly LoginViewModel _login;
    private readonly SetupWizardViewModel _wizard;
    private readonly ConfigViewModel _config;

    private ViewModelBase _currentSubview;
    private string _activeSubview = "login";

    public ConfigHubViewModel(LoginViewModel login, SetupWizardViewModel wizard, ConfigViewModel config)
    {
        _login = login;
        _wizard = wizard;
        _config = config;

        HubNavigateCommand = new RelayCommand<string>(HubNavigate);

        // 默认进入最易用的「账号登录（一键，最简单）」
        _currentSubview = _login;
        _login.Refresh();
    }

    public LoginViewModel Login => _login;

    public SetupWizardViewModel Wizard => _wizard;

    public ConfigViewModel Config => _config;

    public ViewModelBase CurrentSubview
    {
        get => _currentSubview;
        private set => SetProperty(ref _currentSubview, value);
    }

    /// <summary>当前激活的子视图 key：login / wizard / config。</summary>
    public string ActiveSubview
    {
        get => _activeSubview;
        private set => SetProperty(ref _activeSubview, value);
    }

    public IRelayCommand<string> HubNavigateCommand { get; }

    /// <summary>在配置中心内部切换子视图；进入时刷新对应数据。</summary>
    public void HubNavigate(string? sub)
    {
        switch (sub)
        {
            case "wizard":
                CurrentSubview = _wizard;
                ActiveSubview = "wizard";
                break;
            case "config":
                _config.Reload(); // 进入高级配置前刷新为最新值
                CurrentSubview = _config;
                ActiveSubview = "config";
                break;
            default:
                _login.Refresh();
                CurrentSubview = _login;
                ActiveSubview = "login";
                break;
        }
    }
}
