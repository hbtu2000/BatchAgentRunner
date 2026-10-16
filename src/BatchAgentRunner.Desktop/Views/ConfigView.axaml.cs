using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BatchAgentRunner.Desktop.ViewModels;

namespace BatchAgentRunner.Desktop.Views;

public partial class ConfigView : UserControl
{
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
                    Patterns = new[] { "*.json", "*.md", "*.markdown", "*.xlsx" }
                },
                FilePickerFileTypes.All
            }
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }
}
