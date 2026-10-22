using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 配置中心：把原先分散在侧边栏的三个配置入口
/// （账号登录 / 配置向导 / 高级配置）合并到一处。新版改为统一的 rail 区段导航 +
/// 顶部「准备度」进度条 + 内嵌 ConfigView（按区段显隐）+ 底部单一保存条。
/// 登录页内嵌于「账号与凭据」区段，配置向导作为「快速开始」区段与准备度引导。
/// </summary>
public sealed class ConfigHubViewModel : ViewModelBase
{
    private readonly LoginViewModel _login;
    private readonly SetupWizardViewModel _wizard;
    private readonly ConfigViewModel _config;
    private string _activeSubview = "config";

    /// <summary>注入登录、向导与配置三个子视图，并把前两者挂到配置页统一驱动。</summary>
    public ConfigHubViewModel(LoginViewModel login, SetupWizardViewModel wizard, ConfigViewModel config)
    {
        _login = login;
        _wizard = wizard;
        _config = config;

        // 把登录 / 向导注入配置页：账号区段内嵌登录、快速开始区段可重跑向导
        _config.Login = _login;
        _config.Wizard = _wizard;
        _config.RefreshReadiness();
    }

    /// <summary>内嵌在「账号与凭据」区段中的登录视图模型。</summary>
    public LoginViewModel Login => _login;

    /// <summary>「快速开始」区段使用的配置向导视图模型。</summary>
    public SetupWizardViewModel Wizard => _wizard;

    /// <summary>内嵌的高级配置视图模型，按当前区段显隐渲染。</summary>
    public ConfigViewModel Config => _config;

    /// <summary>当前激活的子视图 key（保留以兼容主窗口导航调用）。</summary>
    public string ActiveSubview
    {
        get => _activeSubview;
        private set => SetProperty(ref _activeSubview, value);
    }

    /// <summary>进入配置中心时刷新配置与准备度。</summary>
    public void HubNavigate(string? sub)
    {
        _config.Reload();
        _config.RefreshReadiness();
        ActiveSubview = "config";
    }
}
