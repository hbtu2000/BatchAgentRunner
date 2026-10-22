using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 配置页共享状态消息槽（P-04 分解产物）。
/// 原 <c>ConfigViewModel.StatusMessage</c> 由「保存结果 / 模型拉取结果 / 工作空间上限拦截」
/// 多处跨职责写入；分解为子编辑器 + 保存协调器后，经本类型收敛为单一可绑定消息源
/// （facade 的 <c>StatusMessage</c> 转发到此），写入方只依赖本槽、互不知晓。
/// </summary>
public sealed class ConfigStatus : ObservableObject
{
    private string _message = string.Empty;

    /// <summary>当前状态消息（配置中心底部保存条展示）。</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>写入一条状态消息（调用方须已在 UI 线程，与各编辑器命令的执行线程一致）。</summary>
    /// <param name="message">要显示在配置页底部保存条的状态文案。</param>
    public void Show(string message) => Message = message;
}
