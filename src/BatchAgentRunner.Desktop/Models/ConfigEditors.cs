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
    private bool _mon = true;
    private bool _tue = true;
    private bool _wed = true;
    private bool _thu = true;
    private bool _fri = true;
    private bool _sat = true;
    private bool _sun = true;

    /// <summary>以领域候选模型初始化编辑包装，并解析其免费时段与折扣。</summary>
    public ModelEditor(ModelCandidate model)
    {
        Model = model;
        _name = model.Name;
        var window = model.FreeTimeWindow;
        var startText = "00:00";
        var endText = "23:59";
        if (window is not null)
        {
            _windowEnabled = true;
            startText = window.Start.ToString("HH:mm");
            endText = window.End.ToString("HH:mm");
            _mon = window.Days?.Contains(DayOfWeek.Monday) ?? true;
            _tue = window.Days?.Contains(DayOfWeek.Tuesday) ?? true;
            _wed = window.Days?.Contains(DayOfWeek.Wednesday) ?? true;
            _thu = window.Days?.Contains(DayOfWeek.Thursday) ?? true;
            _fri = window.Days?.Contains(DayOfWeek.Friday) ?? true;
            _sat = window.Days?.Contains(DayOfWeek.Saturday) ?? true;
            _sun = window.Days?.Contains(DayOfWeek.Sunday) ?? true;
        }

        // P2-15：解析类字段一律走 FieldEditor<T>（解析失败显式报错，不再静默置 null）
        UnitPrice = new FieldEditor<decimal?>("预估单价",
            model.EstimatedUnitPrice?.ToString() ?? string.Empty, FieldParsers.DecimalOrNull);
        Discount = new FieldEditor<decimal?>("折扣",
            model.Discount?.ToString() ?? string.Empty, FieldParsers.DecimalOrNull);
        // P-09：时段起止原本手写 TryParse，解析失败时 Apply() 会把已配置的时段静默抹掉 → 一并收敛为 FieldEditor<T>；
        // 且直接暴露给视图绑定（不再包 StartText/EndText 裸字符串桥接），文本变更经回调刷新 Preview。
        StartTime = new FieldEditor<TimeOnly?>("起始时刻", startText, FieldParsers.TimeOnlyOrNull, RefreshPreview);
        EndTime = new FieldEditor<TimeOnly?>("结束时刻", endText, FieldParsers.TimeOnlyOrNull, RefreshPreview);
    }

    /// <summary>被编辑的领域候选模型（名称 / 折扣 / 时段最终写回此对象）。</summary>
    public ModelCandidate Model { get; }

    /// <summary>预估单价（元/次）编辑字段（P2-15）。</summary>
    public FieldEditor<decimal?> UnitPrice { get; }

    /// <summary>折扣倍率编辑字段（P2-15）。</summary>
    public FieldEditor<decimal?> Discount { get; }

    /// <summary>免费时段起始编辑字段（P-09；非法输入不再静默丢弃已配置时段）。</summary>
    public FieldEditor<TimeOnly?> StartTime { get; }

    /// <summary>免费时段结束编辑字段（P-09；非法输入不再静默丢弃已配置时段）。</summary>
    public FieldEditor<TimeOnly?> EndTime { get; }

    /// <summary>候选模型名称（写回 Model.Name）。</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>是否启用免费时段限制；关闭表示全天可用。</summary>
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

    /// <summary>免费时段是否包含周一（仅在启用时段限制时生效）。</summary>
    public bool Mon { get => _mon; set => SetProperty(ref _mon, value); }
    /// <summary>免费时段是否包含周二（仅在启用时段限制时生效）。</summary>
    public bool Tue { get => _tue; set => SetProperty(ref _tue, value); }
    /// <summary>免费时段是否包含周三（仅在启用时段限制时生效）。</summary>
    public bool Wed { get => _wed; set => SetProperty(ref _wed, value); }
    /// <summary>免费时段是否包含周四（仅在启用时段限制时生效）。</summary>
    public bool Thu { get => _thu; set => SetProperty(ref _thu, value); }
    /// <summary>免费时段是否包含周五（仅在启用时段限制时生效）。</summary>
    public bool Fri { get => _fri; set => SetProperty(ref _fri, value); }
    /// <summary>免费时段是否包含周六（仅在启用时段限制时生效）。</summary>
    public bool Sat { get => _sat; set => SetProperty(ref _sat, value); }
    /// <summary>免费时段是否包含周日（仅在启用时段限制时生效）。</summary>
    public bool Sun { get => _sun; set => SetProperty(ref _sun, value); }

    /// <summary>折扣的展示形态（如「0.65x」），未配置折扣时为空串（对齐模型选择器的折扣列样式）。</summary>
    public string DiscountDisplay =>
        Discount.Value is { } d ? $"{d:0.00}x" : string.Empty;

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

    /// <summary>按当前开关与起止时刻构造免费时段；未启用或时刻非法时返回 null。</summary>
    public FreeTimeWindow? BuildWindow()
    {
        if (!WindowEnabled)
        {
            return null;
        }

        // 留空 = 未配置，非法 = 错误态：两者都不生成窗口，但都不允许抹掉已配置的时段（见 Apply）
        if (StartTime.Value is not { } start || EndTime.Value is not { } end)
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

    /// <summary>时段文本变更 → 刷新 <see cref="Preview"/>（P-09：由 <see cref="FieldEditor{TValue}"/> 回调驱动，取代原字符串桥接属性的手工通知）。</summary>
    private void RefreshPreview() => OnPropertyChanged(nameof(Preview));

    /// <summary>把编辑结果写回领域对象。</summary>
    public void Apply()
    {
        Model.Name = Name;
        // P-09：起止时刻非法时保留原时段——原实现 BuildWindow() 返回 null 会静默抹掉用户已配置的免费时段
        if (!StartTime.HasError && !EndTime.HasError)
        {
            Model.FreeTimeWindow = BuildWindow();
        }

        // P2-15：解析失败时保留原值（不再静默置 null），错误态经 FieldEditor.Error 对用户可见
        if (!UnitPrice.HasError)
        {
            Model.EstimatedUnitPrice = UnitPrice.Value;
        }

        if (!Discount.HasError)
        {
            Model.Discount = Discount.Value;
        }
    }
}

/// <summary>
/// 模型下拉项：模型名 + 已配置的折扣倍率（对齐模型选择器「名称 + 折扣」的样式）。
/// 折扣取自同名候选模型已配置的 <c>discount</c>，未配置则不显示。
/// </summary>
public sealed class ModelOption
{
    /// <summary>用模型名与折扣展示文本创建下拉项。</summary>
    public ModelOption(string name, string discountDisplay)
    {
        Name = name;
        DiscountDisplay = discountDisplay;
    }

    /// <summary>下拉项显示的模型名称（同时作为可编辑下拉的回填文本）。</summary>
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

    /// <summary>以领域兜底模型初始化包装（仅名称与预估单价可编辑）。</summary>
    public FallbackModelEditor(FallbackModel model)
    {
        Model = model;
        _name = model.Name;
        // P2-15：解析类字段一律走 FieldEditor<T>（解析失败显式报错，不再静默置 null）
        UnitPrice = new FieldEditor<decimal?>("预估单价",
            model.EstimatedUnitPrice?.ToString() ?? string.Empty, FieldParsers.DecimalOrNull);
    }

    /// <summary>被编辑的领域兜底模型。</summary>
    public FallbackModel Model { get; }

    /// <summary>预估单价（元/次）编辑字段（P2-15）。</summary>
    public FieldEditor<decimal?> UnitPrice { get; }

    /// <summary>兜底模型名称（写回 Model.Name）。</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>把名称与预估单价写回兜底模型；解析失败时保留原值。</summary>
    public void Apply()
    {
        Model.Name = Name;
        // P2-15：解析失败时保留原值（不再静默置 null），错误态经 FieldEditor.Error 对用户可见
        if (!UnitPrice.HasError)
        {
            Model.EstimatedUnitPrice = UnitPrice.Value;
        }
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

    /// <summary>以账号配置初始化编辑器，并包装其候选模型池与兜底模型池。</summary>
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

    /// <summary>被编辑的领域账号配置。</summary>
    public AccountConfig Account { get; }

    /// <summary>账号唯一标识（写回 Account.Id，供凭据与台账关联）。</summary>
    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    /// <summary>账号显示名（界面列表与报告中展示）。</summary>
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

    /// <summary>账号是否参与批处理与积分领取。</summary>
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

    /// <summary>向候选模型池追加一条默认名为 model-new 的条目。</summary>
    public IRelayCommand AddModelCommand { get; }

    /// <summary>从候选模型池移除指定模型条目。</summary>
    public IRelayCommand<ModelEditor> RemoveModelCommand { get; }

    /// <summary>把指定模型条目在候选池中上移一位。</summary>
    public IRelayCommand<ModelEditor> MoveModelUpCommand { get; }

    /// <summary>把指定模型条目在候选池中下移一位。</summary>
    public IRelayCommand<ModelEditor> MoveModelDownCommand { get; }

    /// <summary>向兜底模型池追加一条默认名为 paid-model 的条目。</summary>
    public IRelayCommand AddFallbackModelCommand { get; }

    /// <summary>从兜底模型池移除指定条目。</summary>
    public IRelayCommand<FallbackModelEditor> RemoveFallbackModelCommand { get; }

    /// <summary>把各字段与两个模型池写回账号配置；空池置 null 表示回退全局列表。</summary>
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
    /// <summary>收件人分隔符（CA1861：常量数组提为 static readonly）。</summary>
    private static readonly char[] RecipientSeparators = { ',', '\n', ';' };

    private NotificationChannel _channel;
    private bool _batchCompleted = true;
    private bool _batchBlocked = true;
    private bool _batchTerminated;
    private bool _taskFailed;
    private bool _needsAttention;
    private string _webhookUrl = string.Empty;
    private string _smtpHost = string.Empty;
    private string _smtpFrom = string.Empty;
    private string _smtpToText = string.Empty;
    private string _smtpCredentialRef = string.Empty;
    private string _quietHours = string.Empty;

    /// <summary>以结构化通知规则初始化编辑器，展开通道、触发事件与 SMTP 字段。</summary>
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
        // P2-15：解析类字段一律走 FieldEditor<T>（解析失败显式报错，不再静默回落 587）
        SmtpPort = new FieldEditor<int?>("SMTP 端口",
            rule.Smtp?.Port.ToString() ?? string.Empty, FieldParsers.IntOrNull);
        if (rule.Smtp is { } smtp)
        {
            _smtpHost = smtp.Host ?? string.Empty;
            _smtpFrom = smtp.From ?? string.Empty;
            _smtpToText = string.Join(", ", smtp.To);
            _smtpCredentialRef = smtp.CredentialRef ?? string.Empty;
        }
    }

    /// <summary>通知通道（桌面 / Webhook / SMTP），决定下方参数字段的可见性。</summary>
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

    /// <summary>可选通知通道列表（供通道下拉绑定）。</summary>
    public IReadOnlyList<NotificationChannel> Channels { get; } =
        new[] { NotificationChannel.Desktop, NotificationChannel.Webhook, NotificationChannel.Smtp };

    /// <summary>当前是否选择 Webhook 通道（控制 Webhook 地址栏可见性）。</summary>
    public bool IsWebhook => _channel == NotificationChannel.Webhook;

    /// <summary>当前是否选择 SMTP 通道（控制邮件参数区可见性）。</summary>
    public bool IsSmtp => _channel == NotificationChannel.Smtp;

    /// <summary>整批正常完成时是否推送通知。</summary>
    public bool BatchCompleted { get => _batchCompleted; set => SetProperty(ref _batchCompleted, value); }

    /// <summary>批次被阻塞时是否推送通知。</summary>
    public bool BatchBlocked { get => _batchBlocked; set => SetProperty(ref _batchBlocked, value); }

    /// <summary>批次被终止时是否推送通知。</summary>
    public bool BatchTerminated { get => _batchTerminated; set => SetProperty(ref _batchTerminated, value); }

    /// <summary>单个任务失败时是否推送通知。</summary>
    public bool TaskFailed { get => _taskFailed; set => SetProperty(ref _taskFailed, value); }

    /// <summary>任务需要人工介入时是否推送通知。</summary>
    public bool NeedsAttention { get => _needsAttention; set => SetProperty(ref _needsAttention, value); }

    /// <summary>Webhook 推送地址（仅 Webhook 通道生效）。</summary>
    public string WebhookUrl { get => _webhookUrl; set => SetProperty(ref _webhookUrl, value); }

    /// <summary>SMTP 服务器地址（留空则视为不配置邮件通道）。</summary>
    public string SmtpHost { get => _smtpHost; set => SetProperty(ref _smtpHost, value); }

    /// <summary>SMTP 端口编辑字段（P2-15；留空回落 587，非空且无法解析则显式报错）。</summary>
    public FieldEditor<int?> SmtpPort { get; }

    /// <summary>发件人地址（可留空，由服务器补默认值）。</summary>
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
                // P2-15：留空回落 587；无法解析时同样回落但 HasError 已对用户显式呈现（不再静默）
                Port = SmtpPort.Value ?? 587,
                From = string.IsNullOrWhiteSpace(_smtpFrom) ? null : _smtpFrom.Trim(),
                To = _smtpToText
                    .Split(RecipientSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
