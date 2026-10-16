using System.Globalization;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using ClosedXML.Excel;

namespace BatchAgentRunner.Infrastructure.TaskFiles;

/// <summary>
/// xlsx 结果回写（与 JSON 一致为原地原子写回）：保留用户原有列布局，仅更新规范结果列；
/// 缺失的规范列自动追加到末列。未知列（用户自定义列）原样保留。
/// </summary>
public static class XlsxTaskWriter
{
    public static async Task WriteAsync(string path, IReadOnlyList<TaskItem> tasks,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}.xlsx";
        try
        {
            using (var workbook = new XLWorkbook(path))
            {
                var worksheet = workbook.Worksheets.FirstOrDefault()
                                ?? throw new TaskFileFormatException("xlsx 中未找到任何工作表");
                WriteWorksheet(worksheet, tasks);
                cancellationToken.ThrowIfCancellationRequested();
                workbook.SaveAs(tempPath);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    internal static void WriteWorksheet(IXLWorksheet worksheet, IReadOnlyList<TaskItem> tasks)
    {
        var columnMap = XlsxTaskParser.MapColumns(worksheet, out var headerRowNumber);

        // 确保全部规范列存在：缺失的追加到末列并写入表头。
        var maxCol = worksheet.LastCellUsed()?.Address.ColumnNumber ?? columnMap.Values.DefaultIfEmpty(0).Max();
        foreach (var field in XlsxTaskParser.CanonicalFields)
        {
            if (!columnMap.ContainsKey(field))
            {
                maxCol++;
                columnMap[field] = maxCol;
                worksheet.FirstRowUsed()!.Cell(maxCol).Value = field;
            }
        }

        var firstDataRow = headerRowNumber + 1;
        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var row = worksheet.Row(firstDataRow + i);

            SetCell(row, columnMap, "id", task.Id);
            SetCell(row, columnMap, "workspace", task.Workspace);
            SetCell(row, columnMap, "instruction", task.Instruction);
            SetCell(row, columnMap, "status", task.Status.ToString());
            SetCell(row, columnMap, "result", task.Result);
            SetCell(row, columnMap, "model_used", task.ModelUsed);
            SetCell(row, columnMap, "account_used", task.AccountUsed);
            SetCell(row, columnMap, "expert_used", task.ExpertUsed);
            SetCell(row, columnMap, "fallback_used", task.FallbackUsed);
            SetCell(row, columnMap, "fallback_cost", task.FallbackCost);
            SetCell(row, columnMap, "retry_count", task.RetryCount);
            SetCell(row, columnMap, "error", task.Error);
            SetCell(row, columnMap, "started_at",
                task.StartedAt?.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
            SetCell(row, columnMap, "finished_at",
                task.FinishedAt?.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
            SetCell(row, columnMap, "duration", task.DurationSeconds);
        }
    }

    private static void SetCell(IXLRow row, Dictionary<string, int> map, string canonical, object? value)
    {
        if (!map.TryGetValue(canonical, out var col))
        {
            return;
        }

        var cell = row.Cell(col);
        switch (value)
        {
            case null:
                cell.Value = string.Empty;
                break;
            case string s:
                cell.Value = s;
                break;
            case bool b:
                cell.Value = b;
                break;
            case int i:
                cell.Value = i;
                break;
            case double d:
                cell.Value = d;
                break;
            case decimal m:
                cell.Value = m;
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }
}
