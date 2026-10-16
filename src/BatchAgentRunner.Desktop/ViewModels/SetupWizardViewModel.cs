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

    private readonly EngineHost _host;

    private int _step;
    private string _stepError = string.Empty;
    private bool _cliDetected;
    private bool _cliCheckRunning;
    private string _cliCheckResult = string.Empty;
    private AgentKind _agentKind;
    private string _cliPath = string.Empty;
    private string _loginHint = string.Empty;
    private string _completedMessage = string.Empty;

    public SetupWizardViewModel(EngineHost host)
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

    public bool IsStep0 => Step == 0;
    public bool IsStep1 => Step == 1;

    public bool IsFirstStep => Step == 0;
    public bool IsLastStep => Step == LastStep;

    public string StepIndicator => $"第 {Step + 1} / {LastStep + 1} 步";

    public string StepTitle => Step switch
    {
        0 => "欢迎 · 选择执行工具",
        _ => "检测 CLI"
    };

    public string StepError
    {
        get => _stepError;
        private set => SetProperty(ref _stepError, value);
    }

    // ---------------- 步骤 0：执行工具 ----------------

    public AgentKind AgentKind
    {
        get => _agentKind;
        private set => SetProperty(ref _agentKind, value);
    }

    public bool AgentIsWorkBuddy
    {
        get => AgentKind == AgentKind.WorkBuddy;
        set { if (value) SetAgentKind(AgentKind.WorkBuddy); OnPropertyChanged(); }
    }

    public bool AgentIsCodeBuddy
    {
        get => AgentKind == AgentKind.CodeBuddy;
        set { if (value) SetAgentKind(AgentKind.CodeBuddy); OnPropertyChanged(); }
    }

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

    public string CliPath
    {
        get => _cliPath;
        set => SetProperty(ref _cliPath, value);
    }

    public bool CliDetected
    {
        get => _cliDetected;
        private set => SetProperty(ref _cliDetected, value);
    }

    public bool CliCheckRunning
    {
        get => _cliCheckRunning;
        private set => SetProperty(ref _cliCheckRunning, value);
    }

    public string CliCheckResult
    {
        get => _cliCheckResult;
        private set => SetProperty(ref _cliCheckResult, value);
    }

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

    public bool HasLoginHint => !string.IsNullOrEmpty(_loginHint);

    public string CompletedMessage
    {
        get => _completedMessage;
        private set => SetProperty(ref _completedMessage, value);
    }

    // ---------------- 命令 ----------------

    public IAsyncRelayCommand DetectCliCommand { get; }

    public IRelayCommand OpenLoginTerminalCommand { get; }

    public IRelayCommand NextCommand { get; }

    public IRelayCommand BackCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

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
        _host.RebuildEngine();

        CompletedMessage =
            $"配置已保存到 {System.IO.Path.Combine(_host.ConfigDirectory, "config.json")}，引擎已按新配置重建。" + Environment.NewLine +
            "账号、模型、任务文件等请到左侧「配置」页设置；CLI 登录状态可到「执行监控」页点「预检」确认。";
    }
}
