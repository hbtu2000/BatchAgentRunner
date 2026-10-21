using System.Collections.Generic;
using System.Threading.Tasks;
using BatchAgentRunner.Core.Abstractions;

namespace BatchAgentRunner.Desktop.ViewModels;

/// <summary>
/// P-05：模板库页「模板清单加载」逻辑收口（从 <see cref="TemplateEditorViewModel"/> 抽离的 Coordinator）。
/// 负责向 <see cref="IWorkflowTemplateStore"/> 拉取模板规格并映射到行视图模型，异常转为状态文案；
/// VM 仅负责把结果灌入 <see cref="TemplateEditorViewModel.Templates"/> 与状态栏。
/// </summary>
public sealed class TemplateLoadCoordinator
{
    private readonly IWorkflowTemplateStore _store;

    public TemplateLoadCoordinator(IWorkflowTemplateStore store) => _store = store;

    /// <summary>加载模板清单；返回行视图模型列表与状态文案（失败时为异常说明，列表为空）。</summary>
    public async Task<(List<TemplateRowViewModel> rows, string status)> LoadAsync()
    {
        try
        {
            var specs = await _store.ListAsync().ConfigureAwait(true);
            var rows = specs.Select(s => new TemplateRowViewModel(s)).ToList();
            return (rows, $"已加载 {rows.Count} 个模板");
        }
        catch (System.Exception ex)
        {
            return (new List<TemplateRowViewModel>(), $"加载模板失败：{ex.Message}");
        }
    }
}
