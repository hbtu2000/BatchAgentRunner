using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Services;
namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// 登录页视图缝（P-06 分解产物）：<see cref="LoginViewModel"/> facade 向 Login 命名空间下的
/// 协作者（<see cref="GuidedLoginFlow"/> / <see cref="CreditsClaimFlow"/> / <see cref="AccountPoolCoordinator"/>）
/// 暴露的最小可变状态面。协作者经本接口读写 UI 状态与宿主，不反向依赖 facade 具体类型
/// （沿用 P-01/P-02「上下文注入」范式，避免循环具体依赖）。
/// </summary>
internal interface ILoginView
{
    /// <summary>引擎宿主（Vault / 配置 / 保存 / 重建等端口入口）。</summary>
    IEngineHost Host { get; }

    /// <summary>当前选择的 CLI 类型（页面下拉与流程守卫读同一值）。</summary>
    AgentKind AgentKind { get; }

    /// <summary>新账号显示名（重新登录时由账号行预填）。</summary>
    string NewAccountDisplayName { get; set; }

    /// <summary>登录/领取忙碌态（与领取互斥：同一时刻 CLI 只有一个 Live 身份）。</summary>
    bool IsBusy { get; set; }

    /// <summary>积分领取进行中。</summary>
    bool IsClaiming { get; set; }

    /// <summary>状态条文案。</summary>
    string StatusText { get; set; }

    /// <summary>结果横幅文案。</summary>
    string ResultText { get; set; }

    /// <summary>结果横幅成功态。</summary>
    bool IsSuccess { get; set; }

    /// <summary>结果横幅失败态。</summary>
    bool IsError { get; set; }

    /// <summary>账号行集合（AccountPoolCoordinator 独占重建，facade 透传）。</summary>
    ObservableCollection<LoginAccountRow> Rows { get; }

    /// <summary>积分领取结果行集合（facade 持有，CreditsClaimFlow 经本接口填充）。</summary>
    ObservableCollection<CreditsClaimRow> ClaimRows { get; }

    /// <summary>登录步骤条（GuidedLoginFlow 经本接口驱动步骤态）。</summary>
    LoginStepsPresenter Steps { get; }

    /// <summary>积分结果行集合变更后刷新 <c>HasClaimRows</c> 绑定通知。</summary>
    void NotifyClaimRowsChanged();

    /// <summary>按账号池 + Vault 快照重建账号行（不刷新 AgentKind / ForceClaim）。</summary>
    void RebuildRows();

    /// <summary>导航进入本页时的全量刷新（同步 AgentKind + ForceClaim + 重建账号行）。</summary>
    void Refresh();
}
