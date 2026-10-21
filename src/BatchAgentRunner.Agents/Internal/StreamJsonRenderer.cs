using System.Text;
using System.Text.Json;
using BatchAgentRunner.Core.Text;

namespace BatchAgentRunner.Agents.Internal;

/// <summary>
/// 把 CodeBuddy CLI 的 <c>--output-format stream-json</c> 输出（逐行 JSON 事件）翻译成可读文本行，
/// 供应用内实时输出流展示；同时抽取最终 answer（<c>result</c> 事件）供结果回写。
/// 设计原则：解析失败的任意行原样透传，保证错误/告警等非 JSON 文本不丢失。
/// 解析异常一律降级为透传，绝不让渲染器炸掉主流程。
/// </summary>
public sealed class StreamJsonRenderer
{
    /// <summary>最终回答文本（来自 <c>result</c> 事件；为 null 表示尚未收到或解析失败）。</summary>
    public string? FinalResult { get; private set; }

    /// <summary>
    /// 最终 <c>result</c> 事件是否带 <c>is_error=true</c>（CLI 自报执行失败，
    /// 如 429 限流/额度耗尽）。供适配层作为 exit 0 时的失败兜底信号。
    /// </summary>
    public bool FinalIsError { get; private set; }

    /// <summary>是否已通过 stream_event 实时透出过增量正文；为 true 后完整 assistant 正文不再重复输出。</summary>
    private bool _sawPartialText;

    /// <summary>增量正文合并缓冲：连续 stream_event 的 delta.text 先累积，按换行或阈值刷成连贯行，降低事件量（避免逐字成行压垮日志/UI）。</summary>
    private readonly StringBuilder _pending = new();

    /// <summary>增量合并阈值（字符）：缓冲达到该长度即刷出一条行。</summary>
    private const int CoalesceThreshold = 200;

    /// <summary>工具入参（partial_json）累积上限（字符）：Write/Edit 会把整份文件内容当入参，防止吃内存。</summary>
    private const int MaxToolInputCaptureChars = 4096;

    /// <summary>工具入参累积缓冲：由 <c>input_json_delta</c> 逐片拼成完整 JSON，块结束时解析成一行摘要。</summary>
    private readonly StringBuilder _toolInput = new();

    /// <summary>已由 stream_event 实时输出过的 tool_use 块 id，避免随后完整 assistant 事件重复输出同一工具调用。</summary>
    private readonly HashSet<string> _emittedToolUseIds = new(StringComparer.Ordinal);

    /// <summary>当前正在流式接收入参的工具名（null 表示不在工具块内）。</summary>
    private string? _toolName;

    /// <summary>工具入参是否因超长放弃累积（仅影响摘要，不影响任务执行）。</summary>
    private bool _toolInputTruncated;

    /// <summary>是否已提示过未知增量形态（只提示一次，避免刷屏）。</summary>
    private bool _unknownDeltaReported;

    /// <summary>喂入一行原始输出（CLI 的 stdout/stderr 单行），返回要转发的可读行。</summary>
    public IReadOnlyList<string> Feed(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
        {
            return Array.Empty<string>();
        }

        JsonElement doc;
        try
        {
            using var d = JsonDocument.Parse(line);
            doc = d.RootElement.Clone();
        }
        catch (JsonException)
        {
            // 非 JSON（stderr 错误/告警、启动横幅等）→ 先刷出增量缓冲再原样透传
            var miss = new List<string>();
            FlushPending(miss);
            miss.Add(line);
            return miss;
        }

        try
        {
            if (!doc.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                var miss = new List<string>();
                FlushPending(miss);
                miss.Add(line);
                return miss;
            }

            var outputs = new List<string>();
            var type = typeEl.GetString();
            // 非 stream_event（回合边界/完整事件）到达时，先把累积的增量缓冲刷出，避免末尾正文滞留
            if (type != "stream_event")
            {
                FlushPending(outputs);
            }

            switch (type)
            {
                case "assistant":
                    EmitAssistant(doc, outputs);
                    break;
                case "user":
                    EmitUser(doc, outputs);
                    break;
                case "result":
                    EmitResult(doc, outputs);
                    break;
                case "stream_event":
                    EmitStreamEvent(doc, outputs, line);
                    break;
                // system / error 等未知类型：尽量抽取可读文本兜底，避免实时流静默；
                // 抽不到则原样透出整行，绝不让实时流整片空白
                default:
                    TryEmitAnyText(doc, outputs);
                    if (outputs.Count == 0)
                    {
                        outputs.Add(line);
                    }

                    break;
            }

            return outputs;
        }
        catch
        {
            return new[] { line };
        }
    }

    private void EmitAssistant(JsonElement doc, List<string> outputs)
    {
        if (!doc.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            var t = content.GetString();
            // 已通过 stream_event 实时透出过增量正文时，跳过完整正文避免重复
            if (!string.IsNullOrEmpty(t) && !_sawPartialText)
            {
                outputs.Add(t!);
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt) || bt.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var btype = bt.GetString();
            if (btype == "text")
            {
                if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                {
                    var t = txt.GetString()!;
                    if (t.Length > 0 && !_sawPartialText)
                    {
                        outputs.Add(t);
                    }
                }
            }
            else if (btype == "tool_use")
            {
                // 该工具调用已通过 stream_event（content_block_start/stop）实时输出过，此处跳过避免重复
                if (block.TryGetProperty("id", out var tid) && tid.ValueKind == JsonValueKind.String
                    && tid.GetString() is { Length: > 0 } toolId && !_emittedToolUseIds.Add(toolId))
                {
                    continue;
                }

                var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()!
                    : "tool";
                var summary = SummarizeToolInput(block);
                outputs.Add($"[工具调用] {name}{(summary.Length > 0 ? "：" + summary : string.Empty)}");
            }
        }
    }

    private static void EmitUser(JsonElement doc, List<string> outputs)
    {
        // 工具结果：只报状态，不展开大段内容
        if (doc.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var bt)
                    && bt.ValueKind == JsonValueKind.String
                    && bt.GetString() == "tool_result")
                {
                    var err = block.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    outputs.Add(err ? "[工具结果] 失败" : "[工具结果] 完成");
                }
            }
        }
    }

    private void EmitResult(JsonElement doc, List<string> outputs)
    {
        var isError = doc.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        FinalIsError = isError;

        if (doc.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
        {
            FinalResult = r.GetString();
        }

        if (isError)
        {
            outputs.Add($"[结果] 失败：{FinalResult ?? "(无详情)"}");
        }
        else if (!string.IsNullOrEmpty(FinalResult))
        {
            outputs.Add($"[结果] {FinalResult}");
        }

        if (doc.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
        {
            outputs.Add($"[费用] ${cost.GetDouble():F4}");
        }
    }

    private void EmitStreamEvent(JsonElement doc, List<string> outputs, string rawLine)
    {
        // --include-partial-messages 下，模型增量以 {"type":"stream_event","event":{...}} 实时推送。
        // 只翻译「有内容」的事件（正文增量 / 工具调用），控制类事件（message_start/stop、
        // content_block_stop 等）与工具入参片段（input_json_delta.partial_json）一律不外发原始 JSON，
        // 否则实时流会被一屏 {"partial_json":"..."} 噪声淹没（2026-10-22 用户反馈）。
        if (!doc.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
        {
            FlushPending(outputs);
            return;
        }

        var evType = ev.TryGetProperty("type", out var et) && et.ValueKind == JsonValueKind.String
            ? et.GetString()
            : null;

        switch (evType)
        {
            case "content_block_start":
                EmitBlockStart(ev, outputs);
                return;
            case "content_block_delta":
                EmitBlockDelta(ev, outputs);
                return;
            case "content_block_stop":
                EmitBlockStop(outputs);
                return;
            case "message_start":
            case "message_delta":
            case "message_stop":
            case "ping":
                // 回合边界 / 心跳：无正文可读，静默丢弃（不改变正文相关状态）
                FlushPending(outputs);
                return;
            default:
                break;
        }

        // 无 event.type 或未知 event：先按正文增量兜底（兼容协议演进），抽不到文本时给简短事件标签，
        // 而不是把整行 JSON 甩给用户。注意必须先判正文增量再刷缓冲，否则连续增量会被逐条刷成碎行。
        if (TryGetDeltaText(ev, out var legacyText))
        {
            AppendText(legacyText, outputs);
            return;
        }

        FlushPending(outputs);
        TryEmitAnyText(doc, outputs);
        if (outputs.Count == 0)
        {
            outputs.Add($"[事件] {DescribeEvent(ev, evType, rawLine)}");
        }
    }

    /// <summary>把未知 stream_event 归成一个简短标签（优先 event.type，其次 event.delta.type）。</summary>
    private static string DescribeEvent(JsonElement ev, string? evType, string rawLine)
    {
        if (evType is { Length: > 0 })
        {
            return evType;
        }

        if (ev.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String
            && dt.GetString() is { Length: > 0 } deltaType)
        {
            return "delta:" + deltaType;
        }

        // 连类型都取不到：只留极短截断，便于现场判断协议是否演进，不刷屏
        return TextTruncate.Truncate(rawLine, 100, "…")!;
    }

    /// <summary>content_block_start：工具块开始输出可读进度行，并开始累积入参。</summary>
    private void EmitBlockStart(JsonElement ev, List<string> outputs)
    {
        FlushPending(outputs);

        if (!ev.TryGetProperty("content_block", out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var btype = block.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String
            ? bt.GetString()
            : null;

        if (btype == "tool_use")
        {
            _toolName = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()!
                : "tool";
            _toolInput.Clear();
            _toolInputTruncated = false;

            if (block.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } toolId)
            {
                _emittedToolUseIds.Add(toolId);
            }

            // 入参偶尔已内联在 start 里（非增量形态）；空对象 {} 不累积，否则会与后续 partial_json 拼成非法 JSON
            if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                && input.EnumerateObject().Any())
            {
                _toolInput.Append(input.GetRawText());
            }

            outputs.Add($"[工具调用] {_toolName}");
            return;
        }

        // text 块可能自带初始文本；thinking 等其他块不展示
        if (btype == "text"
            && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String
            && txt.GetString() is { Length: > 0 } initial)
        {
            AppendText(initial, outputs);
        }
    }

    /// <summary>content_block_delta：正文增量并入合并缓冲；工具入参片段只累积不外发。</summary>
    private void EmitBlockDelta(JsonElement ev, List<string> outputs)
    {
        if (!ev.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            FlushPending(outputs);
            return;
        }

        var deltaType = delta.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String
            ? dt.GetString()
            : null;

        if (deltaType == "input_json_delta")
        {
            // 工具入参的流式片段（partial_json）：累积后由 content_block_stop 汇成一行摘要
            AppendToolInput(delta);
            return;
        }

        if (TryGetDeltaText(ev, out var text))
        {
            AppendText(text, outputs);
            return;
        }

        // 思考 / 签名 / 引用等非正文增量：不展示，也不透传原始 JSON
        if (deltaType is "thinking_delta" or "signature_delta" or "citations_delta")
        {
            return;
        }

        FlushPending(outputs);
        if (!_unknownDeltaReported)
        {
            _unknownDeltaReported = true;
            outputs.Add(deltaType is { Length: > 0 }
                ? $"[事件] 未知增量：{deltaType}（同类已忽略）"
                : "[事件] 未知增量形态（同类已忽略）");
        }
    }

    /// <summary>content_block_stop：工具块结束时把累积入参解析成一行可读摘要。</summary>
    private void EmitBlockStop(List<string> outputs)
    {
        FlushPending(outputs);

        if (_toolName is null)
        {
            return;
        }

        var summary = _toolInputTruncated
            ? "(入参过长，已省略)"
            : SummarizeToolInputJson(_toolInput.ToString());

        _toolName = null;
        _toolInput.Clear();
        _toolInputTruncated = false;

        if (summary.Length > 0)
        {
            outputs.Add($"[工具参数] {summary}");
        }
    }

    /// <summary>累积工具入参片段（超限即放弃累积，避免大文件写入把内容全吃进内存）。</summary>
    private void AppendToolInput(JsonElement delta)
    {
        if (_toolInputTruncated
            || !delta.TryGetProperty("partial_json", out var pj) || pj.ValueKind != JsonValueKind.String
            || pj.GetString() is not { Length: > 0 } piece)
        {
            return;
        }

        if (_toolInput.Length + piece.Length > MaxToolInputCaptureChars)
        {
            _toolInputTruncated = true;
            _toolInput.Clear();
            return;
        }

        _toolInput.Append(piece);
    }

    /// <summary>取 event.delta.text 正文增量（text_delta 及无 type 的历史形态通用）。</summary>
    private static bool TryGetDeltaText(JsonElement ev, out string text)
    {
        text = string.Empty;
        if (ev.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String
            && txt.GetString() is { Length: > 0 } s)
        {
            text = s;
            return true;
        }

        return false;
    }

    /// <summary>正文增量并入合并缓冲，按换行或阈值刷成连贯行；同时标记已见增量以避免完整正文重复输出。</summary>
    private void AppendText(string text, List<string> outputs)
    {
        _sawPartialText = true;
        _pending.Append(text);

        var pendingStr = _pending.ToString();
        var nl = pendingStr.LastIndexOf('\n');
        if (nl >= 0)
        {
            // 遇到换行：刷出整行，余下部分继续累积
            outputs.Add(pendingStr.Substring(0, nl).TrimEnd('\r'));
            _pending.Clear();
            if (nl + 1 < pendingStr.Length)
            {
                // CA1846：StringBuilder.Append 有 ReadOnlySpan<char> 重载，免一次中间字符串分配
                _pending.Append(pendingStr.AsSpan(nl + 1));
            }
        }
        else if (_pending.Length >= CoalesceThreshold)
        {
            outputs.Add(pendingStr);
            _pending.Clear();
        }
    }

    private static void TryEmitAnyText(JsonElement doc, List<string> outputs)
    {
        // 兜底：CLI 协议演进/字段变动时，从未知类型事件中抽取短文本，避免实时流静默。
        foreach (var key in new[] { "text", "content", "message" })
        {
            if (doc.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString()!;
                    if (s.Length > 0 && s.Length <= 500)
                    {
                        outputs.Add(s);
                        return;
                    }
                }
                else if (v.ValueKind == JsonValueKind.Array)
                {
                    // content 为数组形态（如 {"content":[{"type":"text","text":"..."}]}）
                    if (TryExtractFromContentArray(v, outputs))
                    {
                        return;
                    }
                }
            }
        }

        foreach (var key in new[] { "event", "delta", "data" })
        {
            if (doc.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object)
            {
                foreach (var k2 in new[] { "text", "content", "message" })
                {
                    if (v.TryGetProperty(k2, out var v2))
                    {
                        if (v2.ValueKind == JsonValueKind.String)
                        {
                            var s = v2.GetString()!;
                            if (s.Length > 0 && s.Length <= 500)
                            {
                                outputs.Add(s);
                                return;
                            }
                        }
                        else if (v2.ValueKind == JsonValueKind.Array)
                        {
                            if (TryExtractFromContentArray(v2, outputs))
                            {
                                return;
                            }
                        }
                    }
                }

                // 再深一层：兼容 event.delta.content[] / event.content[] 等嵌套数组形态
                if (v.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object
                    && d.TryGetProperty("content", out var dc) && TryExtractFromContentArray(dc, outputs))
                {
                    return;
                }

                if (v.TryGetProperty("content", out var vc) && TryExtractFromContentArray(vc, outputs))
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 从 content 数组（形态为 [{"type":"text"|"input_text","text":"..."}] 或直接 [{"text":"..."}]）抽取文本。
    /// 兼容 CLI 协议演进后把增量正文放在 <c>event.delta.content</c> / <c>event.content</c> 等数组字段的情况。
    /// </summary>
    private static bool TryExtractFromContentArray(JsonElement content, List<string> outputs)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var any = false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString()!;
                if (s.Length > 0 && s.Length <= 500)
                {
                    outputs.Add(s);
                    any = true;
                }
            }
        }

        return any;
    }

    private static string SummarizeToolInput(JsonElement toolUse)
    {
        if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return SummarizeInputObject(input);
    }

    /// <summary>把 <c>input_json_delta</c> 累积出的入参 JSON 文本解析成一句话摘要（解析失败返回空）。</summary>
    private static string SummarizeToolInputJson(string json)
    {
        if (json.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.ValueKind == JsonValueKind.Object
                ? SummarizeInputObject(d.RootElement)
                : string.Empty;
        }
        catch (JsonException)
        {
            // 入参超长被放弃累积 / 流式中途结束时可能不是合法 JSON：放弃摘要，不影响任务
            return string.Empty;
        }
    }

    private static string SummarizeInputObject(JsonElement input)
    {
        // 优先取常见「一句话描述」字段
        foreach (var key in new[] { "command", "file_path", "description", "prompt", "path" })
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString() is { Length: > 0 } s)
            {
                return TextTruncate.Truncate(s, 120, "…")!;
            }
        }

        return TextTruncate.Truncate(input.GetRawText(), 120, "…")!;
    }

    /// <summary>
    /// 把增量合并缓冲中尚未刷出的部分作为一条行输出（在完整事件到达或任务结束时调用，避免末尾正文滞留）。
    /// </summary>
    private void FlushPending(List<string> outputs)
    {
        if (_pending.Length == 0)
        {
            return;
        }

        outputs.Add(_pending.ToString());
        _pending.Clear();
    }

    /// <summary>
    /// 任务结束时刷出残留增量（适配层在进程退出后调用），返回需转发的可读行。
    /// </summary>
    public IReadOnlyList<string> Flush()
    {
        var outputs = new List<string>();
        FlushPending(outputs);
        return outputs;
    }
}
