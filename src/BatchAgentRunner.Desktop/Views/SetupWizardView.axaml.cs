using Avalonia.Controls;
using BatchAgentRunner.Desktop.ViewModels;

namespace BatchAgentRunner.Desktop.Views;

/// <summary>首次启动引导页：引导用户完成账号与任务文件配置。</summary>
public partial class SetupWizardView : UserControl
{
    /// <summary>初始化 XAML 组件。</summary>
    public SetupWizardView()
    {
        InitializeComponent();
    }
}
