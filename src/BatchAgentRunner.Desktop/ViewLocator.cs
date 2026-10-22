using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using BatchAgentRunner.Desktop.ViewModels;

namespace BatchAgentRunner.Desktop;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    /// <summary>按视图模型的类型名定位对应视图并实例化（ViewModel → View 约定）。</summary>
    /// <param name="param">视图模型实例；null 时返回 null。</param>
    /// <returns>视图控件；找不到同名视图时返回「Not Found」提示控件。</returns>
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    /// <summary>仅当数据是 ViewModelBase 时才按约定定位视图。</summary>
    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
