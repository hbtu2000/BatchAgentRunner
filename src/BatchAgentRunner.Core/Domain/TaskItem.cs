using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using BatchAgentRunner.Core.Serialization;

namespace BatchAgentRunner.Core.Domain;

/// <summary>
/// 任务项（PRD 第 10 章数据模型 + 附录 A 字段口径）。
/// 一期不实现二期预留字段（workflow / depends_on / max_loops / batch_size），
/// 反序列化时忽略未知字段以保证前向兼容。
/// </summary>
public sealed class TaskItem : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string? _workspace;
    private string _instruction = string.Empty;
    private TaskStatus _status = TaskStatus.Pending;
    private string? _result;
    private string? _modelUsed;
    private string? _accountUsed;
    private string? _expertUsed;
    private bool _fallbackUsed;
    private decimal? _fallbackCost;
    private int _retryCount;
    private string? _error;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private double _durationSeconds;

    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    /// <summary>工作空间：命名空间名或目录路径。三级覆盖：任务 &gt; 本批 &gt; 全局（REQ-003a）。</summary>
    [JsonPropertyName("workspace")]
    public string? Workspace
    {
        get => _workspace;
        set => SetField(ref _workspace, value);
    }

    [JsonPropertyName("instruction")]
    public string Instruction
    {
        get => _instruction;
        set => SetField(ref _instruction, value);
    }

    [JsonPropertyName("status")]
    public TaskStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    [JsonPropertyName("result")]
    public string? Result
    {
        get => _result;
        set => SetField(ref _result, value);
    }

    [JsonPropertyName("model_used")]
    public string? ModelUsed
    {
        get => _modelUsed;
        set => SetField(ref _modelUsed, value);
    }

    [JsonPropertyName("account_used")]
    public string? AccountUsed
    {
        get => _accountUsed;
        set => SetField(ref _accountUsed, value);
    }

    [JsonPropertyName("expert_used")]
    public string? ExpertUsed
    {
        get => _expertUsed;
        set => SetField(ref _expertUsed, value);
    }

    [JsonPropertyName("fallback_used")]
    public bool FallbackUsed
    {
        get => _fallbackUsed;
        set => SetField(ref _fallbackUsed, value);
    }

    [JsonPropertyName("fallback_cost")]
    public decimal? FallbackCost
    {
        get => _fallbackCost;
        set => SetField(ref _fallbackCost, value);
    }

    [JsonPropertyName("retry_count")]
    public int RetryCount
    {
        get => _retryCount;
        set => SetField(ref _retryCount, value);
    }

    [JsonPropertyName("error")]
    public string? Error
    {
        get => _error;
        set => SetField(ref _error, value);
    }

    [JsonPropertyName("started_at")]
    [JsonConverter(typeof(NullableDateTimeOffsetConverter))]
    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        set => SetField(ref _startedAt, value);
    }

    [JsonPropertyName("finished_at")]
    [JsonConverter(typeof(NullableDateTimeOffsetConverter))]
    public DateTimeOffset? FinishedAt
    {
        get => _finishedAt;
        set => SetField(ref _finishedAt, value);
    }

    /// <summary>耗时，单位为秒（v1.10 定稿）。</summary>
    [JsonPropertyName("duration")]
    public double DurationSeconds
    {
        get => _durationSeconds;
        set => SetField(ref _durationSeconds, value);
    }

    [JsonIgnore]
    public bool IsTerminal => Status is TaskStatus.Done or TaskStatus.Failed or TaskStatus.Interrupted;

    /// <summary>结果是否为「部分输出」（中断/超时保留的中间结果）。</summary>
    [JsonIgnore]
    public bool HasPartialResult => Result is not null && Result.StartsWith(PartialResultPrefix, StringComparison.Ordinal);

    public const string PartialResultPrefix = "[部分输出]";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Finish(TaskStatus status, DateTimeOffset finishedAt)
    {
        Status = status;
        FinishedAt = finishedAt;
        DurationSeconds = StartedAt.HasValue ? Math.Round((finishedAt - StartedAt.Value).TotalSeconds, 3) : 0;
    }

    public TaskItem Clone() => new()
    {
        Id = Id,
        Workspace = Workspace,
        Instruction = Instruction,
        Status = Status,
        Result = Result,
        ModelUsed = ModelUsed,
        AccountUsed = AccountUsed,
        ExpertUsed = ExpertUsed,
        FallbackUsed = FallbackUsed,
        FallbackCost = FallbackCost,
        RetryCount = RetryCount,
        Error = Error,
        StartedAt = StartedAt,
        FinishedAt = FinishedAt,
        DurationSeconds = DurationSeconds
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
