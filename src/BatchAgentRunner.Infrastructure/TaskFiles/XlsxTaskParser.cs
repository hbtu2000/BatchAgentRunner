using System.Globalization;
using BatchAgentRunner.Core.Abstractions;
using BatchAgentRunner.Core.Domain;
using ClosedXML.Excel;
using TaskStatus = BatchAgentRunner.Core.Domain.TaskStatus;

namespace BatchAgentRunner.Infrastructure.TaskFiles;

/// <summary>
/// xlsx 任务文件解析（REQ-001 扩展）：首行为表头，至少需 <c>id</c> 与 <c>instruction</c> 两列
/// （支持中文表头别名）。其余结果列（status / result / model_used ...）若存在则作为续跑起点读取。
/// 读取结果与 JSON 一致为自包含格式：状态与结果原地写回同一 xlsx（见 <see cref="XlsxTaskWriter"/>）。
/// </summary>
public static class XlsxTaskParser
{
    /// <summary>表头别名 → 规范字段名（与 <see cref="TaskItem"/> 的 JSON 字段对齐）。</summary>
    private static readonly Dictionary<string, string> ColumnAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "id",
        ["任务id"] = "id",
        ["任务编号"] = "id",
        ["编号"] = "id",
        ["instruction"] = "instruction",
        ["指令"] = "instruction",
        ["任务"] = "instruction",
        ["任务内容"] = "instruction",
        ["workspace"] = "workspace",
        ["工作空间"] = "workspace",
        ["工作区"] = "workspace",
        ["status"] = "status",
        ["状态"] = "status",
        ["result"] = "result",
        ["结果"] = "result",
        ["model_used"] = "model_used",
        ["使用模型"] = "model_used",
        ["account_used"] = "account_used",
        ["使用账号"] = "account_used",
        ["expert_used"] = "expert_used",
        ["使用专家"] = "expert_used",
        ["fallback_used"] = "fallback_used",
        ["是否兜底"] = "fallback_used",
        ["fallback_cost"] = "fallback_cost",
        ["兜底花费"] = "fallback_cost",
        ["retry_count"] = "retry_count",
        ["重试次数"] = "retry_count",
        ["error"] = "error",
        ["错误"] = "error",
        ["started_at"] = "started_at",
        ["开始时间"] = "started_at",
        ["finished_at"] = "finished_at",
        ["结束时间"] = "finished_at",
        ["duration"] = "duration",
        ["耗时"] = "duration",
        ["耗时秒"] = "duration"
    };

    /// <summary>续跑/写回需要保证存在的规范列（按追加顺序）。</summary>
    internal static readonly IReadOnlyList<string> CanonicalFields = new[]
    {
        "id", "workspace", "instruction", "status", "result",
        "model_used", "account_used", "expert_used",
        "fallback_used", "fallback_cost", "retry_count",
        "error", "started_at", "finished_at", "duration"
    };

    public static List<TaskItem> Parse(string path, List<TaskFileIssue> issues)
    {
        using var workbook = new XLWorkbook(path);
        var worksheet = workbook.Worksheets.FirstOrDefault()
                        ?? throw new TaskFileFormatException("xlsx 中未找到任何工作表");
        return ParseWorksheet(worksheet, issues);
    }

    internal static List<TaskItem> ParseWorksheet(IXLWorksheet worksheet, List<TaskFileIssue> issues)
    {
        var headerRow = worksheet.FirstRowUsed()
                        ?? throw new TaskFileFormatException("xlsx 首行（表头）为空");
        var columnMap = MapColumns(worksheet, out _);

        if (!columnMap.ContainsKey("id") && !columnMap.ContainsKey("instruction"))
        {
            throw new TaskFileFormatException("xlsx 缺少必要的表头列（至少需要 id 与 instruction）");
        }

        var tasks = new List<TaskItem>();
        var firstDataRow = headerRow.RowNumber() + 1;
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? firstDataRow - 1;

        // 先扫描全表已填写的 id：自动生成的 id 必须避开它们，否则会与后续行的显式 id 撞车，
        // 进而在 NormalizeAndValidateIds 抛「任务 ID 重复」导致整批加载失败（2026-10-18 现场）。
        var usedIds = CollectExplicitIds(worksheet, columnMap, firstDataRow, lastRow);
        var nextGeneratedIndex = 1;

        for (var r = firstDataRow; r <= lastRow; r++)
        {
            var row = worksheet.Row(r);
            if (row.IsEmpty())
            {
                continue;
            }

            var id = GetCell(row, columnMap, "id");
            var instruction = GetCell(row, columnMap, "instruction");
            var hasId = !string.IsNullOrWhiteSpace(id);

            if (string.IsNullOrWhiteSpace(instruction))
            {
                issues.Add(new TaskFileIssue("warning", id ?? $"行{r}",
                    $"第 {r} 行未找到 instruction（指令）内容，已跳过"));
                continue;
            }

            if (!hasId)
            {
                id = NextFreeId(usedIds, ref nextGeneratedIndex);
                issues.Add(new TaskFileIssue("info", id,
                    $"第 {r} 行缺少 id，已自动生成 {id}"));
            }

            var task = new TaskItem
            {
                Id = id!,
                Workspace = GetCell(row, columnMap, "workspace"),
                Instruction = instruction!,
                Status = ParseStatus(GetCell(row, columnMap, "status"), issues, id!),
                Result = GetCell(row, columnMap, "result"),
                ModelUsed = GetCell(row, columnMap, "model_used"),
                AccountUsed = GetCell(row, columnMap, "account_used"),
                ExpertUsed = GetCell(row, columnMap, "expert_used"),
                FallbackUsed = ParseBool(GetCell(row, columnMap, "fallback_used")),
                FallbackCost = ParseDecimal(GetCell(row, columnMap, "fallback_cost")),
                RetryCount = ParseInt(GetCell(row, columnMap, "retry_count")),
                Error = GetCell(row, columnMap, "error"),
                StartedAt = ParseDateTime(GetCell(row, columnMap, "started_at")),
                FinishedAt = ParseDateTime(GetCell(row, columnMap, "finished_at")),
                DurationSeconds = ParseDouble(GetCell(row, columnMap, "duration"))
            };
            tasks.Add(task);
        }

        if (tasks.Count == 0)
        {
            throw new TaskFileFormatException("xlsx 中未找到任何有效任务（至少需要一行含 instruction 的数据）");
        }

        return tasks;
    }

    /// <summary>读取表头行，建立「规范字段名 → 列号」映射（忽略未知列）。</summary>
    internal static Dictionary<string, int> MapColumns(IXLWorksheet worksheet, out int headerRowNumber)
    {
        var headerRow = worksheet.FirstRowUsed()
                        ?? throw new TaskFileFormatException("xlsx 首行（表头）为空");
        headerRowNumber = headerRow.RowNumber();

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            var name = GetCellText(cell)?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (ColumnAliases.TryGetValue(name, out var canonical) && !map.ContainsKey(canonical))
            {
                map[canonical] = cell.Address.ColumnNumber;
            }
        }

        return map;
    }

    /// <summary>收集表中所有显式填写（非空）的 id，供自动生成时避让。</summary>
    private static HashSet<string> CollectExplicitIds(IXLWorksheet worksheet, Dictionary<string, int> map,
        int firstDataRow, int lastRow)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!map.TryGetValue("id", out var idColumn))
        {
            return used;
        }

        for (var r = firstDataRow; r <= lastRow; r++)
        {
            var text = GetCellText(worksheet.Row(r).Cell(idColumn))?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                used.Add(text);
            }
        }

        return used;
    }

    /// <summary>取下一个未被占用的自动生成 id（task-00N），并立即登记为已占用。</summary>
    private static string NextFreeId(HashSet<string> used, ref int nextIndex)
    {
        string id;
        do
        {
            id = MarkdownTaskParser.GenerateId(nextIndex++);
        }
        while (!used.Add(id));

        return id;
    }

    private static string? GetCell(IXLRow row, Dictionary<string, int> map, string canonical) =>
        map.TryGetValue(canonical, out var col) ? GetCellText(row.Cell(col)) : null;

    private static string? GetCellText(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsBlank)
        {
            return null;
        }

        if (value.IsText)
        {
            return value.GetText();
        }

        if (value.IsBoolean)
        {
            return value.GetBoolean() ? "true" : "false";
        }

        if (value.IsDateTime)
        {
            return value.GetDateTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (value.IsTimeSpan)
        {
            return value.GetTimeSpan().ToString();
        }

        if (value.IsNumber)
        {
            return value.GetNumber().ToString(CultureInfo.InvariantCulture);
        }

        var text = value.GetText();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static TaskStatus ParseStatus(string? s, List<TaskFileIssue> issues, string id)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return TaskStatus.Pending;
        }

        if (Enum.TryParse<TaskStatus>(s, ignoreCase: true, out var status))
        {
            return status;
        }

        issues.Add(new TaskFileIssue("warning", id, $"状态值无法识别：{s}，按 Pending 处理"));
        return TaskStatus.Pending;
    }

    private static bool ParseBool(string? s) =>
        s is not null && (bool.TryParse(s, out var b)
            ? b
            : s == "1" || s.Equals("是", StringComparison.OrdinalIgnoreCase)
                   || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || s.Equals("y", StringComparison.OrdinalIgnoreCase));

    private static decimal? ParseDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static double ParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return 0;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static int ParseInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return 0;
        }

        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
    }

    private static DateTimeOffset? ParseDateTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            return dto;
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            return new DateTimeOffset(dt, TimeSpan.Zero);
        }

        return null;
    }
}
