using System.Collections.ObjectModel;
using BatchAgentRunner.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchAgentRunner.Desktop.Models;

/// <summary>候选模型编辑包装：把时段 / 星期过滤等结构化配置映射为可编辑的字符串与开关。</summary>
public sealed class ModelEditor : ObservableObject
{
    private string _name;
    private bool _windowEnabled;
    private string _startText = "00:00";
    private string _endText = "23:59";
    private bool _mon = true;
    private bool _tue = true;
    private bool _wed = true;
    private bool _thu = true;
    private bool _fri = true;
    private bool _sat = true;
    private bool _sun = true;
    private string _unitPriceText = string.Empty;
    private string _discountText = string.Empty;

    public ModelEditor(ModelCandidate model)
    {
        Model = model;
        _name = model.Name;
        if (model.FreeTimeWindow is { } w)
        {
            _windowEnabled = true;
            _startText = w.Start.ToString("HH:mm");
            _endText = w.End.ToString("HH:mm");
            var days = w.Days;
            _mon = days?.Contains(DayOfWeek.Monday) ?? true;
            _tue = days?.Contains(DayOfWeek.Tuesday) ?? true;
            _wed = days?.Contains(DayOfWeek.Wednesday) ?? true;
            _thu = days?.Contains(DayOfWeek.Thursday) ?? true;
            _fri = days?.Contains(DayOfWeek.Friday) ?? true;
            _sat = days?.Contains(DayOfWeek.Saturday) ?? true;
            _sun = days?.Contains(DayOfWeek.Sunday) ?? true;
        }

        _unitPriceText = model.EstimatedUnitPrice?.ToString() ?? string.Empty;
        _discountText = model.Discount?.ToString() ?? string.Empty;
    }

    public ModelCandidate Model { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool WindowEnabled
    {
        get => _windowEnabled;
        set
        {
            if (SetProperty(ref _windowEnabled, value))
            {
                OnPropertyChanged(nameof(Preview));
            }
        }
    }

    public string StartText
    {
        get => _startText;
        set
        {
            if (SetProperty(ref _startText, value))
            {
                OnPropertyChanged(nameof(Preview));
            }
        }
    }

    public string EndText
    {
        get => _endText;
        set
        {
            if (SetProperty(ref _endText, value))
            {
                OnPropertyChanged(nameof(Preview));
            }
        }
    }

    public bool Mon { get => _mon; set => SetProperty(ref _mon, value); }
    public bool Tue { get => _tue; set => SetProperty(ref _tue, value); }
    public bool Wed { get => _wed; set => SetProperty(ref _wed, value); }
    public bool Thu { get => _thu; set => SetProperty(ref _thu, value); }
    public bool Fri { get => _fri; set => SetProperty(ref _fri, value); }
    public bool Sat { get => _sat; set => SetProperty(ref _sat, value); }
    public bool Sun { get => _sun; set => SetProperty(ref _sun, value); }

    public string UnitPriceText
    {
        get => _unitPriceText;
        set => SetProperty(ref _unitPriceText, value);
    }

    /// <summary>折扣倍率（如 0.65 = 6.5 折），留空 = 不显示折扣。</summary>
    public string DiscountText
    {
        get => _discountText;
        set
        {
            if (SetProperty(ref _discountText, value))
            {
                OnPropertyChanged(nameof(DiscountDisplay));
            }
        }
    }

    /// <summary>折扣的展示形态（如「0.65x」），未配置折扣时为空串（对齐模型选择器的折扣列样式）。</summary>
    public string DiscountDisplay =>
        decimal.TryParse(DiscountText, out var d) ? $"{d:0.00}x" : string.Empty;

    /// <summary>时段预览（跨零点以「周日 22:00 → 周一 06:00」形式展示，REQ-003b）。</summary>
    public string Preview
    {
        get
        {
            if (!WindowEnabled)
            {
                return "全天可用";
            }

            var window = BuildWindow();
            return window?.Describe() ?? "时段无效";
        }
    }

    public FreeTimeWindow? BuildWindow()
    {
        if (!WindowEnabled)
        {
            return null;
        }

        if (!TimeOnly.TryParse(StartText, out var start) || !TimeOnly.TryParse(EndText, out var end))
        {
            return null;
        }

        var days = new List<DayOfWeek>();
        if (Mon) days.Add(DayOfWeek.Monday);
        if (Tue) days.Add(DayOfWeek.Tuesday);
        if (Wed) days.Add(DayOfWeek.Wednesday);
        if (Thu) days.Add(DayOfWeek.Thursday);
        if (Fri) days.Add(DayOfWeek.Friday);
        if (Sat) days.Add(DayOfWeek.Saturday);
        if (Sun) days.Add(DayOfWeek.Sunday);

        return new FreeTimeWindow(start, end, days.Count == 7 ? null : days);
    }

    /// <summary>把编辑结果写回领域对象。</summary>
    public void Apply()
    {
        Model.Name = Name;
        Model.FreeTimeWindow = BuildWindow();
        Model.EstimatedUnitPrice = decimal.TryParse(UnitPriceText, out var price) ? price : null;
        Model.Discount = decimal.TryParse(DiscountText, out var discount) ? discount : null;
    }
}

/// <summary>
/// 模型下拉项：模型名 + 已配置的折扣倍率（对齐模型选择器「名称 + 折扣」的样式）。
/// 折扣取自同名候选模型已配置的 <c>discount</c>，未配置则不显示。
/// </summary>
public sealed class ModelOption
{
    public ModelOption(string name, string discountDisplay)
    {
        Name = name;
        DiscountDisplay = discountDisplay;
    }

    public string Name { get; }

    /// <summary>折扣展示（如「0.65x」），空串 = 未配置折扣（下拉里不显示）。</summary>
    public string DiscountDisplay { get; }

    /// <summary>可编辑 ComboBox 选中项回填文本用。</summary>
    public override string ToString() => Name;
}

/// <summary>账号自有兜底模型编辑包装（REQ-004b，v1.12）。</summary>
public sealed class FallbackModelEditor : ObservableObject
{
    private string _name;
    private string _unitPriceText = string.Empty;

    public FallbackModelEditor(FallbackModel model)
    {
        Model = model;
        _name = model.Name;
        _unitPriceText = model.EstimatedUnitPrice?.ToString() ?? string.Empty;
    }

    public FallbackModel Model { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string UnitPriceText
    {
        get => _unitPriceText;
        set => SetProperty(ref _unitPriceText, value);
    }

    public void Apply()
    {
        Model.Name = Name;
        Model.EstimatedUnitPrice = decimal.TryParse(UnitPriceText, out var price) ? price : null;
    }
}

/// <summary>
/// 账号编辑包装（v1.12：候选模型池与兜底模型池挂在账号下，故事 2.1 / REQ-004b）。
/// </summary>
public sealed class AccountEditor : ObservableObject
{
    private string _id;
    private string _displayName;
    private string _credentialRef;
    private bool _enabled = true;
    private string _snapshotStatus = string.Empty;
    private bool _isFetchingModels;

    /// <summary>该账号实时拉取到的可用模型（配置页「拉取该账号可用模型」填充，供下拉选择用；含已配置折扣的展示）。</summary>
    public ObservableCollection<ModelOption> AvailableModels { get; } = new();

    /// <summary>该账号是否正在拉取可用模型（禁用重复点击 + 显示进度）。</summary>
    public bool IsFetchingModels
    {
        get => _isFetchingModels;
        set => SetProperty(ref _isFetchingModels, value);
    }

    public AccountEditor(AccountConfig account)
    {
        Account = account;
        _id = account.Id;
        _displayName = account.DisplayName;
        _credentialRef = account.CredentialReference;
        _enabled = account.Enabled;

        Models = new ObservableCollection<ModelEditor>(
            (account.ModelCandidates ?? new List<ModelCandidate>()).Select(m => new ModelEditor(m)));
        FallbackModels = new ObservableCollection<FallbackModelEditor>(
            (account.FallbackModels ?? new List<FallbackModel>()).Select(m => new FallbackModelEditor(m)));

        AddModelCommand = new RelayCommand(() =>
            Models.Add(new ModelEditor(new ModelCandidate { Name = "model-new" })));
        RemoveModelCommand = new RelayCommand<ModelEditor>(m => { if (m is not null) Models.Remove(m); });
        MoveModelUpCommand = new RelayCommand<ModelEditor>(m =>
        {
            if (m is null) return;
            var i = Models.IndexOf(m);
            if (i > 0) Models.Move(i, i - 1);
        });
        MoveModelDownCommand = new RelayCommand<ModelEditor>(m =>
        {
            if (m is null) return;
            var i = Models.IndexOf(m);
            if (i >= 0 && i < Models.Count - 1) Models.Move(i, i + 1);
        });
        AddFallbackModelCommand = new RelayCommand(() =>
            FallbackModels.Add(new FallbackModelEditor(new FallbackModel { Name = "paid-model" })));
        RemoveFallbackModelCommand = new RelayCommand<FallbackModelEditor>(m =>
        {
            if (m is not null) FallbackModels.Remove(m);
        });
    }

    public AccountConfig Account { get; }

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    /// <summary>仅存凭据引用（profile/env/keychain），不明文保存，PRD 第 8 章。</summary>
    public string CredentialRef
    {
        get => _credentialRef;
        set => SetProperty(ref _credentialRef, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>
    /// Vault 快照状态（DESIGN-账号凭证切换 §7：备份时间/最近验证/尚未备份）。
    /// 由配置页按 Vault 实况刷新，不属于配置内容。
    /// </summary>
    public string SnapshotStatus
    {
        get => _snapshotStatus;
        set => SetProperty(ref _snapshotStatus, value);
    }

    /// <summary>该账号自有的有序候选模型池（空 = 回退全局共享列表）。</summary>
    public ObservableCollection<ModelEditor> Models { get; }

    /// <summary>该账号自有的有序兜底模型池。</summary>
    public ObservableCollection<FallbackModelEditor> FallbackModels { get; }

    public IRelayCommand AddModelCommand { get; }

    public IRelayCommand<ModelEditor> RemoveModelCommand { get; }

    public IRelayCommand<ModelEditor> MoveModelUpCommand { get; }

    public IRelayCommand<ModelEditor> MoveModelDownCommand { get; }

    public IRelayCommand AddFallbackModelCommand { get; }

    public IRelayCommand<FallbackModelEditor> RemoveFallbackModelCommand { get; }

    public void Apply()
    {
        Account.Id = Id;
        Account.DisplayName = DisplayName;
        Account.CredentialReference = CredentialRef;
        Account.Enabled = Enabled;

        // 自有候选池：一条都没配 = 回退全局共享列表（v1.12 向后兼容语义）
        foreach (var editor in Models)
        {
            editor.Apply();
        }

        Account.ModelCandidates = Models.Count > 0
            ? Models.Select(m => m.Model).ToList()
            : null;

        foreach (var editor in FallbackModels)
        {
            editor.Apply();
        }

        Account.FallbackModels = FallbackModels.Count > 0
            ? FallbackModels.Select(m => m.Model).ToList()
            : null;
    }
}

/// <summary>
/// 通知规则编辑器（二期 REQ-018）：把结构化 NotificationRule 映射为可编辑的
/// 通道 / 触发事件多选 / Webhook / SMTP / 安静时段。
/// </summary>
public sealed class NotificationRuleEditor : ObservableObject
{
    private NotificationChannel _channel;
    private bool _batchCompleted = true;
    private bool _batchBlocked = true;
    private bool _batchTerminated;
    private bool _taskFailed;
    private bool _needsAttention;
    private string _webhookUrl = string.Empty;
    private string _smtpHost = string.Empty;
    private string _smtpPortText = "587";
    private string _smtpFrom = string.Empty;
    private string _smtpToText = string.Empty;
    private string _smtpCredentialRef = string.Empty;
    private string _quietHours = string.Empty;

    public NotificationRuleEditor(NotificationRule rule)
    {
        _channel = rule.Channel;
        _batchCompleted = rule.Triggers.Contains(NotifyTrigger.BatchCompleted);
        _batchBlocked = rule.Triggers.Contains(NotifyTrigger.BatchBlocked);
        _batchTerminated = rule.Triggers.Contains(NotifyTrigger.BatchTerminated);
        _taskFailed = rule.Triggers.Contains(NotifyTrigger.TaskFailed);
        _needsAttention = rule.Triggers.Contains(NotifyTrigger.NeedsAttention);
        _webhookUrl = rule.WebhookUrl ?? string.Empty;
        _quietHours = rule.QuietHours ?? string.Empty;
        if (rule.Smtp is { } smtp)
        {
            _smtpHost = smtp.Host ?? string.Empty;
            _smtpPortText = smtp.Port.ToString();
            _smtpFrom = smtp.From ?? string.Empty;
            _smtpToText = string.Join(", ", smtp.To);
            _smtpCredentialRef = smtp.CredentialRef ?? string.Empty;
        }
    }

    public NotificationChannel Channel
    {
        get => _channel;
        set
        {
            if (SetProperty(ref _channel, value))
            {
                OnPropertyChanged(nameof(IsWebhook));
                OnPropertyChanged(nameof(IsSmtp));
            }
        }
    }

    public IReadOnlyList<NotificationChannel> Channels { get; } =
        new[] { NotificationChannel.Desktop, NotificationChannel.Webhook, NotificationChannel.Smtp };

    public bool IsWebhook => _channel == NotificationChannel.Webhook;

    public bool IsSmtp => _channel == NotificationChannel.Smtp;

    public bool BatchCompleted { get => _batchCompleted; set => SetProperty(ref _batchCompleted, value); }

    public bool BatchBlocked { get => _batchBlocked; set => SetProperty(ref _batchBlocked, value); }

    public bool BatchTerminated { get => _batchTerminated; set => SetProperty(ref _batchTerminated, value); }

    public bool TaskFailed { get => _taskFailed; set => SetProperty(ref _taskFailed, value); }

    public bool NeedsAttention { get => _needsAttention; set => SetProperty(ref _needsAttention, value); }

    public string WebhookUrl { get => _webhookUrl; set => SetProperty(ref _webhookUrl, value); }

    public string SmtpHost { get => _smtpHost; set => SetProperty(ref _smtpHost, value); }

    public string SmtpPortText { get => _smtpPortText; set => SetProperty(ref _smtpPortText, value); }

    public string SmtpFrom { get => _smtpFrom; set => SetProperty(ref _smtpFrom, value); }

    /// <summary>收件人（逗号或换行分隔多个）。</summary>
    public string SmtpToText { get => _smtpToText; set => SetProperty(ref _smtpToText, value); }

    /// <summary>SMTP 凭据引用（credential_ref 风格，不存明文密码）。</summary>
    public string SmtpCredentialRef { get => _smtpCredentialRef; set => SetProperty(ref _smtpCredentialRef, value); }

    /// <summary>安静时段（本地时间 HH:mm-HH:mm），命中则不推送。</summary>
    public string QuietHours { get => _quietHours; set => SetProperty(ref _quietHours, value); }

    /// <summary>触发事件的人类可读摘要（列表行显示）。</summary>
    public string TriggersSummary
    {
        get
        {
            var names = new List<string>();
            if (_batchCompleted) names.Add("批完成");
            if (_batchBlocked) names.Add("批阻塞");
            if (_batchTerminated) names.Add("批终止");
            if (_taskFailed) names.Add("任务失败");
            if (_needsAttention) names.Add("待人工");
            return names.Count > 0 ? string.Join("、", names) : "（未选触发事件）";
        }
    }

    /// <summary>写回结构化规则。</summary>
    public NotificationRule Apply()
    {
        var triggers = new List<NotifyTrigger>();
        if (_batchCompleted) triggers.Add(NotifyTrigger.BatchCompleted);
        if (_batchBlocked) triggers.Add(NotifyTrigger.BatchBlocked);
        if (_batchTerminated) triggers.Add(NotifyTrigger.BatchTerminated);
        if (_taskFailed) triggers.Add(NotifyTrigger.TaskFailed);
        if (_needsAttention) triggers.Add(NotifyTrigger.NeedsAttention);

        SmtpConfig? smtp = null;
        if (_channel == NotificationChannel.Smtp && !string.IsNullOrWhiteSpace(_smtpHost))
        {
            smtp = new SmtpConfig
            {
                Host = _smtpHost.Trim(),
                Port = int.TryParse(_smtpPortText, out var p) ? p : 587,
                From = string.IsNullOrWhiteSpace(_smtpFrom) ? null : _smtpFrom.Trim(),
                To = _smtpToText
                    .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                CredentialRef = string.IsNullOrWhiteSpace(_smtpCredentialRef) ? null : _smtpCredentialRef.Trim()
            };
        }

        return new NotificationRule
        {
            Channel = _channel,
            Triggers = triggers,
            WebhookUrl = _channel == NotificationChannel.Webhook && !string.IsNullOrWhiteSpace(_webhookUrl)
                ? _webhookUrl.Trim()
                : null,
            Smtp = smtp,
            QuietHours = string.IsNullOrWhiteSpace(_quietHours) ? null : _quietHours.Trim()
        };
    }
}
