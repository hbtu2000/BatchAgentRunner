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
    private ViewModelBase? _currentPage;
    private string _activePage = "monitor";
    private bool _isDark;

    public MainViewModel()
    {
        Host = new EngineHost();
    }

    /// <summary>
    /// 异步初始化门（P1-6）：构造器只做廉价装配，宿主初始化（加载配置 / 建引擎）移到此处
    /// <c>await</c>，避免原构造器中 <c>Host.InitializeAsync().GetAwaiter().GetResult()</c> 的同步阻塞
    /// （UI 线程死锁 / 线程池饥饿）。必须在窗口可见前完成；窗口随后按 <see cref="IEngineHost"/> 的
    /// <c>PropertyChanged</c> 响应式刷新，无需轮询。
    /// </summary>
    /// <remarks>宿主初始化失败走兜底（与 CLR 崩溃兜底 B3/B4 同类）：记录后仍然装配页面，窗口进入可用态，用户可到「配置」页重试，不使进程崩溃。</remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Host.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MainViewModel] 宿主初始化失败：{ex}");
        }

        BuildPages();
    }

    /// <summary>装配各页面 VM、接线导航/主题命令、订阅宿主状态广播（原构造器体，沉入初始化门）。</summary>
    private void BuildPages()
    {
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
            if (e.PropertyName == nameof(IEngineHost.Status))
            {
                // 宿主状态在后台线程变更（Host.InitializeAsync 经 ConfigureAwait(false)），统一收敛到 UI 线程。
                UiThread.Post(() =>
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(Status));
                });
            }
        };
    }

    public IEngineHost Host { get; }

    public ConfigViewModel? Config { get; private set; }

    public MonitorViewModel? Monitor { get; private set; }

    public ReportViewModel? Report { get; private set; }

    public SetupWizardViewModel? Wizard { get; private set; }

    /// <summary>傻瓜式登录页：一键登录 / 一键切换账号。</summary>
    public LoginViewModel? Login { get; private set; }

    /// <summary>配置中心：合并「账号登录 / 配置向导 / 高级配置」三个配置入口。</summary>
    public ConfigHubViewModel? ConfigHub { get; private set; }

    /// <summary>变更概览（二期 diff 视图，REQ-017）。</summary>
    public DiffViewModel? Diff { get; private set; }

    /// <summary>模板库（二期 REQ-016 模板展开与任务生成）。</summary>
    public TemplateEditorViewModel? Templates { get; private set; }

    public ViewModelBase? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string ActivePage
    {
        get => _activePage;
        private set => SetProperty(ref _activePage, value);
    }

    public IRelayCommand<string>? NavigateCommand { get; private set; }

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

    public IRelayCommand? ToggleThemeCommand { get; private set; }

    public string ConfigDirectory => Host.ConfigDirectory;

    private void Navigate(string? page)
    {
        var configHub = ConfigHub!;
        var monitor = Monitor!;
        var report = Report!;
        var diff = Diff!;
        var templates = Templates!;
        switch (page)
        {
            case "confighub":
                configHub.HubNavigate(configHub.ActiveSubview); // 进入时刷新当前激活的子视图
                CurrentPage = configHub;
                ActivePage = "confighub";
                break;
            case "report":
                report.Refresh();
                CurrentPage = report;
                ActivePage = "report";
                break;
            case "diff":
                diff.Refresh();
                CurrentPage = diff;
                ActivePage = "diff";
                break;
            case "templates":
                _ = templates.LoadAsync();
                CurrentPage = templates;
                ActivePage = "templates";
                break;
            default:
                CurrentPage = monitor;
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
