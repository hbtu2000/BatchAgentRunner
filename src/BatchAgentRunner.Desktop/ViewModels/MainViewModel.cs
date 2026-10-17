using Avalonia;
using Avalonia.Styling;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>主窗口：左侧导航（配置 / 监控 / 报告）+ 顶部批级状态条。</summary>
public sealed class MainViewModel : ViewModelBase
{
    private ViewModelBase _currentPage;
    private string _activePage = "monitor";
    private bool _isDark;

    public MainViewModel()
    {
        Host = new EngineHost();
        Host.InitializeAsync().GetAwaiter().GetResult();

        Config = new ConfigViewModel(Host);
        Monitor = new MonitorViewModel(Host);
        Report = new ReportViewModel(Host);
        Wizard = new SetupWizardViewModel(Host);
        Login = new LoginViewModel(Host);
        Diff = new DiffViewModel(Host);
        Templates = new TemplateEditorViewModel(Host);

        // 把「账号登录 / 配置向导 / 高级配置」三个配置入口合并为一个配置中心
        ConfigHub = new ConfigHubViewModel(Login, Wizard, Config);

        _currentPage = Monitor;
        _isDark = Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;

        NavigateCommand = new RelayCommand<string>(Navigate);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        Host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EngineHost.Status))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(Status));
            }
        };
    }

    public EngineHost Host { get; }

    public ConfigViewModel Config { get; }

    public MonitorViewModel Monitor { get; }

    public ReportViewModel Report { get; }

    public SetupWizardViewModel Wizard { get; }

    /// <summary>傻瓜式登录页：一键登录 / 一键切换账号。</summary>
    public LoginViewModel Login { get; }

    /// <summary>配置中心：合并「账号登录 / 配置向导 / 高级配置」三个配置入口。</summary>
    public ConfigHubViewModel ConfigHub { get; }

    /// <summary>变更概览（二期 diff 视图，REQ-017）。</summary>
    public DiffViewModel Diff { get; }

    /// <summary>模板库（二期 REQ-016 模板展开与任务生成）。</summary>
    public TemplateEditorViewModel Templates { get; }

    public ViewModelBase CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string ActivePage
    {
        get => _activePage;
        private set => SetProperty(ref _activePage, value);
    }

    public IRelayCommand<string> NavigateCommand { get; }

    public string StatusText => Host.Status switch
    {
        BatchAgentRunner.Core.Domain.BatchStatus.Idle => "空闲",
        BatchAgentRunner.Core.Domain.BatchStatus.Running => "运行中",
        BatchAgentRunner.Core.Domain.BatchStatus.Waiting => "等待恢复",
        BatchAgentRunner.Core.Domain.BatchStatus.Blocked => "已阻塞",
        BatchAgentRunner.Core.Domain.BatchStatus.Terminated => "已终止",
        BatchAgentRunner.Core.Domain.BatchStatus.Completed => "已完成",
        _ => Host.Status.ToString()
    };

    /// <summary>批级状态枚举（供 StatusDotHelper.BatchStatusText 附加属性按状态着色，双主题）。</summary>
    public BatchStatus Status => Host.Status;

    /// <summary>当前主题名（浅色 / 深色），供框架按钮显示。</summary>
    public string Theme => _isDark ? "深色" : "浅色";

    public IRelayCommand ToggleThemeCommand { get; }

    public string ConfigDirectory => Host.ConfigDirectory;

    private void Navigate(string? page)
    {
        switch (page)
        {
            case "confighub":
                ConfigHub.HubNavigate(ConfigHub.ActiveSubview); // 进入时刷新当前激活的子视图
                CurrentPage = ConfigHub;
                ActivePage = "confighub";
                break;
            case "report":
                Report.Refresh();
                CurrentPage = Report;
                ActivePage = "report";
                break;
            case "diff":
                Diff.Refresh();
                CurrentPage = Diff;
                ActivePage = "diff";
                break;
            case "templates":
                _ = Templates.LoadAsync();
                CurrentPage = Templates;
                ActivePage = "templates";
                break;
            default:
                CurrentPage = Monitor;
                ActivePage = "monitor";
                break;
        }
    }

    private void ToggleTheme()
    {
        _isDark = !_isDark;
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = _isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        OnPropertyChanged(nameof(Theme));
    }
}
