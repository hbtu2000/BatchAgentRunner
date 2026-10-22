namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 登录/账号操作异常的人话翻译（P-06 从 <see cref="LoginViewModel"/> 抽出的纯函数）。
/// 把 Vault / 编排器的英文技术异常翻译为面向用户的可执行文案，并保留原始细节（换行附注）。
/// </summary>
internal static class LoginErrorTranslator
{
    /// <summary>把技术异常翻译为面向用户、可照做的中文提示。</summary>
    public static string Translate(Exception ex)
    {
        var raw = ex.Message;
        if (raw.Contains("超时"))
        {
            return "等了 5 分钟还没检测到登录完成——可能是浏览器里的登录没有点完。" +
                   "点「开始登录」重来一次就好；如果反复失败，先在终端里手动运行 codebuddy 确认能正常登录。" +
                   Environment.NewLine + $"（细节：{raw}）";
        }

        if (raw.Contains("进程仍在运行") || raw.Contains("进程互斥"))
        {
            return "有一个 CLI 窗口还开着，请先把它整个关掉，再点「开始登录」。" +
                   Environment.NewLine + $"（细节：{raw}）";
        }

        if (raw.Contains("未找到"))
        {
            return "电脑上没找到 codebuddy 命令。先安装：npm install -g @tencent-ai/codebuddy-code，装完再点「开始登录」。" +
                   Environment.NewLine + $"（细节：{raw}）";
        }

        return raw;
    }
}
