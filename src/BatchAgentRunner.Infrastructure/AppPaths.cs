namespace BatchAgentRunner.Infrastructure;

/// <summary>
/// 应用本地数据目录的<b>单一事实来源</b>（P2-31）。
/// </summary>
/// <remarks>
/// 治理前「<c>LocalApplicationData</c> + 应用名」这组路径前缀在 <c>src</c> 内散落 <b>9 处</b>
/// （三份 <c>Json*Store</c> 各 1 处 + <c>FileWorkflowTemplateStore</c> / <c>FileCredentialVault</c> /
/// <c>FileRunLogger</c> / <c>Desktop/Program</c> / <c>Desktop/Composition/EngineFactory</c> 各 1~2 处），
/// 且三份 Store 又各自复制了「解析目录 → 建目录 → 拼文件名」的构造样板——改一处必漏改两处。
/// 收口后：应用名与子目录名只在本类出现一次，新增数据目录必须在此登记；
/// 「<c>src</c> 内不得再内联应用名路径」由 <c>AppPathsHygieneTests</c> 锁死。
///
/// 边界：本类只管**磁盘目录**。通知标题里的 <c>[BatchAgentRunner]</c> 是品牌前缀而非路径，
/// 不在 P2-31 范围内，保持原样。
/// </remarks>
public static class AppPaths
{
    private const string AppName = "BatchAgentRunner";

    /// <summary>本地数据根目录：<c>%LOCALAPPDATA%\BatchAgentRunner</c>。</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>
    /// 运行配置与三个 JSON 仓储的共用目录（<c>config.json</c> / <c>cooldowns.json</c> / <c>credits-claims.json</c>）。
    /// 历史原因该组文件直接落在根目录，不额外套子目录——改名会破坏既有用户的数据位置。
    /// </summary>
    public static string ConfigDirectory => Root;

    /// <summary>工作流/任务模板根目录（内置 <c>builtin/</c> + 用户 <c>user/</c>）。</summary>
    public static string TemplatesDirectory => Path.Combine(Root, "templates");

    /// <summary>凭证 Vault 根目录（DPAPI 静态加密 + 受限 ACL）。</summary>
    public static string VaultDirectory => Path.Combine(Root, "vault");

    /// <summary>引擎运行日志目录（未显式指定任务文件时的默认落点）。</summary>
    public static string RunsDirectory => Path.Combine(Root, "runs");

    /// <summary>应用级日志目录（启动/退出/进程级异常，与 <see cref="RunsDirectory"/> 区分）。</summary>
    public static string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Mock 工具的「Live 凭证」根目录（与 Vault 共用位置以便全链路联调）。</summary>
    public static string MockLiveDirectory => Path.Combine(Root, "mock-live");
}
