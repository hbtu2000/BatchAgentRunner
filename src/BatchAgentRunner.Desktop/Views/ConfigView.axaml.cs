using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BatchAgentRunner.Desktop.ViewModels;

namespace BatchAgentRunner.Desktop.Views;

public partial class ConfigView : UserControl
{
    /// <summary>任务文件筛选通配符（CA1861：常量数组提为 static readonly）。</summary>
    private static readonly string[] TaskFilePatterns = { "*.json", "*.md", "*.markdown", "*.xlsx" };

    public ConfigView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ConfigViewModel vm)
        {
            vm.PickTaskFile = PickTaskFileAsync;
        }
    }

    private async Task<string?> PickTaskFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择任务文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("任务文件")
                {
                    Patterns = TaskFilePatterns
                },
                FilePickerFileTypes.All
            }
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }
}
