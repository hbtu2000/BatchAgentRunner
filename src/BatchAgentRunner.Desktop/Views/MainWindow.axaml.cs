using Avalonia.Controls;

namespace BatchAgentRunner.Desktop.Views;

/// <summary>主窗口：承载导航与各功能页，启动时登记为桌面生命周期主窗口。</summary>
public partial class MainWindow : Window
{
    /// <summary>初始化 XAML 组件。</summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
