using System.Diagnostics;
using System.Text;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 配置向导：只保留最必要的两步——「选择执行工具 → 检测 CLI（含登录）」。
/// 账号、模型、任务文件、工作空间等高级配置一律由「配置」页管理，向导不触碰。
/// </summary>
public sealed class SetupWizardViewModel : ViewModelBase
{
    private const int LastStep = 1;

    private readonly IEngineHost _host;

    private int _step;
    private string _stepError = string.Empty;
    private bool _cliDetected;
    private bool _cliCheckRunning;
    private string _cliCheckResult = string.Empty;
    private AgentKind _agentKind;
    private string _cliPath = string.Empty;
    private string _loginHint = string.Empty;
    private string _completedMessage = string.Empty;

    /// <summary>创建配置向导，从配置读入内核与 CLI 路径并装好六个命令。</summary>
    public SetupWizardViewModel(IEngineHost host)
    {
        _host = host;
        var o = host.Options;

        _agentKind = o.AgentKind;
        _cliPath = o.CliPath ?? string.Empty;

        DetectCliCommand = new AsyncRelayCommand(DetectCliAsync, () => !CliCheckRunning);
        OpenLoginTerminalCommand = new RelayCommand(OpenLoginTerminal);
        NextCommand = new RelayCommand(Next);
        BackCommand = new RelayCommand(Back);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RestartCommand = new RelayCommand(Restart);
    }

    // ---------------- 步骤状态 ----------------

    /// <summary>当前步骤索引（0 = 选择工具，1 = 检测 CLI），切换时清空错误提示。</summary>
    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(IsStep0));
                OnPropertyChanged(nameof(IsStep1));
                OnPropertyChanged(nameof(StepIndicator));
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(IsFirstStep));
                OnPropertyChanged(nameof(IsLastStep));
                StepError = string.Empty;
            }
        }
    }

    /// <summary>是否停留在第一步（选择执行工具），控制该页显隐。</summary>
    public bool IsStep0 => Step == 0;
    /// <summary>是否已进入第二步（检测 CLI），控制检测区显隐。</summary>
    public bool IsStep1 => Step == 1;

    /// <summary>是否为首步，为真时隐藏「上一步」按钮。</summary>
    public bool IsFirstStep => Step == 0;
    /// <summary>是否为末步，为真时「下一步」改为完成保存。</summary>
    public bool IsLastStep => Step == LastStep;

    /// <summary>步骤进度文案，形如「第 1 / 2 步」。</summary>
    public string StepIndicator => $"第 {Step + 1} / {LastStep + 1} 步";

    /// <summary>当前步骤标题，随 Step 切换为「欢迎」或「检测 CLI」。</summary>
    public string StepTitle => Step switch
    {
        0 => "欢迎 · 选择执行工具",
        _ => "检测 CLI"
    };

    /// <summary>步骤级错误提示，切换步骤时自动清空。</summary>
    public string StepError
    {
        get => _stepError;
        private set => SetProperty(ref _stepError, value);
    }

    // ---------------- 步骤 0：执行工具 ----------------

    /// <summary>选定的执行工具内核，保存时写回配置。</summary>
    public AgentKind AgentKind
    {
        get => _agentKind;
        private set => SetProperty(ref _agentKind, value);
    }

    /// <summary>单选按钮绑定：是否选中 WorkBuddy 内核，置真时切换选择。</summary>
    public bool AgentIsWorkBuddy
    {
        get => AgentKind == AgentKind.WorkBuddy;
        set { if (value) SetAgentKind(AgentKind.WorkBuddy); OnPropertyChanged(); }
    }

    /// <summary>单选按钮绑定：是否选中 CodeBuddy 内核，置真时切换选择。</summary>
    public bool AgentIsCodeBuddy
    {
        get => AgentKind == AgentKind.CodeBuddy;
        set { if (value) SetAgentKind(AgentKind.CodeBuddy); OnPropertyChanged(); }
    }

    /// <summary>单选按钮绑定：是否选中 Mock 内核，仅用于本地演练。</summary>
    public bool AgentIsMock
    {
        get => AgentKind == AgentKind.Mock;
        set { if (value) SetAgentKind(AgentKind.Mock); OnPropertyChanged(); }
    }

    private void SetAgentKind(AgentKind kind)
    {
        AgentKind = kind;
        OnPropertyChanged(nameof(AgentIsWorkBuddy));
        OnPropertyChanged(nameof(AgentIsCodeBuddy));
        OnPropertyChanged(nameof(AgentIsMock));
    }

    // ---------------- 步骤 1：CLI 检测 ----------------

    /// <summary>CLI 可执行文件路径，留空则按 PATH 中的 codebuddy 查找。</summary>
    public string CliPath
    {
        get => _cliPath;
        set => SetProperty(ref _cliPath, value);
    }

    /// <summary>上次检测是否找到可用 CLI，决定是否允许进入下一步。</summary>
    public bool CliDetected
    {
        get => _cliDetected;
        private set => SetProperty(ref _cliDetected, value);
    }

    /// <summary>检测是否进行中，期间禁用检测按钮防止重复点击。</summary>
    public bool CliCheckRunning
    {
        get => _cliCheckRunning;
        private set => SetProperty(ref _cliCheckRunning, value);
    }

    /// <summary>检测结果说明，含版本号或安装指引，检测结束后写入。</summary>
    public string CliCheckResult
    {
        get => _cliCheckResult;
        private set => SetProperty(ref _cliCheckResult, value);
    }

    /// <summary>登录引导提示，打开终端后写入并展示给用户。</summary>
    public string LoginHint
    {
        get => _loginHint;
        private set
        {
            if (SetProperty(ref _loginHint, value))
            {
                OnPropertyChanged(nameof(HasLoginHint));
            }
        }
    }

    /// <summary>是否有登录提示文案，为假时隐藏提示条。</summary>
    public bool HasLoginHint => !string.IsNullOrEmpty(_loginHint);

    /// <summary>保存完成后的说明文案，含配置路径与后续引导。</summary>
    public string CompletedMessage
    {
        get => _completedMessage;
        private set => SetProperty(ref _completedMessage, value);
    }

    // ---------------- 命令 ----------------

    /// <summary>检测命令，点击后执行 CLI --version 并回报结果。</summary>
    public IAsyncRelayCommand DetectCliCommand { get; }

    /// <summary>登录命令，点击后新开终端窗口运行 codebuddy 供用户登录。</summary>
    public IRelayCommand OpenLoginTerminalCommand { get; }

    /// <summary>下一步命令，点击后前进到下一向导步骤（不越过末步）。</summary>
    public IRelayCommand NextCommand { get; }

    /// <summary>上一步命令，点击后回到前一向导步骤（不越过首步）。</summary>
    public IRelayCommand BackCommand { get; }

    /// <summary>保存命令，把内核与 CLI 路径写入配置并重建引擎。</summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>重来命令，清空完成提示并回到第一步。</summary>
    public IRelayCommand RestartCommand { get; }

    // ---------------- 步骤流转 ----------------

    private void Next()
    {
        Step = Math.Min(Step + 1, LastStep);
    }

    private void Back()
    {
        Step = Math.Max(0, Step - 1);
    }

    private void Restart()
    {
        CompletedMessage = string.Empty;
        Step = 0;
    }

    // ---------------- CLI 检测 ----------------

    /// <summary>运行 CLI 并捕获输出（Windows 下经 cmd.exe 以便解析 .cmd 入口）。</summary>
    private async Task<(int ExitCode, string Output)> RunCliOutputAsync(string arguments)
    {
        var cliPath = CliPath?.Trim();
        var isWindows = OperatingSystem.IsWindows();

        string fileName;
        string resolvedArguments;
        if (!string.IsNullOrEmpty(cliPath))
        {
            fileName = isWindows ? "cmd.exe" : cliPath;
            resolvedArguments = isWindows ? $"/c \"\"{cliPath}\" {arguments}\"" : arguments;
        }
        else
        {
            fileName = isWindows ? "cmd.exe" : "codebuddy";
            resolvedArguments = isWindows ? $"/c codebuddy {arguments}" : arguments;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = resolvedArguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // CLI 输出为 UTF-8；不指定会按控制台代码页（GBK）解码导致中文乱码
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        });
        if (process is null)
        {
            throw new InvalidOperationException("无法启动 CLI 进程");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 忽略终止失败 */ }
            throw new TimeoutException($"CLI 响应超时（15 秒）：{arguments}");
        }

        var output = (await stdoutTask.ConfigureAwait(true) + await stderrTask.ConfigureAwait(true)).Trim();
        return (process.ExitCode, output);
    }

    private async Task DetectCliAsync()
    {
        CliCheckRunning = true;
        CliCheckResult = "正在检测 CLI…";
        ((AsyncRelayCommand)DetectCliCommand).NotifyCanExecuteChanged();
        try
        {
            var (exitCode, output) = await RunCliOutputAsync("--version").ConfigureAwait(true);
            if (exitCode == 0 && output.Length > 0)
            {
                CliDetected = true;
                CliCheckResult = $"检测通过：{output.Split('\n').FirstOrDefault()?.Trim() ?? output}";
            }
            else
            {
                CliDetected = false;
                CliCheckResult =
                    "未找到可用的 codebuddy CLI。" + Environment.NewLine +
                    "请先安装：npm install -g @tencent-ai/codebuddy-code" + Environment.NewLine +
                    $"CLI 输出：{output}";
            }
        }
        catch (Exception ex)
        {
            CliDetected = false;
            CliCheckResult =
                $"检测失败：{ex.Message}" + Environment.NewLine +
                "请先安装：npm install -g @tencent-ai/codebuddy-code，或在上方填写 CLI 的绝对路径。";
        }
        finally
        {
            CliCheckRunning = false;
            ((AsyncRelayCommand)DetectCliCommand).NotifyCanExecuteChanged();
        }
    }

    private void OpenLoginTerminal()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/k codebuddy")
                {
                    UseShellExecute = true,
                    CreateNoWindow = false
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo("codebuddy") { UseShellExecute = true });
            }

            LoginHint =
                "已在新终端窗口打开 codebuddy：按提示完成登录（微信 / 手机号 / 邮箱）后回到本页点「检测 CLI」确认。" +
                "登录凭据由 CLI 自己保存，本工具不接触明文。";
        }
        catch (Exception ex)
        {
            LoginHint = $"打开终端失败：{ex.Message}。请手动打开终端运行 codebuddy 完成登录。";
        }
    }

    // ---------------- 保存 ----------------

    private async Task SaveAsync()
    {
        // 向导只写入这两步覆盖的字段；账号/模型/任务文件/工作空间等保持「配置」页当前值不变。
        var o = _host.Options;
        o.AgentKind = AgentKind;
        o.CliPath = string.IsNullOrWhiteSpace(CliPath) ? null : CliPath;

        await _host.SaveConfigAsync().ConfigureAwait(true);
        await _host.RebuildEngineAsync().ConfigureAwait(true);

        CompletedMessage =
            $"配置已保存到 {System.IO.Path.Combine(_host.ConfigDirectory, "config.json")}，引擎已按新配置重建。" + Environment.NewLine +
            "账号、模型、任务文件等请到左侧「配置」页设置；CLI 登录状态可到「执行监控」页点「预检」确认。";
    }
}
