using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Domain;
using BatchAgentRunner.Desktop.Models;

namespace BatchAgentRunner.Desktop.ViewModels.Config;

/// <summary>
/// 模型目录填充共享逻辑（P-04 分解产物）。
/// 原 <c>ConfigViewModel.FillAvailableModels</c> 同时服务「共享列表拉取」与
/// 「账号卡拉取」两条路径；分解为降级 / 账号卡两个子编辑器后，二者共用本填充器——
/// 倍率优先用官方目录 credits、官方没有的回退已配折扣、已配折扣但目录没有的追加尾部，
/// 规则只此一份（含目录内重名去重与空白名跳过）。
/// </summary>
internal static class ModelCatalogFiller
{
    internal static void Fill(
        ObservableCollection<ModelOption> target,
        IReadOnlyList<ModelCatalogEntry> entries,
        IEnumerable<ModelEditor> configured)
    {
        var configuredDiscount = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var editor in configured)
        {
            var key = editor.Name.Trim();
            if (key.Length == 0 || configuredDiscount.ContainsKey(key))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(editor.DiscountDisplay))
            {
                configuredDiscount[key] = editor.DiscountDisplay;
            }
        }

        target.Clear();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = entry.Name.Trim();
            if (key.Length == 0 || !present.Add(key))
            {
                continue;
            }

            var display = entry.CreditsMultiplier is { } multiplier
                ? $"{multiplier:0.00}x"
                : configuredDiscount.TryGetValue(key, out var discount) ? discount : string.Empty;
            target.Add(new ModelOption(entry.Name, display));
        }

        foreach (var (name, discount) in configuredDiscount)
        {
            if (present.Add(name))
            {
                target.Add(new ModelOption(name, discount));
            }
        }
    }
}
