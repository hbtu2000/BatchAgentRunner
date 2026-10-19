# 技术设计文档（合并版）—— AI 批处理任务调度工具（Batch Agent Runner）

---

**文档负责人：** 工程
**状态：** 🟢 已合并发布（由一期设计 v1.1、二期设计 v1.0、账号凭证切换 v1.1、额度耗尽暂停续跑 v1.0 合并而成）
**最后更新：** 2026-10-19（§4.3 同步 P1-16 实例化修订；其余章节沿用 2026-10-17 合并版）
**版本：** 2.0

**上游需求文档：** [`docs/PRD-BatchAgentRunner-v2.0.md`](docs/PRD-BatchAgentRunner-v2.0.md)（原 PRD-AI批处理任务调度工具，内容版本 v1.12，已整理为需求总览 v2.0）

**本设计覆盖：** 一期 P0 全量 + P1 全量；二期全部方向（PRD 第 15.4 章 1–7）+ P2 需求 REQ-016/017/018 + 场景模板库 S1–S16；账号凭证切换专题；额度耗尽暂停续跑与暂停时长剔除专题。

**合并说明：** 本文档为单一权威技术设计源。原分散的设计文档（一期 / 二期 / 账号凭证切换 / 额度耗尽）已归档至 `docs/archive/`。

---

## 目录

- [第1部分：一期技术设计（原 DESIGN-BatchAgentRunner v1.1）](#第1部分一期技术设计原-design-batchagentrunner-v11)
  - [1. 设计目标与约束](#1-设计目标与约束)
    - [1.1 设计目标](#11-设计目标)
    - [1.2 硬约束](#12-硬约束)
  - [2. 技术选型](#2-技术选型)
  - [3. 总体架构](#3-总体架构)
    - [3.1 分层](#31-分层)
    - [3.2 项目与目录](#32-项目与目录)
  - [4. 领域模型](#4-领域模型)
    - [4.1 任务项 `TaskItem`（对齐 PRD 第 10 章数据模型 + 附录 A）](#41-任务项-taskitem对齐-prd-第-10-章数据模型--附录-a)
    - [4.2 批级运行态 `BatchRunState`](#42-批级运行态-batchrunstate)
    - [4.3 配置 `RunOptions`（关键默认值对齐 PRD）](#43-配置-runoptions关键默认值对齐-prd)
    - [4.4 账号配置 `AccountConfig`（v1.1：候选/兜底模型池挂账号下）](#44-账号配置-accountconfigv11候选兜底模型池挂账号下)
    - [4.5 免费时段 `FreeTimeWindow`](#45-免费时段-freetimewindow)
    - [4.6 冷却记录 `QuotaCooldownRecord`](#46-冷却记录-quotacooldownrecord)
  - [5. 核心算法](#5-核心算法)
    - [5.1 组合可用性评估 `AvailabilityEvaluator`](#51-组合可用性评估-availabilityevaluator)
    - [5.2 降级矩阵 `DegradationMatrix`](#52-降级矩阵-degradationmatrix)
    - [5.3 额度错误识别与重置时间解析](#53-额度错误识别与重置时间解析)
    - [5.4 引擎主循环 `BatchEngine`](#54-引擎主循环-batchengine)
    - [5.5 重试与上限（REQ-009a）](#55-重试与上限req-009a)
  - [6. Agent 适配层契约](#6-agent-适配层契约)
  - [7. 持久化与文件契约](#7-持久化与文件契约)
  - [8. 界面设计（Avalonia）](#8-界面设计avalonia)
    - [8.1 页面](#81-页面)
    - [8.2 六种批级状态视图](#82-六种批级状态视图)
    - [8.3 数据流](#83-数据流)
  - [9. 受控并发（P1，REQ-012）](#9-受控并发p1req-012)
  - [10. 自动关机（P1，REQ-015）](#10-自动关机p1req-015)
  - [11. M0 验证项与本设计的接口映射](#11-m0-验证项与本设计的接口映射)
  - [12. 测试策略](#12-测试策略)
  - [13. 里程碑](#13-里程碑)
  - [14. 风险与开放问题](#14-风险与开放问题)
- [第2部分：二期技术设计（原 DESIGN-BatchAgentRunner-二期 v1.0）](#第2部分二期技术设计原-design-batchagentrunner-二期-v10)
  - [1. 二期定位与一期基础](#1-二期定位与一期基础)
    - [1.1 定位](#11-定位)
    - [1.2 一期为二期预留的接口（已在代码层面落地）](#12-一期为二期预留的接口已在代码层面落地)
    - [1.3 设计原则（沿用一期 + 二期新增）](#13-设计原则沿用一期--二期新增)
  - [2. 分层与模块扩展](#2-分层与模块扩展)
    - [2.1 分层（在一期基础上新增/扩展，依赖方向不变：Desktop → Agents/Infrastructure → Core）](#21-分层在一期基础上新增扩展依赖方向不变desktop--agentsinfrastructure--core)
    - [2.2 配置域扩展（在 9 域基础上新增 3 域 → 共 12 域）](#22-配置域扩展在-9-域基础上新增-3-域--共-12-域)
  - [3. 领域模型扩展](#3-领域模型扩展)
    - [3.1 任务项 `TaskItem` 扩展](#31-任务项-taskitem-扩展)
    - [3.2 工作流 `WorkflowSpec`](#32-工作流-workflowspec)
    - [3.3 质量门禁 `QualityGate`](#33-质量门禁-qualitygate)
    - [3.4 任务依赖 `TaskDependency`（DAG）](#34-任务依赖-taskdependencydag)
    - [3.5 项目级引用 `ProjectRef`](#35-项目级引用-projectref)
    - [3.6 会话句柄 `SessionHandle`（断点恢复方案 b）](#36-会话句柄-sessionhandle断点恢复方案-b)
    - [3.7 模板 `TemplateSpec`（REQ-016）](#37-模板-templatespecreq-016)
    - [3.8 Diff 与通知模型（REQ-017/018）](#38-diff-与通知模型req-017018)
  - [4. 核心算法](#4-核心算法)
    - [4.1 阶段模板引擎（轻量）](#41-阶段模板引擎轻量)
    - [4.2 工作流执行 `WorkflowExecutor`](#42-工作流执行-workflowexecutor)
    - [4.3 质量门禁评估 `GateEvaluator`](#43-质量门禁评估-gateevaluator)
    - [4.4 DAG 调度 `DagScheduler`](#44-dag-调度-dagscheduler)
    - [4.5 断点会话恢复（方案 b，兼容方案 a）](#45-断点会话恢复方案-b兼容方案-a)
  - [5. 场景模板库（S1–S16）与原语](#5-场景模板库s1s16与原语)
    - [5.1 通用能力原语（PRD 15.1）](#51-通用能力原语prd-151)
    - [5.2 模板注册与自定义](#52-模板注册与自定义)
    - [5.3 内置场景模板定义（S1–S16）](#53-内置场景模板定义s1s16)
  - [6. Markdown 原地回写规范（二期新增，兼容一期 sidecar）](#6-markdown-原地回写规范二期新增兼容一期-sidecar)
    - [6.1 原地回写格式](#61-原地回写格式)
    - [6.2 兼容与迁移](#62-兼容与迁移)
  - [7. REQ-016 任务模板 / 变量批量生成](#7-req-016-任务模板--变量批量生成)
    - [7.1 展开机制 `TemplateExpander`](#71-展开机制-templateexpander)
    - [7.2 UI（TemplateEditorView）](#72-uitemplateeditorview)
  - [8. REQ-017 结果 diff 视图](#8-req-017-结果-diff-视图)
    - [8.1 采集 `IDiffCollector`](#81-采集-idiffcollector)
    - [8.2 落盘与展示](#82-落盘与展示)
  - [9. REQ-018 通知推送](#9-req-018-通知推送)
    - [9.1 通道与触发](#91-通道与触发)
    - [9.2 消息模板](#92-消息模板)
    - [9.3 安全](#93-安全)
  - [10. 适配层与持久化扩展](#10-适配层与持久化扩展)
    - [10.1 `AgentRequest` 扩展](#101-agentrequest-扩展)
    - [10.2 文件契约更新](#102-文件契约更新)
    - [10.3 报告扩展（REQ-011 二期增强）](#103-报告扩展req-011-二期增强)
  - [11. 界面设计扩展（Avalonia，沿用 UI 设计系统 v1.0 双主题/令牌）](#11-界面设计扩展avalonia沿用-ui-设计系统-v10-双主题令牌)
    - [11.1 配置页新增（域 10/11/12）](#111-配置页新增域-101112)
    - [11.2 监控页新增视图](#112-监控页新增视图)
    - [11.3 报告页新增](#113-报告页新增)
    - [11.4 新增独立视图](#114-新增独立视图)
    - [11.5 新增任务级状态视图](#115-新增任务级状态视图)
  - [12. 测试策略](#12-测试策略-1)
  - [13. 里程碑与风险](#13-里程碑与风险)
    - [13.1 里程碑（建议在 M4 打磨发布后启动）](#131-里程碑建议在-m4-打磨发布后启动)
    - [13.2 风险与缓解](#132-风险与缓解)
  - [14. 开放问题（待用户/M0 确认）](#14-开放问题待用户m0-确认)
- [第3部分：专题设计 A — 凭证备份与账号切换（原 DESIGN-账号凭证切换 v1.1）](#第3部分专题设计-a--凭证备份与账号切换原-design-账号凭证切换-v11)
  - [1. 背景与目标](#1-背景与目标)
  - [2. 本机侦查结论（2026-10-15 实测，Windows 11）](#2-本机侦查结论2026-10-15-实测windows-11)
  - [3. 核心模型：Live / Snapshot / Vault](#3-核心模型live--snapshot--vault)
  - [4. 目录与清单契约](#4-目录与清单契约)
    - [4.1 快照目录](#41-快照目录)
    - [4.2 manifest.json（snake_case，与项目约定一致）](#42-manifestjsonsnakecase与项目约定一致)
    - [4.3 Live 位置注册表（代码内 `CredentialLocationRegistry`）](#43-live-位置注册表代码内-credentiallocationregistry)
  - [5. 引导登录流程（登录成功 → 按账号备份）](#5-引导登录流程登录成功--按账号备份)
  - [6. 切换流程（把备份复制回 Live）](#6-切换流程把备份复制回-live)
  - [7. 与引擎 / 配置 / 界面的衔接](#7-与引擎--配置--界面的衔接)
  - [8. 安全设计](#8-安全设计)
  - [9. 约束、风险与开放问题](#9-约束风险与开放问题)
  - [10. M0 验证项细化（V-4 拆分）](#10-m0-验证项细化v-4-拆分)
  - [11. 测试要点](#11-测试要点)
- [第4部分：专题设计 B — 额度耗尽暂停续跑与暂停时长剔除（原 DESIGN-额度耗尽 v1.0）](#第4部分专题设计-b--额度耗尽暂停续跑与暂停时长剔除原-design-额度耗尽-v10)
  - [1. 需求（Requirements）](#1-需求requirements)
  - [2. 设计（Design）](#2-设计design)
    - [2.1 配置项（RunOptions）](#21-配置项runoptions)
    - [2.2 引擎判定（BatchEngine）](#22-引擎判定batchengine)
    - [2.3 暂停时长剔除](#23-暂停时长剔除)
    - [2.4 事件/日志文案](#24-事件日志文案)
    - [2.5 报告与界面](#25-报告与界面)
    - [2.6 边界与并发](#26-边界与并发)
  - [3. 测试计划（覆盖点）](#3-测试计划覆盖点)
  - [4. 影响面](#4-影响面)

---

## 第1部分：一期技术设计（原 DESIGN-BatchAgentRunner v1.1）

### 1. 设计目标与约束

#### 1.1 设计目标

| 目标 | 来源 | 设计落点 |
|------|------|---------|
| 无人值守跑完一批任务，结果完整回写、可断点续跑 | REQ-001/002/006/008 | 任务文件即状态源 + 原子回写 + 启动前备份 |
| 双维度降级「账号 × 自有候选模型池」应触发必触发（v1.1：模型池挂账号下） | REQ-003/004 | `DegradationMatrix` 各账号 × 自有模型池有序遍历 + 可用性过滤 |
| 免费时段感知（组合级时段：同一模型在不同账号可配不同时段，时段外跳过不计失败、不告警） | REQ-003b | `AvailabilityEvaluator`，时段随账号下模型项配置，时段外为独立不可用原因 |
| 额度重置时间感知（冷却期内不调用、不计尝试） | REQ-004c | `QuotaCooldownRegistry`，恢复时间 = max(重置, 下一时段开始) |
| 断点任务可用兜底模型收尾（每账号独立兜底池） | REQ-004b | `FallbackPolicy`，按「账号顺序 × 账号内兜底池顺序」选取，双重成本上限 |
| 实时可见、随时可停、停了可续 | REQ-007/008 | Avalonia MVVM + 引擎事件流 + 协作式取消 |
| 汇总报告、受控并发、失败即停、日志持久化、自动关机 | REQ-011/012/013/014/015 | P1 模块（本设计一并覆盖） |
| 工作空间数量上限 + 按工作空间串行并发 + 并行下统一账号切换 | REQ-019/020/021 | `RunOptions.MaxWorkspaces` + 调度器工作空间在飞计数与派发守卫（§9.1）+ 槽位冻结/全局派发暂停/统一切换状态机（§9.2） |

#### 1.2 硬约束

1. **M0 技术验证未完成**：WorkBuddy / CodeBuddy CLI 的 headless 参数、模型参数、额度错误特征、重置时间格式、用量数据均未实测（PRD 第 13 章 V-1~V-10）。因此适配层必须**契约先行、实现可插拔**，并内置**仿真适配器**让全链路在 M0 前即可验证。
2. **纯本地**：无云端同步、无 외부服务（PRD 第 3 章范围外）。
3. **安全**：凭据不明文落任务文件/日志；危险操作确认**永不自动批准**（REQ-002b）。
4. **状态一致性**：内存任务列表为唯一事实来源；单写者串行回写（REQ-012）。

---

### 2. 技术选型

| 维度 | 选型 | 理由 |
|------|------|------|
| 运行时 | .NET 10（已装 SDK 10.0.401） | 环境就绪；长期支持版本；跨平台 |
| 界面 | **Avalonia 12.1 + Fluent 主题**（用户选定） | 跨平台桌面壳，XAML + MVVM，与 C# 技术栈统一；PRD 第 16 章「Web UI vs 桌面壳」按用户决策取桌面壳 |
| MVVM 框架 | CommunityToolkit.Mvvm 8.4（`ObservableObject`/`RelayCommand`/`ObservableProperty` 源生成器） | 轻量、无侵入；Avalonia 官方模板自带 |
| DI | Microsoft.Extensions.DependencyInjection | 标准容器，便于测试替换与分层解耦 |
| 日志 | Microsoft.Extensions.Logging.Abstractions（Core 仅依赖抽象）+ Infrastructure 文件实现 | Core 不绑定具体实现 |
| 序列化 | System.Text.Json（源生成器友好、UTF-8、无第三方依赖） | 任务文件/配置/冷却记录/报告统一 |
| 测试 | xUnit + FluentAssertions 风格断言（用原生 Assert 亦可） | 标准 |
| 子进程 | System.Diagnostics.Process + 进程树回收 | REQ-008「无僵尸进程残留」 |

**不采用：** Blazor/ASP.NET Core（用户选桌面）；Electron/Tauri（PRD 范围外）；ReactiveUI（模板默认可换，Toolkit 更轻）。

---

### 3. 总体架构

#### 3.1 分层

```
┌──────────────────────────────────────────────────────────────┐
│  BatchAgentRunner.Desktop  (Avalonia, MVVM)                  │
│  配置页 / 执行监控页 / 报告页   ← 引擎事件（Observable）驱动    │
└───────────────────────────┬──────────────────────────────────┘
                            │ 引用
┌───────────────────────────▼──────────────────────────────────┐
│  BatchAgentRunner.Agents      │  BatchAgentRunner.Infrastructure│
│  IAgentCliAdapter             │  任务文件加载/回写（原子+备份）  │
│   WorkBuddyAdapter (骨架)     │  配置存储 / 冷却存储 / 运行日志  │
│   CodeBuddyAdapter (骨架)     │  汇总报告 / 自动关机             │
│   MockAdapter (仿真)          │                                 │
│   CliProcessRunner            │                                 │
└───────────────────────────┬──────────────────────────────────┘
                            │ 实现 Core 抽象
┌───────────────────────────▼──────────────────────────────────┐
│  BatchAgentRunner.Core                                        │
│  Domain（实体/枚举/值对象） · Scheduling（可用性/降级矩阵/额度）  │
│  Engine（BatchEngine 主循环 + 批级状态机） · Abstractions（端口）│
└──────────────────────────────────────────────────────────────┘
```

**依赖方向：** Desktop → Agents/Infrastructure → Core。Core **零外部实现依赖**（仅 Logging.Abstractions）。

#### 3.2 项目与目录

```
BatchAgentRunner.sln
├─ Directory.Build.props
├─ src/
│  ├─ BatchAgentRunner.Core/
│  │   ├─ Domain/        TaskItem, BatchRunState, AccountConfig（含自有候选/兜底模型池）,
│  │   │                 ModelCandidate, FreeTimeWindow, QuotaCooldownRecord, ExpertRef, Enums
│  │   ├─ Options/       RunOptions（全部可配置参数 + 默认值）
│  │   ├─ Scheduling/    AvailabilityEvaluator, DegradationMatrix,
│  │   │                 QuotaErrorClassifier, ResetTimeParser, RetryPolicy
│  │   ├─ Engine/        BatchEngine, BatchStateMachine, TaskExecutionContext
│  │   ├─ Events/        BatchEvent（任务/切换/等待/阻塞/兜底/终止…）
│  │   └─ Abstractions/  IAgentCliAdapter, ITaskFileStore, ICooldownStore,
│  │                     IRunLogger, IConfigStore, IReportWriter, IShutdownService
│  ├─ BatchAgentRunner.Agents/
│  │   ├─ Adapters/      WorkBuddyAdapter, CodeBuddyAdapter, MockAdapter, AdapterFactory
│  │   └─ Internal/      CliProcessRunner, DangerousOperationGuard, PromptClassifier
│  ├─ BatchAgentRunner.Infrastructure/
│  │   ├─ TaskFiles/     JsonTaskFileStore, MarkdownTaskParser, TaskFileWriter, BackupManager
│  │   ├─ Persistence/   JsonConfigStore, JsonCooldownStore, AtomicFileWriter
│  │   ├─ Logging/       FileRunLogger（运行日志，REQ-014）
│  │   ├─ Reporting/     JsonReportWriter + MarkdownReportWriter（REQ-011）
│  │   └─ System/        ShutdownService（REQ-015）
│  └─ BatchAgentRunner.Desktop/
│      ├─ Views/         MainWindow, ConfigView, MonitorView, ReportView, TaskDetailView
│      ├─ ViewModels/    MainViewModel, ConfigViewModel, MonitorViewModel, ReportViewModel…
│      ├─ Services/      EngineHost（DI 组装 + 事件桥接 UI 线程）
│      └─ Converters/    状态徽标颜色/耗时/倒计时格式化
└─ tests/BatchAgentRunner.Tests/  Core / Scheduling / TaskFiles / Engine
```

---

### 4. 领域模型

#### 4.1 任务项 `TaskItem`（对齐 PRD 第 10 章数据模型 + 附录 A）

| 字段 | 类型 | 说明 |
|------|------|------|
| `Id` | string | 稳定 ID（REQ-001a：缺失自动生成 `task-001`，重复/非法字符加载期报错） |
| `Workspace` | string? | 命名空间名或目录路径；三级覆盖：任务 > 本批 > 全局（REQ-003a） |
| `Instruction` | string | 完整指令（Markdown 多行合并，REQ-001） |
| `Status` | `TaskStatus` | Pending / Running / Done / Failed / Interrupted |
| `Result` | string? | 结果；部分完成时前缀「[部分输出]」（REQ-008） |
| `ModelUsed` / `AccountUsed` / `ExpertUsed` | string? | 实际执行者 |
| `FallbackUsed` | bool | 是否兜底完成 |
| `FallbackCost` | decimal? | 兜底花费（实测或估算，`CostIsEstimated` 标注口径） |
| `RetryCount` | int | 重试次数 |
| `Error` | string? | 失败归因（**不允许静默失败**） |
| `StartedAt` / `FinishedAt` | DateTimeOffset? | 时间元数据 |
| `DurationSeconds` | double | 秒（v1.10 定稿） |

二期预留字段（`workflow` / `depends_on` / `max_loops` / `batch_size`）**一期不建模、不落盘**，只在 JSON 反序列化时忽略未知字段以保证前向兼容（PRD 15.4）。

#### 4.2 批级运行态 `BatchRunState`

```csharp
enum BatchStatus { Idle, Running, Waiting, Blocked, Terminated, Completed }
```

- `Waiting` 为**独立批级状态**，启动预检等待与运行中等待同一语义（REQ-003b/004c/010）
- 运行态**不落任务文件**，随运行日志落盘；冷却记录单独持久化且重启有效
- REQ-019/020/021 引入的运行态扩展（`MaxWorkspaces`、`InFlightByWorkspace`、`InFlightWorkspaceCount`、`DispatchHalted`、`PendingSwitchTarget`、`FrozenSlots`）均为内存态、不落任务文件，定义与算法见 §9.1 / §9.2

#### 4.3 配置 `RunOptions`（关键默认值对齐 PRD）

| 配置 | 默认 | 来源 |
|------|------|------|
| `AgentKind` | WorkBuddy | REQ-002 |
| `DegradationStrategy` | `ModelFirst` | REQ-004（可切 AccountFirst） |
| `MaxRetriesPerTask`（普通重试） | 2 | REQ-009 |
| `RateLimitBackoffRetries`（临时限流重试） | 3，指数退避 | REQ-005 |
| `TaskTimeoutMinutes` | 10（标注假设） | 边缘情况 4 |
| `MaxAttemptsPerTask`（总尝试上限） | 10 | REQ-009a |
| `MaxTotalMinutesPerTask`（总时长上限） | 60 | REQ-009a |
| `MaxWaitMinutes`（最长可等待时长） | 60 | REQ-003b |
| `DefaultCooldownMinutes`（解析不到重置时间时） | 60 | REQ-004c |
| `Concurrency` | 1（串行） | REQ-012 |
| `MaxWorkspaces` | 10 | REQ-019 | 命名工作空间数量上限（JSON 字段 `max_workspaces`）；引擎不设置绝对硬上限，以配置值为唯一校验准绳；配置页建议对 UI 输入范围作合理限制（如 1–100） |
| `FallbackEnabled` | false | REQ-004b |
| `ForceRerun` | false | REQ-008 |
| `StopOnFailure` | false | REQ-013 |
| `AutoShutdownEnabled` / `ShutdownOnlyWhenZeroFailure` / `CountdownSeconds=60` | false / false / 60 | REQ-015 |
| `ResultMaxKB` | 100（超出截断并提示全量日志位置） | 第 8 章 |

#### 4.4 账号配置 `AccountConfig`（v1.1：候选/兜底模型池挂账号下）

```csharp
class AccountConfig
{
    string Id;                  // 稳定标识，冷却记录按它落盘
    string DisplayName;
    string CredentialRef;       // 凭据引用（profile:/env:/keychain:），不明文落地
    bool   Enabled = true;
    string? Note;
    List<ModelCandidate>? ModelCandidates;  // 该账号自有候选模型池（有序）；null → 回退全局共享列表
    List<FallbackModel>?  FallbackModels;   // 该账号自有兜底模型池（有序，不参与免费时段过滤）
}

class ModelCandidate
{
    string Name;                        // 账号内唯一；不同账号可同名（时段/单价各自独立）
    FreeTimeWindow? FreeTimeWindow;     // null = 全天可用
    decimal? EstimatedUnitPrice;
}

record FallbackModel(string Name, decimal? EstimatedUnitPrice);
```

要点（对齐 REQ-003/003b/004b，v1.12）：
- **模型池是账号的子配置**：降级矩阵 = 各启用账号 × 其自有候选池（不再是「账号池 × 全局列表」笛卡尔积）
- **免费时段天然是「账号 × 模型」组合级**——同一模型名（如 Hy4）在 A 账号可设 0:00–8:00、B 账号设 0:00–24:00
- 兼容策略：全局 `ModelCandidates` 保留为共享默认池，仅对未配置自有池的账号生效；`Fallback.Enabled / MaxCostPerTask / MaxCostPerBatch` 保持全局，约束所有账号的兜底行为
- 实现约束沿用项目约定：集合声明用具体 `List<T>`（非接口类型，避免 STJ 反序列化 `NotSupportedException`）；字段 snake_case；派生属性标 `[JsonIgnore]`

#### 4.5 免费时段 `FreeTimeWindow`

```csharp
record FreeTimeWindow(TimeOnly Start, TimeOnly End, IReadOnlySet<DayOfWeek>? Days);
```

判定规则（REQ-003b，`days` 按**时段覆盖日**）：
- 未配置 → 全天可用
- `Start == End` → 全天
- 不跨零点（`Start < End`）：当日 `Start ≤ now.TimeOfDay ≤ End`（**按分钟闭区间**，写 `End` 即包含该整分钟，下一分钟才离开时段）且 `now.DayOfWeek ∈ Days`
- 跨零点（`Start > End`，如 22:00–06:00 或 23:00 → 次日 07:59）：区间拆为「当日 Start → 次日 End」；`now` 落在任一半段即视为在时段内（同样按分钟闭区间，如 `23:00 → 07:59` 覆盖到次日 08:00 之前）；`Days` 检查**开始日与结束日**（覆盖任一日命中即生效，支持「周日 22:00 → 周一 06:00」）
- 下一可用开始时间 `NextStartAfter(now)`：用于 waiting 倒计时

#### 4.6 冷却记录 `QuotaCooldownRecord`

```csharp
record QuotaCooldownRecord(string AccountId, string ModelName,
                           DateTimeOffset ResetAt, CooldownSource Source);
enum CooldownSource { CliParsed, DefaultFallback }
```

- 键 = `账号 × 模型`；恢复后再次耗尽 → 新记录覆盖
- 不可靠重置时间（早于当前 / 无法解析）→ 降级默认冷却并标注 `DefaultFallback`（边缘情况 13）

---

### 5. 核心算法

#### 5.1 组合可用性评估 `AvailabilityEvaluator`

对组合 `(account, model)` 在时刻 `now` 求值（时段窗口取该账号下模型项自己的配置，v1.1）：

```
Evaluate(account, model, now):
   windowOut   = combo.Window != null && !combo.Window.Contains(now)   // combo.Window = account.ModelCandidates[i].FreeTimeWindow
   nextWindow  = windowOut ? model.Window.NextStartAfter(now) : null
   cooldown    = registry.TryGet(account, model, out resetAt) && resetAt > now
   resetAt     = cooldown ? resetAt : null

   available   = !windowOut && !cooldown
   restoreAt   = available ? now
                           : max(nextWindow ?? now, resetAt ?? now)   // 取较晚者（REQ-004c）
   reason      = available ? None
                           : (cooldown && resetAt >= (nextWindow ?? now) ? Cooldown : OutsideFreeWindow)
```

> **两种不可用原因严格分离**（PRD 亮点）：`OutsideFreeWindow`（预期调度行为）与 `Cooldown`（额度维度）分别导致 `model_window_skipped` / `model_cooldown_skipped` 两个不同事件，均**不计失败、不触发切换告警、不污染误切换率**。

#### 5.2 降级矩阵 `DegradationMatrix`

组合序列在引擎启动时一次性展开（配置变更即重建，REQ-004，v1.1 语义）：
- **ModelFirst（默认）**：`foreach account in 账号池（启用且有序）: foreach model in 该账号自有候选池（有序；未配置自有池则回退全局共享列表）`
- **AccountFirst**：按「模型名」聚合——`foreach model name（按各账号候选池中的首次出现顺序）: foreach account（仅取配置了该模型名的启用账号，按账号顺序）`

> 组合总数 = Σ 各账号自有候选池大小；同名模型在不同账号下视为**不同组合**（时段/单价/冷却均独立，冷却键仍为 `账号 × 模型`）。

选点逻辑 `NextAvailable(from, now)`：
1. 从 `from` 在序列中的位置往后找第一个 `Evaluate(...).Available` 的组合
2. 找不到则从头到 `from` 之前再找一轮（循环一次，避免过早 blocked）
3. 仍无 → 返回 `null`，由引擎进入「兜底 / waiting / blocked」判定

被跳过的组合**实时**记录 `SkipReason` 并发出事件（供界面 toast 与报告统计）。

#### 5.3 额度错误识别与重置时间解析

`QuotaErrorClassifier`（适配层归一化后的 `AgentFailure` 输入）：

| 类别 | 判据（模式库，可按 CLI 扩展） | 处理 |
|------|--------------------------|------|
| `QuotaExhausted` | quota / quota_exceeded / 额度已用尽 / out of credits / insufficient balance / billing hard limit / 402 … | 记录冷却 + 切换组合 |
| `RateLimited` | 429 / rate limit / too many requests / try again later | 指数退避 3 次重试**同一组合**，仍失败才升级切换 |
| `DangerousOperation` | 交互点命中危险黑名单 | 立即失败该任务，记录交互点 |
| `AuthExpired` | 未登录/凭据过期/401/403（非额度） | 停用该账号，切换下一账号 |
| `TaskError` | 其余非零退出 | 普通重试 2 次 → failed |
| `Timeout` | 超过 `TaskTimeoutMinutes` | 按失败处理，保留已产出部分输出 |

`ResetTimeParser` 归一化三类输入（REQ-004c / V-10）：
1. ISO 8601 绝对时间戳
2. 相对时长（`3h24m`、`in 2 hours`、`90s`、`120 分钟`）
3. HTTP `Retry-After`（秒数或 HTTP-date）

解析结果 `< now` 或超长异常 → `DefaultFallback`（默认冷却时长）。

#### 5.4 引擎主循环 `BatchEngine`

```
Start():
  预检（REQ-010）→ 加载任务（跳过 done，除非 ForceRerun）→ ID 快照校验（REQ-001a）
  → 备份任务文件 → Status = Running → 调度循环

调度循环（单任务视角，串行；并发见 §9）：
  while 有待处理任务 && 未终止:
      task = 下一个 pending/interrupted 任务
      ctx  = new TaskExecutionContext(task)   // 总尝试/总时长上限计时起点
      loop:
         受总上限约束检查（REQ-009a）→ 超限则 failed
         combo = matrix.NextAvailable(current, now)

         if combo == null:                       // 免费组合矩阵当前无可用
             if 任务未完成 && FallbackEnabled && 未超成本上限:
                 fallbackCombo = 第一个可用兜底组合   // 按「账号顺序 × 该账号兜底池顺序」遍历（v1.1）
                 if fallbackCombo != null:
                     用它执行（不受时段过滤）→ 成功则 done 并记 fallback_cost → 下一条
             earliest = 全部组合中最早 restoreAt
             if earliest <= now + MaxWaitMinutes:
                 Status = Waiting(earliest) → 倒计时（可取消；配置变更即重评提前退出）
                 到点 → 回到 loop；取消 → Blocked，当前任务 interrupted
             else:
                 Status = Blocked → 当前任务 interrupted（保留部分输出）→ 退出循环

         r = adapter.ExecuteAsync(task.Instruction, combo, expert, workspace, ct)
         switch r.Failure:
            None              → 写回 done → 下一条
            RateLimited       → 退避重试同一组合（≤3）→ 仍失败按 QuotaExhausted 升级
            QuotaExhausted    → 记录冷却（解析/默认）→ combo 前进 → 继续 loop（**同一任务，完整指令重发，不继承上下文**）
            AuthExpired       → 停用账号 → combo 前进
            DangerousOperation→ task failed（注明命中点）→ 下一条
            其它              → 重试（≤2）→ 仍失败 failed → 下一条
```

**终止（REQ-008）**：协作式 `CancellationToken` → adapter 回收子进程树 → 当前任务 `Interrupted`（**不写成功**）→ 全部落盘 → `Terminated`。支持「等待当前任务完成后再终止」。

**续跑**：以任务文件/sidecar 的 `status` 为准，`done` 跳过并计数「已跳过 N 条」；`Interrupted` 重跑时清除旧「部分输出」（旧值留运行日志）。

**批级终态**：全部任务到达终态 → `Completed`；存在 blocked/waiting 则不触发完成（影响自动关机条件，REQ-015）。

#### 5.5 重试与上限（REQ-009a）

单任务维护 `Attempts`（实际发起的调用次数，冷却跳过的组合**不计**）与 `TotalElapsed`；任一超限 → `failed`（错误信息注明超限类型）。退避等待计入总时长。

---

### 6. Agent 适配层契约

```csharp
public interface IAgentCliAdapter
{
    AgentKind Kind { get; }
    Task<PreflightResult> ValidateAsync(AccountConfig account, CancellationToken ct); // REQ-010 极简调用
    Task<AgentExecutionResult> ExecuteAsync(AgentRequest request, CancellationToken ct);
}

public record AgentRequest(
    string Instruction, AccountConfig Account, ModelCandidate Model,
    string? ExpertId, string WorkingDirectory, TimeSpan Timeout,
    AutoAnswerPolicy AutoAnswer,        // 默认选项映射 + 危险黑名单/白名单
    Action<string>? OnOutputChunk);      // 实时输出流（REQ-007）
```

`AgentExecutionResult`：`Succeeded` / `RawOutput` / `Failure(AgentFailureKind, Message, ResetAt?, Usage?)`。

**`CliProcessRunner`（Agents/Internal）职责：**
- 以 `RedirectStandardOutput/Input/Error` 启动子进程，`WorkingDirectory` = 任务工作空间（REQ-003a）
- 增量转发 stdout → `OnOutputChunk`（界面流式追加）
- **零人工介入（REQ-002a）**：优先 CLI 官方无人值守参数；检测「无输出增长超阈值」判定 stdin 等待；普通确认注入 `default_answer`，选择型交互（含编号选项）按 `default_choice_index` 自动选择对应选项；无法注入 → 超时失败，不挂起
- **危险操作防护（REQ-002b）**：`DangerousOperationGuard` 维护黑名单（删除文件 / 系统配置修改 / `git push --force` / `rm -rf` / 格式化 …）与白名单；**命中黑名单永不自动批准**，该任务标记 failed 并醒目标记日志（REQ-010b）
- **进程树回收**：终止/超时/崩溃时 kill 子进程树，无残留（第 8 章可靠性）

**`MockAdapter`（仿真）**：按脚本返回成功 / 限流 / 额度耗尽（可带重置时间）/ 危险操作 / 超时，用于端到端验证全部降级与等待逻辑，支撑 M0 前的开发自测。

**WorkBuddy/CodeBuddy Adapter 骨架**：参数拼装集中在 `CliCommandBuilder`（`-p/--prompt`、`--model`、`--output-format`、无人值守开关等），**真实参数与错误特征待 M0 实测后填充**，现阶段以配置项 `CliArgumentsTemplate` 外置，避免硬编码猜测。

---

### 7. 持久化与文件契约

| 内容 | 位置 | 说明 |
|------|------|------|
| JSON 任务文件 | 用户指定 `.json` | **原地原子回写**（临时文件 + `File.Move(overwrite:true)`） |
| Markdown 任务文件 | 用户指定 `.md` | **只读输入**；状态/结果写 sidecar `<name>.md.result.json`；续跑以 sidecar 为准 |
| 备份 | `<name>.backup-<yyyyMMddHHmmss>` | 每次启动前，保留最近 5 份（REQ-006） |
| 冷却记录 | `%LOCALAPPDATA%/BatchAgentRunner/cooldowns.json` | 重启有效（REQ-004c） |
| 配置 | `%LOCALAPPDATA%/BatchAgentRunner/config.json` | 凭据**不明文落地**为设计约束：账号仅存标识 + 凭据引用（profile/env/keychain 引用），日志脱敏 |
| 运行日志 | `<任务文件目录>/runs/run-<时间戳>.log` | task_started/finished、model_switched、account_switched、model_window_skipped、quota_reset_recorded、model_cooldown_skipped、fallback_used、batch_blocked/resumed/terminated（第 11 章） |
| 汇总报告 | `<任务文件目录>/report-<时间戳>.json/.md` | REQ-011：成败统计、账号/模型使用量、切换时间线、兜底花费、失败清单；切换原因四段区分 + 时段外/冷却累计等待时长 |

**原子写（`AtomicFileWriter`）**：写同目录临时文件 → `Flush(true)` → `File.Move(tmp, target, overwrite: true)`；失败保留临时文件不破坏原文件。

---

### 8. 界面设计（Avalonia）

#### 8.1 页面

1. **配置页**（分组，PRD 第 9 章 9 个配置域）：任务文件与执行工具 / 工作空间（多空间 CRUD + 全局默认 + 本批默认）/ 账号池（增删排序启停 + **每账号编排自有候选模型池与兜底模型池**：增删、拖拽排序、免费时段配置，跨零点以「周日 22:00 → 周一 06:00」预览；同一模型在不同账号可配不同时段）/ 全局兼容候选模型列表（仅对未配置自有池的账号生效）/ 专家或专家团 / 兜底全局开关与成本上限（单任务 + 单批，默认关）/ 危险操作白名单 / 并发·重试·超时·总上限·降级策略 / 自动关机（二次确认 + 倒计时说明）
2. **执行监控页（主页面）**：顶部总进度条 + 状态统计卡（pending/running/done/failed/interrupted + 批级状态 + 已跳过计数）；中部任务列表（状态徽标、当前账号/模型/专家/工作空间、兜底标识）；底部当前任务实时输出流；右侧/抽屉任务详情（指令 + 输出 + 结果 + 错误）；toast 区（切换/兜底/时段/冷却/危险操作提示）
3. **报告页**：汇总视图（P1，REQ-011）

#### 8.2 六种批级状态视图

`Idle`（空状态引导）· `Running` · `Waiting`（倒计时 + 取消等待，取消转 Blocked）· `Blocked`（提示下一恢复时间点，可续跑）· `Terminated`（可续跑 + 强制重跑）· `Completed`（含失败清单入口）

（附注：REQ-021 新增「派发已暂停（等待在飞任务结束并统一切换账号）」瞬时态提示，不新增批级状态枚举，仅在 `Running` 期间由监控页于「当前任务」区或批级状态栏展示，提示用户为何无新任务启动。）

#### 8.3 数据流

```
BatchEngine ──(BatchEvent)──▶ EngineHost(订阅)
                                  │ Dispatcher.UIThread.Post
                                  ▼
                         MonitorViewModel (ObservableCollection<TaskRowViewModel>)
                                  │ 绑定
                                  ▼
                         Avalonia View（≤2s 刷新：事件驱动，非轮询）
```
终止 / 续跑 / 取消等待 / 强制重跑 → ViewModel 命令 → Engine API。

---

### 9. 受控并发（P1，REQ-012）

- 默认 `Concurrency = 1`（串行）；>1 时并行派发
- **额度/冷却/时段状态共享**：`AvailabilityEvaluator` 与 `QuotaCooldownRegistry` 为线程安全单例（`lock` + 不可变快照），任一任务触发耗尽/时段边界后立即对所有任务生效
- **单写者**：`TaskFileWriter` 串行队列执行回写，内存状态为事实来源
- **终止顺序**：停止派发 → 逐一回收子进程 → 全部落盘 → 反馈已终止
- 界面：「当前任务」展示全部 running 卡片；账号/模型按任务分别展示

#### 9.1 按工作空间串行（REQ-020，精炼 REQ-012）

`REQ-012` 支持可配置并发数（默认 1 串行），并发任务共享「账号 × 模型」可用性视图（§5.1/§5.3）。本小节在其上追加一条**派发粒度约束：工作空间是序列化的基本单位**。完整需求与验收见变更单 REQ-020。

**不变式**
- 同一工作空间任意时刻至多 1 个 `running` 任务：`InFlightByWorkspace[w] ∈ {0,1}`。
- 并行度上界 = `min(Concurrency, 分布有 pending/interrupted 待派发任务的不同工作空间数)`；`Concurrency` 因此退化为并行度的**上限**而非保证值（语义对齐 REQ-012「全局在飞上限」，见变更单 REQ-020 开放问题 1）。
- 每个并行通道绑定一个独立工作空间、互不串扰；并行通道共享同一「当前活动账号」（接力 §9.2）。

**运行态扩展（落在 `BatchEngine` 调度器内存，不落任务文件）**
- `InFlightByWorkspace: Dictionary<string, bool>`：每个工作空间是否在飞。
- `InFlightWorkspaceCount`：`InFlightByWorkspace` 中为 `true` 的个数（= 当前并行通道数）。

**派发选择算法（替代 §5.4 主循环中的「下一个 pending 任务」选取，与 §5.4 的单任务执行 loop 正交）**
```
DistinctPendingWs = { t.Workspace | t 处于 pending/interrupted 且依赖已满足(DAG) 且未进入 waiting }
Cap = min(Concurrency, DistinctPendingWs.Count)
while 存在可派发任务 && !DispatchHalted && InFlightWorkspaceCount < Cap:
    candidates = { t | t.status ∈ {pending,interrupted}
                     && !InFlightByWorkspace[t.Workspace]
                     && 依赖已满足 }
    if candidates 为空: break
    pick = candidates.First()              // 同空间内严格 FIFO；多空间间按候选顺序
    InFlightByWorkspace[pick.Workspace] = true
    InFlightWorkspaceCount++
    Dispatch(pick)                          // 后台执行
    // 槽位释放：任务到达终态 或 进入 waiting/interrupted →
    //   InFlightByWorkspace[t.Workspace] = false; InFlightWorkspaceCount--
```
- 边界：`Concurrency=1` 或仅 1 个工作空间 → 退化为全局串行；`K>Concurrency`（K = 不同工作空间数）→ 仅前 `Concurrency` 个工作空间先跑，其余排队，待槽位释放再补位。有效并行度可能低于 `Concurrency`，属按空间串行的固有代价（变更单 REQ-020 边界情形表）。
- DAG（`depends_on`）阻塞的任务未被派发，不占工作空间在飞槽位，与上述规则自然兼容（REQ-020 开放问题 2）。
- REQ-012 既有条款——额度/冷却/时段状态共享、单写者串行回写、终止顺序——**保持不变**。

**界面（监控页）**：「当前任务」卡片增加「所属工作空间」列；配置页并发设置 ToolTip 注明「并发按工作空间串行：同空间始终串行，不同空间可并行」。

#### 9.2 并行下统一账号切换（REQ-021，精炼 REQ-012 + 第3部分 §6）

`Concurrency>1` 时多个 CLI 子进程同时读同一 Live 凭证（第3部分 §3）；若中途即时切换账号，会造成「部分进程旧身份、部分新身份」混用，破坏额度归集与用量统计语义。本小节把账号切换收敛到「当前在飞任务全部结束后」这一边界，引入**槽位冻结 + 全局派发暂停 + 延后统一切换**。完整需求、验收与审查修复见变更单 REQ-021（含 F-1/F-2/F-3/F-4/F-7）。

**运行态扩展（落在调度器内存，不落任务文件）**
- `DispatchHalted: bool`：全局派发暂停标志（接入 §9.1 派发循环的 `!DispatchHalted` 守卫）。
- `PendingSwitchTarget: AccountId?`：冻结时记录的「后续任务统一切换目标账号」。
- `FrozenSlots: HashSet<TaskId>`：处于冻结态（仍在原账号走兜底、未结束）的任务集合。

**切换触发与冻结流程**
1. **约束前提**：所有在飞任务共享同一「当前活动账号」；任一在飞任务的中途账号需求（额度耗尽晋级下一账号 / AuthExpired 需换账号）**不即时切换**。
2. 某在飞任务触发切换需求时：
   - **冻结该在飞槽位**：`FrozenSlots.Add(task.Id)`；该任务继续在**原启动账号**上跑。
   - **进入全局派发暂停**：`DispatchHalted = true`；见 §9.1 派发循环，`!DispatchHalted` 不满足即不再派发任何新任务——即便其它工作空间的槽位空闲也不再填充，从源头消除切换窗口内「新任务以旧账号启动」的竞态。
   - **原账号兜底收尾**：在该任务的**原启动账号**上触发付费兜底（REQ-004b）完成本条任务；**兜底仅限于原启动账号自身的 `accounts[].fallback_models` 兜底池，不得跨账号**（否则违背「不切换 Live 凭证」，与 REQ-004b 全局「账号顺序 × 账号内兜底池顺序」选取冲突，见变更单 F-2）——原账号兜底池无可用组合或已超成本上限时，按 REQ-004b 转入 `waiting`/`blocked`。
   - **记录统一目标账号**：`PendingSwitchTarget = DegradationMatrix.NextAvailable(currentActiveAccount, now)`；因所有在飞共享同一账号，多任务并行冻结只产生唯一目标（见下「目标唯一性」）。
3. **统一切换**：当**所有在飞任务结束**（在飞数 = 0；已进入 `waiting`/`interrupted` 的任务不计入在飞、不阻塞切换）后，调度器调用**一次**第 3 部分 §6 切换流程（含 ② 保活回写 + ③ 原子覆盖）至 `PendingSwitchTarget`；随后 `DispatchHalted=false`，清空 `FrozenSlots`/`PendingSwitchTarget`，后续任务以目标账号并行派发。调用瞬间无运行中进程，天然满足 §6 ① 前置互斥「无运行中进程才允许切换」。

**目标唯一性**：规则要求「所有在飞任务始终共享同一活动账号」，故任一在飞任务冻结时 `NextAvailable(active)` 均相同 → 多任务并行冻结只产生唯一 `PendingSwitchTarget`，不存在目标冲突。

**降级 / 全不可用**
- 切换后的目标账号 B 自身亦不可用（模型耗尽或时段外）→ 后续任务在 B 上触发兜底并依降级矩阵标注下一目标（如 C）。
- **全部账号均不可用**且兜底不可用/超限 → 按 REQ-004b 转入 `waiting`/`blocked`（遵循 REQ-003b 时段规则 / REQ-004c 冷却规则）；整个过程**切换次数上界 = 启用账号数**（每个账号至多作为一次切换目标），**不无限循环切换**。
- `Concurrency=1` 或仅 1 个工作空间（退化为串行）时，上述机制退化为 §6 的「任务边界切换」，本小节不额外约束。

**手动切换禁用（衔接第 3 部分 §7）**：用户手动「切换到此账号」在 `in-flight>0`（含派发暂停期间）时**禁用**；仅当 `in-flight=0`（含派发暂停自然结束）或串行态可操作，且立即执行（同 §6）并清除 `PendingSwitchTarget`。

**埋点 / 报告（须同步 REQ-011 汇总报告与第 11 章埋点表，见变更单 F-1）**
- 新增埋点事件：`slot_frozen`（冻结发生，附原账号 / 任务 id）、`account_switch_deferred`（延后标记，附目标账号）。
- `account_switched` 时间线条目新增布尔属性 `deferred_switch = true`，表示本次为并发下延后统一切换，区分于常规任务边界切换；原 `account_switched` 事件其余语义保持不变。

**一致性**：不影响降级矩阵、额度冷却登记、兜底（除上「原账号兜底池」约束）、凭证快照/Vault；通过 §6 互斥从源头消除并发账号混用竞态。

**风险（吞吐坍塌，见变更单 F-7）**：冻结 → 统一切换窗口内，并行度坍塌为在飞任务（含冻结任务）单独执行，不再派发新任务；若冻结任务长耗时，会显著损失并行度。监控页须在「当前任务」区或批级状态栏展示「派发已暂停（等待在飞任务结束并统一切换账号）」状态（见 §8.2 批级状态视图附注）；「暂停期间仍可在原账号 A 上继续派发同账号新任务」属可选折中，本文暂不采用，待评审。

---

### 10. 自动关机（P1，REQ-015）

- 触发条件：批级 `Completed`（全部任务到达终态，无 blocked/waiting）且未被终止；默认允许带 failed 关机，勾选「仅失败率为 0」时存在 failed 则阻止
- 二次确认 + **60 秒可撤销倒计时**（界面「取消关机」按钮）
- 顺序硬约束：**先全部落盘并回收子进程 → 再关机**
- Windows：`shutdown /s /t 0`（权限不足 → 降级提示手动关机并记录）；macOS：AppleScript `shut down`
- 未触发时在界面与报告标注原因（边缘情况 7）
- **实现状态（2026-10-17 补齐，技术债务 P0-10）**：原实现判定条件后直接执行关机指令，跳过可撤销倒计时。
  现在策略下沉到 `Core/Engine/AutoShutdownCoordinator`（条件判定 + 倒计时委托），
  Desktop 以 `Views/ShutdownCountdownWindow` 提供倒计时窗口（剩余秒数 +「立即关机」/「取消关机」），
  只有「倒计时走完未被取消」才调用 `IShutdownService.TryShutdownAsync()`。
  倒计时在应用内计时而非 OS 级延时：进程不在时默认**不关机**（失效即保护），因此系统命令仍是 `/t 0` 立即执行。
  任何异常/被取消/窗口不可用 → `ShutdownOutcome.Executed = false` 并把原因写进报告。

---

### 11. M0 验证项与本设计的接口映射

| M0 项 | 影响的本设计落点 | 未验证时的设计兜底 |
|-------|----------------|-------------------|
| V-1 headless 调用与输出捕获 | `CliCommandBuilder` / `CliProcessRunner` | 参数模板外置为配置，不硬编码 |
| V-2 模型参数 | `CliCommandBuilder` | 配置 `CliArgumentsTemplate` 占位 |
| V-3 额度错误特征 | `QuotaErrorClassifier` 模式库 | 模式库配置化 + 手动标记额度耗尽入口 |
| V-4 多账号切换 | `AccountConfig` 凭据策略 | 账号仅存标识 + 凭据引用，切换策略可替换；**凭据备份/切换专项设计见 `docs/DESIGN-BatchAgentRunner-v2.0.md`（凭证快照 Vault 方案）** |
| V-5 专家参数 | `AgentRequest.ExpertId` | 不支持时降级为 prompt 注入角色描述 |
| V-6 无人值守交互覆盖 | `CliProcessRunner` + `PromptClassifier` | stdin 等待检测；确认型注入默认应答，选择型按索引自动选默认项 |
| V-7 用量/费用数据 | `AgentUsage` / `FallbackCost` | 不可得 → 调用次数 × 预估单价，报告标注「估算」 |
| V-8 会话恢复 | 一期不依赖 | 一律「完整指令重新执行」（方案 a） |
| V-9 危险操作默认项 | `DangerousOperationGuard` 黑白名单 | 黑名单命中一律拒绝自动批准（保守默认） |
| V-10 重置时间格式 | `ResetTimeParser` | 不可得 → 默认冷却时长（可配）并标注来源 |

---

### 12. 测试策略

| 层 | 用例 |
|----|------|
| `FreeTimeWindow` | 跨零点（22:00–06:00）边界、星期覆盖日（周日 22:00 → 周一 06:00 命中 mon）、未配置=全天、`NextStartAfter` |
| `QuotaCooldownRegistry` | 恢复时间 = max(重置, 下一时段开始)；恢复后覆盖；持久化往返 |
| `DegradationMatrix` | ModelFirst / AccountFirst 遍历顺序（含账号自有池展开与全局列表回退）；同名模型不同账号不同时段分别评估；时段外与冷却组合被跳过；冷却跳过不计尝试 |
| `FallbackPolicy` | 按「账号顺序 × 账号内兜底池顺序」选取第一个可用兜底组合；全局开关/成本上限约束所有账号兜底池；兜底组合不参与时段过滤 |
| `ResetTimeParser` | ISO / 相对时长 / Retry-After / 不可靠值降级 |
| `QuotaErrorClassifier` | 额度 vs 限流 vs 认证 vs 危险操作 |
| `MarkdownTaskParser` | `## id` + `- instruction:` 多行合并至下一二级标题；缺失 ID 生成；重复 ID 报错定位 |
| `TaskFileWriter` | 原子写、备份保留 5 份、sidecar 回写不改动 md 原文 |
| `BatchEngine`（MockAdapter） | 端到端：额度耗尽自动切换、全矩阵耗尽→兜底、全冷却→waiting→恢复续跑、终止→interrupted→续跑跳过 done、危险操作→failed 不阻塞 |
| `BatchEngine` 工作空间串行（REQ-020） | MockAdapter：单空间无论 `Concurrency` 多高均串行；多空间并行度 = min(Concurrency, 空间数) 且同空间不出现两个 running；K>Concurrency 时排队补位；DAG 阻塞任务不占槽位 |
| `BatchEngine` 并行统一切换（REQ-021） | MockAdapter：在飞任务额度耗尽→槽位冻结 + 全局派发暂停 + 原账号兜底完成；其余在飞结束后统一切换一次至目标账号；冻结期间无新进程以旧账号启动；`deferred_switch` 标记正确；全部不可用→waiting/blocked 不无限循环 |
| 工作空间上限（REQ-019） | 默认 10 时第 11 个新增被拦截；调小配置值后以配置值为准拦截；手工 `config.json` 超限 → 预检告警并拦截启动、不静默截断 |

---

### 13. 里程碑

| 里程碑 | 内容 | 依赖 |
|-------|------|------|
| D0 | 本设计 + 项目骨架 + Core 领域/调度算法 + 单元测试 | — |
| D1 | Agents 适配层（含 Mock）+ 引擎主循环贯通 | D0 |
| D2 | Infrastructure 持久化（回写/备份/冷却/日志/报告/关机） | D0 |
| D3 | Avalonia 界面三页 + 六种状态 | D1/D2 |
| D4 | M0 实测 CLI → 填充真实 adapter 参数与错误模式库 → 自用验证（3 批 × ≥50 条） | M0 |

---

### 14. 风险与开放问题

| 风险 | 设计缓解 |
|------|---------|
| CLI 能力未知导致返工 | 适配层契约先行 + 仿真适配器 + 参数/错误模式库外置配置 |
| 额度误判（限流当耗尽） | 限流先退避 3 次；模式库可迭代；误切换率可在报告中统计 |
| 自动批准放大破坏 | 危险黑名单命中即失败；全量自动应答日志（REQ-010b） |
| 子进程残留 | 进程树管理 + 超时兜底 + 终止流程统一回收 |
| 回写冲突 | 单写者串行；外部修改仅提示不合并 |
| 系统时间跳变导致时段误判 | 以 wall-clock 为准，恢复/唤醒后立即重评；时段外跳过不计失败 |
| 界面刷新延迟 | 事件驱动（非轮询），UI 线程批量合并刷新 |
| REQ-021 派发暂停吞吐坍塌 | 冻结→统一切换窗口内并行度坍塌为在飞任务单独执行；长耗时冻结任务显著损失并行度 | 监控页明确展示「派发已暂停」状态；如确需缓解可评审「暂停期同账号续派」折中（默认不采用） |

**开放问题（待 M0 或用户确认）：** 单任务超时/总上限/最长等待/默认冷却四个默认值需真实任务校准（PRD 第 16 章）；跨零点时段+星期过滤的极端组合（如 `days` 只含某天且跨零点）以「覆盖任一日命中」为准已在 §4.5 定义。

## 第2部分：二期技术设计（原 DESIGN-BatchAgentRunner-二期 v1.0）

### 1. 二期定位与一期基础

#### 1.1 定位

一期把「一条指令」无人值守跑完、多账号降级、免费时段/额度感知、实时可视与断点续跑都做扎实了。二期把**任务对象从「一条指令」升级为「一个软件工程流程」**：

| 维度 | 一期 | 二期 |
|------|------|------|
| 任务单元 | 单条 `instruction` | 单条指令 **或** 多阶段工作流 `workflow` |
| 执行模型 | 单次 agent 调用 + 重试/降级 | 阶段有序执行 + 阶段产出传递 + 质量门禁循环 |
| 任务关系 | 扁平列表、顺序/串行 | 支持 `depends_on` 依赖图（DAG 拓扑调度） |
| 结果消费 | 文本结果 + 报告 | 文本结果 + **diff 视图**（代码修改类）+ 报告 |
| 任务来源 | 手写任务文件 | 手写 **或** 模板/变量批量展开（REQ-016） |
| 流程沉淀 | — | **场景模板库 S1–S16**（开箱即用工作流） |
| 无人值守闭环 | 结束/阻塞需人工查看 | 结束/阻塞 **主动通知**（REQ-018） |
| 会话语义 | 切换/兜底后完整指令重发（方案 a） | 若 CLI 支持会话恢复，升级为**断点恢复**（方案 b，兼容回退方案 a） |
| Markdown 契约 | 只读输入 + sidecar 结果 | 支持**原地回写**（可选，向后兼容 sidecar） |

#### 1.2 一期为二期预留的接口（已在代码层面落地）

- 任务数据模型预留字段 `workflow` / `depends_on` / `max_loops` / `batch_size`：**一期反序列化已忽略未知字段**，二期直接启用，无需改反序列化器（仅需放开建模）。
- 适配层 `IAgentCliAdapter.ExecuteAsync(AgentRequest, ct)`：**已按「一次任务多次连续调用」设计**；二期新增「每阶段独立会话」「可选会话恢复」能力，不破坏既有签名（通过 `AgentRequest.Session` 可选字段扩展）。

#### 1.3 设计原则（沿用一期 + 二期新增）

沿用一期所有硬约束与「预期行为 ≠ 错误」「防误操作优先」「状态永远可见」原则；二期新增：

1. **工作流内失败可局部重试、不连坐**：单阶段失败按该阶段策略处理（重试/降级/门禁循环），不立即终止整批。
2. **阶段产出显式传递、可审计**：阶段间数据通过受控的上下文变量 `{output:stageId}` 引用，禁止隐式共享运行会话状态（防串扰、省 tokens）。
3. **门禁不通过 ≠ 错误消失**：门禁失败产出「遗留问题清单」，任务状态进入 `NeedsAttention`（新增任务级状态，见 §3.1），不污染「失败率」统计但单独可见。
4. **模板与手写等价**：模板展开后的产物与手写任务文件字节级等价，不引入专属运行时格式。

---

### 2. 分层与模块扩展

#### 2.1 分层（在一期基础上新增/扩展，依赖方向不变：Desktop → Agents/Infrastructure → Core）

```
BatchAgentRunner.Core
 ├─ Domain/        新增 WorkflowStage, QualityGate, TaskDependency, ProjectRef,
 │                 TemplateSpec, DiffRecord, NotificationRule, SessionHandle
 ├─ Scheduling/    新增 WorkflowExecutor（阶段循环+门禁）、DagScheduler（拓扑）、
 │                 GateEvaluator、TemplateExpander、SessionResumePolicy
 ├─ Engine/        BatchEngine 接入 DagScheduler + WorkflowExecutor；批级状态机扩展
 └─ Abstractions/  新增 INotifier, IGitIntegration, IAgentSessionStore,
                   IWorkflowTemplateStore, IDiffCollector

BatchAgentRunner.Agents
 └─ Adapters/     AgentRequest 扩展 Session 字段；新增 ResumeAsync 能力探测；
                  MockAdapter 仿真阶段/门禁/恢复

BatchAgentRunner.Infrastructure
 ├─ TaskFiles/    JsonTaskFileStore 扩展 workflow/depends_on 等字段；
 │                新增 MarkdownInPlaceWriter（原地回写）；DiffCollector
 ├─ Reporting/    MarkdownReportWriter 增加工作流阶段时间线 / DAG / 门禁 / diff 章节
 ├─ Notification/ WebhookNotifier / SmtpNotifier / DesktopNotifier
 ├─ Workflow/     FileWorkflowTemplateStore（templates/*.json）
 └─ Git/          LocalGitIntegration（建分支 / 开 PR，可选）

BatchAgentRunner.Desktop
 ├─ Views/        新增 WorkflowView, DagBoardView, GateResultView,
 │                 DiffView, TemplateEditorView, NotificationSettingsView
 └─ ViewModels/   对应 ViewModel + WorkflowRowViewModel / StageRowViewModel
```

#### 2.2 配置域扩展（在 9 域基础上新增 3 域 → 共 12 域）

新增配置域（详见 §9 与 CONFIG 文档二期条目）：
- **域 10 工作流与模板**：默认阶段超时、默认门禁最大循环、模板目录、是否启用会话恢复。
- **域 11 通知**：通道（桌面/Webhook/SMTP）、触发事件、安静时段、消息模板。
- **域 12 版本控制集成**：Git 根目录、自动建分支命名、PR 目标分支、是否自动开 PR。

---

### 3. 领域模型扩展

> 约定（沿用一期）：枚举字符串序列化；字段 snake_case；派生/计算属性标 `[JsonIgnore]`；集合声明用具体 `List<T>`（避免 STJ 反序列化接口类型 `NotSupportedException`）；反序列化忽略未知字段保证前向兼容。

#### 3.1 任务项 `TaskItem` 扩展

```csharp
class TaskItem
{
    string Id;
    string? Workspace;
    string? Instruction;          // 非工作流任务的单条指令（与 workflow 二选一）
    TaskStatus Status;            // 见下方枚举扩展
    // —— 二期新增（可选）——
    WorkflowSpec? Workflow;       // 工作流定义（取代单条 Instruction 时）
    List<string>? DependsOn;      // DAG 依赖的任务 Id 列表
    ProjectRef? ProjectRef;       // 项目级任务关联的代码仓库/分支
    TemplateVars? TemplateVars;   // 该任务由模板展开得到的变量实例（溯源用）
    int? MaxLoops;                // 该任务门禁/修订循环上限覆盖（全局见 RunOptions）
    int? BatchSize;              // 分批推进时的批大小覆盖
    // —— 结果与元数据（沿用一期，工作流下扩展 stage_results）——
    string? Result;
    List<StageResult>? StageResults;  // 工作流每个阶段的产出/状态（仅工作流任务）
    string? ModelUsed, AccountUsed, ExpertUsed;
    bool FallbackUsed;
    decimal? FallbackCost;
    int RetryCount;
    string? Error;
    // 断点恢复（方案 b）
    SessionHandle? Session;       // 当前会话句柄（CLI 支持时）
    DateTimeOffset? StartedAt, FinishedAt;
    double DurationSeconds;
}
```

**任务级状态枚举扩展**（一期 5 态 + 二期新增 2 态）：

```csharp
enum TaskStatus
{
    Pending, Running, Done, Failed, Interrupted,
    NeedsAttention,   // 二期新增：门禁/修订循环超限，产出遗留问题清单，等待人工介入
    Blocked           // 二期新增：依赖未满足或依赖失败导致本任务阻塞（区别于批级 Blocked）
}
```

> `NeedsAttention` 与 `Blocked` 为任务级状态，不影响批级 `Completed` 判定时**单独统计**（报告区分「真失败」与「待人工」）。

#### 3.2 工作流 `WorkflowSpec`

```csharp
class WorkflowSpec
{
    string Name;                       // 模板名或自定义名
    List<WorkflowStage> Stages;        // 有序阶段
    WorkflowOnFailure OnFailure = WorkflowOnFailure.StopTask; // 阶段全失败时的任务策略
    bool FreshSessionPerStage = true;  // 每阶段独立会话（省 tokens，默认开）
}

enum WorkflowOnFailure { StopTask, ContinueNextStage, MarkNeedsAttention }

class WorkflowStage
{
    string Id;                         // 阶段内唯一，供 {output:Id} 引用
    string Name;
    string InstructionTemplate;        // 支持变量：{output:prevStageId} / {var:x} / {task.instruction}
    StageExecutorOverride? Executor;   // 可选：覆盖模型/账号/专家/工作空间
    QualityGate? Gate;                 // 可选质量门禁
    int? MaxLoops;                     // 该阶段门禁修订循环上限（覆盖全局）
    StageOnGateFail OnGateFail = StageOnGateFail.FixLoop; // 门禁失败处理
    string? DependsOnStage;            // 阶段级依赖（同一工作流内，可选）
}

enum StageOnGateFail { FixLoop, StopStage, MarkNeedsAttention }
```

**阶段产出变量作用域**：每个阶段执行完毕后，其 `RawOutput` / `Result` 存入工作流上下文 `Dict<string,string>`，键为 `stageId`。后续阶段 `InstructionTemplate` 通过轻量模板引擎（见 §4.1）替换 `{output:stageId}`、`{var:name}`、`{task.field}`。模板引擎**仅做字符串替换 + 长度裁剪**，不做代码执行。

#### 3.3 质量门禁 `QualityGate`

```csharp
class QualityGate
{
    GateKind Kind;
    // —— Kind=Command ——
    string? Command;                  // 如 "dotnet test"、"npm run lint"
    string? ExpectedExitCode;         // 默认为 "0"
    string? OutputMatchRegex;         // 可选：对 stdout 做正则匹配判定通过
    // —— Kind=Review ——
    string? ReviewChecklist;          // 审查维度/checklist 文本，交给 agent 审查阶段
    string? ReviewExpertId;           // 审查所用专家
    // —— Kind=Manual ——
    bool ManualApproval = true;       // 人工卡点（遵循 REQ-002a，危险操作除外）
    // 通用
    string? FailMessageTemplate;      // 门禁失败时的遗留问题清单模板
}

enum GateKind { Command, Review, Manual }
```

#### 3.4 任务依赖 `TaskDependency`（DAG）

- 以 `TaskItem.DependsOn` 表达；加载期构建有向图。
- **入度调度**：任务仅当其所有依赖处于终态且满足「成功谓词」时入队。成功谓词默认 `Done || NeedsAttention`（即人工接管的不阻塞下游）；可配置 `BlockOnNeedsAttention`。
- **环检测**：加载期 Tarjan/DFS 检测环，命中即 `LoadError` 拒绝启动（不静默）。
- 依赖失败策略（`RunOptions.DependencyFailurePolicy`）：`BlockDependents`（默认，依赖 Failed/Blocked 则下游 Blocked）/ `AbortBatch` / `SkipDependents`。

#### 3.5 项目级引用 `ProjectRef`

```csharp
class ProjectRef
{
    string RepoPath;                  // 本地仓库路径（必填）
    string? BaseBranch;               // 基准分支（默认仓库当前分支）
    string? TaskBranch;               // 本任务工作分支（自动生成或指定）
    string? PrTargetBranch;           // 开 PR 的目标分支
    bool AutoCreateBranch = true;     // 启动时按命名规则建分支
    bool AutoOpenPr = false;          // 完成后自动开 PR（依赖 GitIntegration）
}
```

> 项目级任务本身**不强制** Git 集成：无 `IGitIntegration` 或配置关闭时，`ProjectRef` 仅作元数据记录，工作目录仍按 `Workspace` 解析。Git 操作为**尽力而为**，失败记入日志但不阻断任务。

#### 3.6 会话句柄 `SessionHandle`（断点恢复方案 b）

```csharp
class SessionHandle
{
    string AdapterKind;               // 仅同类型 adapter 可恢复
    string SessionId;                 // CLI 返回的 session/resume 标识
    string AccountId;                 // 会话所属账号（恢复时需对应凭证）
    DateTimeOffset LastTouchedAt;
}
```

#### 3.7 模板 `TemplateSpec`（REQ-016）

```csharp
class TemplateSpec
{
    string Id;
    string Name;
    string InstructionTemplate;       // 支持 {var:name}
    List<TemplateVarDef> Vars;        // 变量定义（名称/来源/默认）
    List<string>? FileGlob;           // 可选：按文件清单批量生成（每个文件一任务）
    WorkflowSpec? Workflow;           // 模板可同时是工作流模板
    int? DefaultBatchSize;
}

class TemplateVarDef { string Name; string? Default; VarSource Source; }
enum VarSource { Literal, FileList, CsvColumn, Expression } // 二期先做 Literal + FileList + CsvColumn
```

#### 3.8 Diff 与通知模型（REQ-017/018）

```csharp
class DiffRecord
{
    string TaskId;
    string? StageId;                  // 属哪个阶段（若有）
    string FilePath;
    DiffKind Kind;                    // Added / Modified / Deleted
    string UnifiedDiff;               // 统一 diff 文本
    string? BeforeHash, AfterHash;    // 内容指纹（sha256 前 16 位）
}

class NotificationRule
{
    NotificationChannel Channel;       // Desktop / Webhook / Smtp
    List<NotifyTrigger> Triggers;     // BatchCompleted / BatchBlocked / TaskFailed / NeedsAttention
    string? WebhookUrl;               // Channel=Webhook
    SmtpConfig? Smtp;                 // Channel=Smtp
    TimeRange? QuietHours;            // 安静时段不推送（仍记日志）
}
enum NotificationChannel { Desktop, Webhook, Smtp }
enum NotifyTrigger { BatchCompleted, BatchBlocked, TaskFailed, NeedsAttention, BatchTerminated }
```

---

### 4. 核心算法

#### 4.1 阶段模板引擎（轻量）

```
Render(template, ctx):
  ctx = { "output:<stageId>" : stage.Result,
          "var:<name>"      : task.TemplateVars[name],
          "task:<field>"    : task.<field>.ToString() }
  result = Regex.Replace(template, @"\{(output|var|task):([\w]+)\}", m => ctx.TryGet(m, out v) ? v : "< unresolved:... >")
  return Truncate(result, RunOptions.TemplateMaxKB * 1024)   // 防膨胀
```

- 未解析占位符保留原样并**告警一次**（不阻断），避免静默吞错。
- 渲染结果作为该阶段实际下发给 agent 的 `instruction`。

#### 4.2 工作流执行 `WorkflowExecutor`

对每个工作流任务，按 `Stages` 顺序驱动；复用一期单任务执行能力作为「单阶段执行器」`ExecuteStageAsync`：

```
RunWorkflow(task, ct):
  ctx = new WorkflowContext(task)
  for stage in task.Workflow.Stages:
      if stage.DependsOnStage 且 前置阶段未成功: continue with OnGateFail/OnFailure 策略
      loopGuard = 0
      loop:
          render instruction (引用 ctx 中已完成阶段产出)
          // —— 单阶段执行（复用一期重试/降级/额度/冷却逻辑）——
          r = ExecuteStageAsync(task, stage, instruction, ct)
          if r.Failure == None:
              ctx.Set(stage.Id, r.RawOutput)
              if stage.Gate != null:
                  gateResult = GateEvaluator.Evaluate(stage.Gate, r, ct)
                  if gateResult.Passed:
                      record StageResult(stage, Done, r); break loop
                  else:
                      loopGuard++
                      if loopGuard >= (stage.MaxLoops ?? RunOptions.DefaultMaxGateLoops):
                          record StageResult(stage, NeedsAttention, r, gateResult.Leftover)
                          apply stage.OnGateFail → FixLoop超限→MarkNeedsAttention / StopStage / 按 OnFailure
                          break loop
                      else:
                          // 修订循环：把门禁遗留问题作为补充指令，重跑本阶段
                          instruction += "\n\n门禁未通过，遗留问题：\n" + gateResult.Leftover
                          continue loop   // 回到 loop（同阶段，新指令）
          else:
              // 阶段执行失败：走阶段级失败策略（重试/降级已在 ExecuteStageAsync 内消耗）
              apply stage.OnGateFail / Workflow.OnFailure
              if 终止本阶段: record StageResult(stage, Failed/Interrupted); break 或 标记任务终态
  // 全部阶段完成
  task.Result = Aggregate(ctx)              // 汇总各阶段产出
  task.Status = (任一 NeedsAttention) ? NeedsAttention : Done
```

> **关键性质**：单阶段执行器 `ExecuteStageAsync` 与一期「单任务执行器」**代码同构**——区别仅在于入口指令来自模板渲染、出口多一步门禁判定与上下文回写。一期所有降级/额度/冷却/兜底逻辑零改动复用。

#### 4.3 质量门禁评估 `GateEvaluator`

| `GateKind` | 评估方式 | 通过判据 |
|------------|---------|---------|
| `Command` | Infrastructure 调 `Process` 执行 `Command`（在 `Workspace` 目录） | 退出码 `== ExpectedExitCode`（默认 0）；若配 `OutputMatchRegex` 需 stdout 命中 |
| `Review` | 以 `ReviewChecklist` 为指令、可选 `ReviewExpertId`，再调一次 agent「审查」阶段 | agent 输出含「通过」判定且无遗留项；遗留项提取为 `Leftover` 清单 |
| `Manual` | 暂停并等待人工在 UI 点「通过/打回」 | 人工通过即通过；打回视为未通过进入修订循环 |

门禁失败统一产出 `LeftoverIssues`（遗留问题清单字符串），写入 `StageResult` 与报告。

#### 4.4 DAG 调度 `DagScheduler`

```
BuildGraph(tasks):    // 加载期
  graph = edges from DependsOn
  if HasCycle(graph): throw LoadError("检测到循环依赖: " + cyclePath)
  indegree[id] = DependsOn.Count

ScheduleLoop(readyQueue, ct):
  while pending 非空 且 未终止:
      task = PickNext(readyQueue)            // 仍受账号/模型可用性约束（见一期调度）
      run task (单指令 或 RunWorkflow)
      on task terminal:
          if task.Status == Failed/Blocked 且 policy=BlockDependents:
              mark all dependents Blocked (记录 blocked_by)
          else:
              for dep in dependents(task): indegree[dep]--; if 0 → enqueue
```

- DAG 拓扑与一期「串行/并发」正交：`Concurrency>1` 时允许多条无依赖链并行；有依赖的链严格按拓扑序。
- 依赖等待期间批级状态可为 `Waiting`（复用一期语义），并提供 DAG 看板可视化等待原因。

#### 4.5 断点会话恢复（方案 b，兼容方案 a）

```
SessionResumePolicy.Resolve(task, candidateCombo, adapter):
  if !RunOptions.EnableSessionResume:            // 默认关，回退一期行为
      return NoSession                            // 完整指令重发（方案 a）
  if task.Session == null: return NoSession
  if task.Session.AdapterKind != adapter.Kind: return NoSession   // 跨 adapter 不可恢复
  if adapter.CanResume(task.Session): 
      return Resume(task.Session)                // 恢复同一会话，继续当前任务
  else:
      return NoSession                           // 降级方案 a（同等幂等假设）
```

- 启用会话恢复时，`ExecuteStageAsync` 在首次调用创建 session 并回写 `task.Session`；后续切换/兜底/重试优先 `Resume`。
- 一期 `SessionHandle` 为空时**完全等价于现有行为**，保证向后兼容与可灰度和回退。

---

### 5. 场景模板库（S1–S16）与原语

#### 5.1 通用能力原语（PRD 15.1）

| 原语 | 在二期设计中的落点 |
|------|------------------|
| 审查-修订循环 | `WorkflowStage` + `QualityGate(Kind=Review)` + `StageOnGateFail=FixLoop` + `MaxLoops` |
| 分批推进 | `RunOptions.Concurrency` + `BatchSize` + 模板 `FileGlob` 展开为多任务；每批完成即落盘（一期原子回写复用） |
| 独立会话 | `WorkflowSpec.FreshSessionPerStage=true`（默认） |
| 质量门禁 | `QualityGate`（Command/Review/Manual） |
| 人工卡点 | `QualityGate(Kind=Manual)`，遵循 REQ-002a 危险操作除外 |

#### 5.2 模板注册与自定义

- 内置模板存于 `templates/builtin/*.json`（S1–S16 各一份，见 §5.3）。
- 用户自定义模板存于 `templates/user/*.json`（用户在 `TemplateEditorView` 基于内置模板微调参数后「另存为」）。
- `IWorkflowTemplateStore` 端口统一加载/保存；模板与手写任务**等价展开**——模板实例化为任务文件后，运行时不再区分来源。
- 模板参数化：批大小 / 循环上限 / 门禁标准 / 审查专家 均为模板顶层可覆盖参数。

#### 5.3 内置场景模板定义（S1–S16）

每个模板 = `WorkflowSpec`（阶段序列 + 原语组合 + 默认参数）。以下为各模板的阶段骨架（指令细节由用户填充参数）：

| 编号 | 场景 | 阶段骨架（WorkflowStage 序列） | 门禁 | 关键默认参数 |
|------|------|-------------------------------|------|------------|
| **S1** | 需求文档生成与审查 | 生成需求 → 审查(Review) → 修订(FixLoop) → 再审 | Review，循环至通过 | 审查专家、checklist、MaxLoops=3 |
| **S2** | 设计文档生成与审查 | 生成设计 → 审查(架构/可行性/成本) → 修订 → 再审 | Review | 设计规范引用、MaxLoops=3 |
| **S3** | 自动排期与逐任务推进 | 生成排期(WBS) → 按排期逐任务(FreshSession 每任务) | 无/人工卡点 | 拆解粒度、推进节奏 |
| **S4** | 批量代码审查与分批修复 | 分批审查→问题分级→分批修复→复审 | Command(编译) + Review | 批大小、严重度过滤、MaxLoops=2 |
| **S5** | 批量 E2E 测试与修复 | 分批写/跑 E2E → 汇总失败 → 定位修复 → 重跑失败项 | Command(测试) | 批大小、失败重跑、MaxLoops=2 |
| **S6** | 批量可用性测试与修复 | 分批走查/启发式评估 → 问题清单 → 修复 → 复检 | Review(WCAG/交互) | 评估维度、批大小、MaxLoops=2 |
| **S7** | 批量 Bug 修复流水线 | Bug清单 → 逐个定位(FreshSession) → 修复 → 验证 → 关闭 | Command(验证) | 分批落盘、MaxLoops=2 |
| **S8** | 单元测试批量补齐 | 扫描低覆盖 → 分批生成用例 → 运行 → 修复失败 → 达标 | Command(覆盖率达阈) | 目标覆盖率、MaxLoops=2 |
| **S9** | 依赖升级流水线 | 依赖清单 → 分批升级 → 编译 → 测试 → 兼容修复 | Command(编译+测试) | 全通过即止 |
| **S10** | 代码规范批量整改 | lint 扫描 → 问题分级 → 分批自动修复 → 复扫清零 | Command(lint==0) | 可接受阈值 |
| **S11** | 安全漏洞批量修复 | 扫描报告 → 分级 → 分批修复 → 复扫 → 回归 | Command(复扫) + Review | 高危清零 |
| **S12** | 性能问题批量优化 | profiling → 热点分级 → 分批优化 → 基准对比 | Command(基准) | 性能目标/收益阈值 |
| **S13** | 代码注释/文档批量补全 | 扫描缺注释 → 分批补全(JSDoc/README) → 抽查复审 | Review(抽查通过率) | 通过率门槛 |
| **S14** | 多语言翻译与校对 | 分批翻译 → 审校(术语一致) → 修订 → 再审校 | Review | MaxLoops=3 |
| **S15** | 发布流水线 | 版本号→changelog→发布说明→回归清单→发布检查 | 各步 Manual/Command Gate | 任一步失败阻断 |
| **S16** | UI 走查与视觉回归 | 分批截图对比 → 差异分级 → 修复 → 复检 | Review(差异清零/人工豁免) | 差异阈值 |

> 模板的「通用性」：S1/S2 的「生成→审查→修订」循环可复用于任意文档类型（测试方案、运维手册等），模板仅参数化实例。

---

### 6. Markdown 原地回写规范（二期新增，兼容一期 sidecar）

一期：Markdown 只读 + `<name>.md.result.json` sidecar。二期提供**可选**原地回写，默认关闭（保持一期行为），开启后：

#### 6.1 原地回写格式

在 `## <task-id>` 标题下、指令内容之后，插入一个受控的元数据区块（以唯一哨兵注释包裹，便于解析与幂等更新）：

```markdown
## task-001
- instruction: 将 src/utils.ts 补充 JSDoc 注释

<!-- batch-agent-runner:meta task-001 -->
- status: done
- model_used: hy3
- account_used: acct-a
- expert_used: ""
- fallback_used: false
- started_at: 2026-10-17T01:00:00+08:00
- finished_at: 2026-10-17T01:03:12+08:00
- duration: 192
- result: |
  已为 12 个函数补充 JSDoc。
<!-- /batch-agent-runner:meta -->
```

- 哨兵注释 `<!-- batch-agent-runner:meta <id> -->` 与 `<!-- /batch-agent-runner:meta -->` 之间为工具托管区；**用户手写内容（instruction 及任何其他文本）永远不被改写**。
- 多阶段任务：在 meta 区块内追加 `stages:` 子列表（每阶段 status/result 摘要），正文仍只写最终汇总 `result`。
- 更新策略：解析时定位哨兵区间整体替换；区间不存在则插入到该任务标题块末尾（下一条 `##` 之前）。

#### 6.2 兼容与迁移

- 开启原地回写后，仍**同时**维护 sidecar（两份并存，sidecar 为权威备份），避免解析失败丢数据。
- 提供一次性迁移命令：将既有 sidecar 结果回填进 `.md` 原地 meta 区块。
- 工作流任务的 Markdown 回写：仅回写最终汇总与阶段状态摘要（完整阶段产出仍在任务文件/报告的 `stage_results`）。

---

### 7. REQ-016 任务模板 / 变量批量生成

#### 7.1 展开机制 `TemplateExpander`

```
Expand(template, source):
  instances =
    source.Vars == Literal   → 笛卡尔积（每变量取值列表）→ N 个变量组合
    source.Vars == FileList  → 遍历 FileGlob 命中文件 → 每文件一个实例（{var:file}=路径）
    source.Vars == CsvColumn → 读 CSV，每行一个实例
  for inst in instances:
      task = new TaskItem {
          Id = template.Id + "-" + index (或 CSV 主键),
          Instruction = Render(template.InstructionTemplate, inst),
          TemplateVars = inst,
          Workflow = template.Workflow?.CloneAndRender(inst),  // 工作流模板同样展开变量
          DependsOn = template.DependsOn (可选链式: 上一条完成后下一条)
      }
  return tasks   // 与手写任务文件字节级等价
```

- 展开产物写入**新的任务文件**（如 `generated-<timestamp>.json`），不污染源模板；用户可审阅后再运行。
- 链式依赖：模板可声明「展开后任务间顺序依赖」（如 S3 排期推进），`Expand` 自动补 `depends_on` 链。

#### 7.2 UI（TemplateEditorView）

- 选择内置/自定义模板 → 编辑变量值（多值换行即多实例）→ 预览展开后的任务列表（条数、首条指令预览）→ 「生成任务文件」落地。

---

### 8. REQ-017 结果 diff 视图

#### 8.1 采集 `IDiffCollector`

- 工作流/单任务**开始前**，对 `Workspace` 下「受关注文件集」做快照（默认：任务显式声明的文件路径 + 阶段执行后 agent 报告的改动文件；或配置 `diff_targets` glob）。
- 快照方式：记录文件 sha256 前 16 位 + 内容（或仅 hash，diff 时按需读取原内容——一期不持久原内容，故默认存 unified diff 快照）。
- 任务/阶段**结束后**再算一次，产出 `List<DiffRecord>`（Added/Modified/Deleted + unified diff 文本）。
- 大文件/二进制：仅记录 kind + size 变化，不生成文本 diff（防膨胀）。

#### 8.2 落盘与展示

- `DiffRecord` 存入任务的 `stage_results` 或独立 sidecar `<name>.diff.json`（按任务聚合）。
- 报告页新增「变更概览」章节（改动文件数、按阶段分布）。
- `DiffView`：左侧文件树（按任务/阶段分组）、右侧 unified diff 高亮；支持「仅看 Modified」「按阶段筛选」。

---

### 9. REQ-018 通知推送

#### 9.1 通道与触发

- `INotifier` 端口；内置 `DesktopNotifier`（复用一期 toast 机制，跨平台系统通知）、`WebhookNotifier`（POST JSON 到用户配置的 Webhook URL，如 企业微信/Slack/Discord 通用 inbound）、`SmtpNotifier`（SMTP 发邮件）。
- 触发事件：`BatchCompleted` / `BatchBlocked` / `BatchTerminated` / `TaskFailed` / `NeedsAttention`。
- 安静时段 `QuietHours`：命中则不推送通道消息（仍写运行日志），避免深夜打扰。

#### 9.2 消息模板

- 默认消息含：批名称、触发事件、成功/失败/待人工计数、耗时、报告路径、下一恢复时间点（若为 Blocked/Waiting）。
- 支持 `{batch.name}` `{stats.done}` 等占位符，用户可在配置域 11 自定义。

#### 9.3 安全

- Webhook URL / SMTP 密码走凭据引用（同 `credential_ref` 机制），不明文落配置；SmtpNotifier 凭据经 `ICredentialVault` 取用。

---

### 10. 适配层与持久化扩展

#### 10.1 `AgentRequest` 扩展

```csharp
record AgentRequest(
    string Instruction, AccountConfig Account, ModelCandidate Model,
    string? ExpertId, string WorkingDirectory, TimeSpan Timeout,
    AutoAnswerPolicy AutoAnswer, Action<string>? OnOutputChunk,
    SessionHandle? Session = null);          // 二期新增：非空则尝试恢复会话

// IAgentCliAdapter 新增能力探测
bool CanResume(SessionHandle handle);          // CLI 是否支持 session/resume
```

- 一期既有字段与行为不变；`Session` 为可选，null 时完全等价原逻辑。
- `MockAdapter` 仿真：阶段成功/门禁失败/会话恢复/断点续跑，支撑全链路自测（无需 M0 即可验证二期逻辑）。

#### 10.2 文件契约更新

| 内容 | 位置 | 二期变更 |
|------|------|---------|
| 任务文件 JSON | `.json` | 新增 `workflow` / `depends_on` / `project_ref` / `template_vars` / `max_loops` / `batch_size` / `stage_results` / `session` 字段；未知字段忽略（前向兼容） |
| Markdown 任务 | `.md` | 可选原地回写（§6）；未开启时仍 sidecar |
| 工作流模板 | `templates/builtin/*.json`、`templates/user/*.json` | 新增 |
| Diff 快照 | `<name>.diff.json`（或随 stage_results） | 新增 |
| 通知配置 | `config.json` 域 11 | 新增 |
| 冷却/运行日志/报告 | 沿用一期 | 报告增加工作流阶段时间线、DAG、门禁、diff 章节 |

#### 10.3 报告扩展（REQ-011 二期增强）

Markdown/JSON 报告新增章节：
- **工作流阶段时间线**：每任务各阶段的起止、门禁结果、循环次数。
- **DAG 概览**：任务依赖关系、阻塞原因（`blocked_by`）。
- **门禁统计**：通过/未通过/遗留问题数，按场景模板归类。
- **变更概览（diff）**：改动文件数、按阶段分布（引用 DiffView）。
- **待人工介入清单**：所有 `NeedsAttention` / `Blocked` 任务及其遗留问题，单独成表（不计入「真失败率」）。

---

### 11. 界面设计扩展（Avalonia，沿用 UI 设计系统 v1.0 双主题/令牌）

在三大页面（配置/监控/报告）基础上新增视图与状态，复用既有设计令牌与状态色速查表。

#### 11.1 配置页新增（域 10/11/12）

- **工作流与模板域**：默认阶段超时、默认门禁最大循环、模板目录、会话恢复开关。
- **通知域**：通道开关 + Webhook URL / SMTP 配置 + 触发事件多选 + 安静时段。
- **版本控制域**：Git 根目录、自动建分支命名规则、PR 目标、是否自动开 PR。

#### 11.2 监控页新增视图

1. **WorkflowView（工作流树）**：每个任务展开为阶段列表（阶段状态徽标、门禁结果、循环次数、当前阶段实时输出）；沿用「状态永远可见」原则。
2. **DagBoardView（依赖看板）**：节点=任务，有向边=依赖；颜色区分 Done/Running/Blocked/NeedsAttention；悬停显示 `blocked_by` 与等待原因；支持缩放到全批。
3. **GateResultView（门禁结果）**：门禁通过/未通过、遗留问题清单、修订循环次数。
4. **Toast 扩展**：模板展开完成、门禁未通过进入修订循环、依赖阻塞、会话恢复成功/降级、通知已发送。

#### 11.3 报告页新增

- 工作流阶段时间线图、DAG 缩略图、门禁统计、变更概览入口（跳转 DiffView）。

#### 11.4 新增独立视图

- **DiffView**：文件树 + unified diff（§8.2）。
- **TemplateEditorView**：模板选择/参数编辑/展开预览/生成任务文件（§7.2）。

#### 11.5 新增任务级状态视图

`NeedsAttention`（中性色 + 「查看遗留问题」按钮，不报红）、`Blocked`（提示 `blocked_by` 与依赖恢复条件）。两者均**不计入批级失败率**，但批级 `Completed` 判定时单独可见（影响自动关机条件：默认允许带 NeedsAttention 关机，勾选「仅零待人工」时阻止）。

---

### 12. 测试策略

| 层 | 用例（二期新增） |
|----|----------------|
| `WorkflowExecutor`(MockAdapter) | 多阶段顺序执行 + 阶段产出传递；门禁通过即止；门禁失败进入 FixLoop 直至通过；MaxLoops 超限 → NeedsAttention；`OnFailure=ContinueNextStage` 容错；FreshSessionPerStage 每阶段独立会话 |
| `GateEvaluator` | Command 退出码/正则判定；Review 遗留问题提取；Manual 卡点通过/打回；门禁失败产出 Leftover |
| `DagScheduler` | 拓扑序执行；环检测拒绝启动；依赖 Failed → 下游 Blocked（policy=BlockDependents）；并发下无依赖链并行、有依赖严格有序 |
| `TemplateExpander` | Literal 笛卡尔积、FileList 多文件、CsvColumn 多行；链式 `depends_on` 自动生成；展开产物与手写等价 |
| `MarkdownInPlaceWriter` | 首次插入 meta、二次幂等更新、不改写用户 instruction、无 meta 区块时插入正确位置、迁移 sidecar |
| `DiffCollector` | 文件新增/修改/删除判定、unified diff 生成、大文件仅记 kind |
| `SessionResumePolicy` | 未启用→方案 a；跨 adapter/无 session→方案 a；支持→Resume；恢复失败降级方案 a |
| `Notifier` | Desktop/Webhook/Smtp 触发；安静时段抑制；消息模板渲染 |
| `FileWorkflowTemplateStore` | 内置 S1–S16 加载、用户自定义保存/读取、模板实例化等价性 |
| 报告 | 工作流时间线/门禁/diff 章节生成正确 |

---

### 13. 里程碑与风险

#### 13.1 里程碑（建议在 M4 打磨发布后启动）

| 里程碑 | 内容 | 依赖 |
|-------|------|------|
| E0 | 二期设计（本文档）+ 领域模型扩展 + 单元测试（不含 UI） | 一期 D0–D4 完成 |
| E1 | 工作流执行引擎 + 门禁 + DAG 调度贯通（MockAdapter 全链路） | E0 |
| E2 | 模板库 S1–S16 + TemplateExpander + TemplateEditorView | E1 |
| E3 | REQ-017 diff + REQ-018 通知 + Markdown 原地回写 | E1 |
| E4 | 会话恢复（方案 b，依赖 M0 V-8 实测；不支持则保持方案 a） | M0 V-8 |
| E5 | UI 扩展（Workflow/DAG/Gate/Diff/Template/Notification 视图）+ 报告增强 | E1–E3 |
| E6 | 真实任务验证（场景模板跑 ≥3 批真实工程任务） | E1–E5 |

#### 13.2 风险与缓解

| 风险 | 缓解 |
|------|------|
| CLI 不支持会话恢复（V-8） | 方案 b 默认关，`SessionHandle` 为空时完全回退方案 a（一期行为）；不阻塞其它二期能力 |
| 阶段产出过大导致上下文膨胀 | 模板引擎默认长度裁剪（TemplateMaxKB）；FreshSessionPerStage 默认开；阶段间仅传字符串变量 |
| 门禁误判（如 lint 噪声） | 门禁标准参数化、可配正则白名单；MaxLoops 封顶；超限转 NeedsAttention 而非失败 |
| DAG 死锁/环 | 加载期环检测拒绝启动；依赖失败策略可配，默认阻塞下游而非 abort |
| 模板展开爆炸（笛卡尔积） | 展开预览先显示条数并强制确认；单批上限 `MaxBatchExpansion` 配置 |
| Git 集成副作用（误建分支/开 PR） | 默认关闭自动开 PR；Git 操作尽力而为，失败仅记日志不阻断；危险操作仍走黑名单防护 |
| Markdown 原地回写破坏用户内容 | 哨兵注释包裹托管区 + 同时维护 sidecar 备份；提供迁移与回滚 |

---

### 14. 开放问题（待用户/M0 确认）

1. **会话恢复是否启用**：取决于 M0 V-8。二期设计已将其作为**可选、默认关**的能力，不影响主线。
2. **门禁默认 MaxLoops**：S1/S2 建议 3，S4–S13 建议 2，是否统一为单一全局默认 + 模板覆盖？待真实工程任务校准。
3. **模板展开单批上限**：默认多少条触发「确认」弹窗（建议 50，对齐一期每批 ≥50 条验证规模）？
4. **Markdown 原地回写默认**：一期用户已习惯 sidecar，二期默认仍关，还是随升级默认开？建议默认关 + 首次开启引导。
5. **项目级任务的 Git 集成深度**：二期先做「建分支 + 记录 PR 链接」，自动开 PR（含 push）是否放入二期或留待三期？
6. **diff 采集的「受关注文件」范围**：默认全仓快照开销大，是否仅采集 agent 显式报告改动的文件（依赖 adapter 返回改动清单）？需 M0 V-1/V-7 实测输出结构确认。
7. **通知通道优先级**：桌面通知是否足以覆盖无人值守场景？Webhook（企业微信等）是否作为二期默认推荐通道？

---

> 本文档与一期设计 `docs/DESIGN-BatchAgentRunner-v2.0.md` 共同构成完整技术设计。一期为「单指令无人值守跑批」，二期为「软件工程流程自动化」。两者共享同一引擎内核、领域模型与界面体系，二期通过**可选字段 + 端口扩展**叠加，不破坏一期已交付行为。

## 第3部分：专题设计 A — 凭证备份与账号切换（原 DESIGN-账号凭证切换 v1.1）

### 1. 背景与目标

PRD 的核心差异化价值是「多账号接力」，但引擎层此前只约定了**切换策略**（DegradationMatrix 遍历顺序），没有解决**凭据从哪来、怎么切**的问题（V-4 开放问题）。

本设计采用**凭证快照 + 文件级切换**方案，两步闭环：

1. **引导登录 → 备份**：用户在界面发起引导登录，工具拉起 CLI 的交互式登录（浏览器 OAuth），登录成功后把 CLI 的登录凭证文件**备份出来，按账号命名**存入本地 Vault。
2. **切换 = 复制回填**：需要用账号 A 执行任务时，把 Vault 里 A 的备份凭证**复制到 CodeBuddy / WorkBuddy 的当前凭证位置（Live）**，CLI 即以 A 的身份运行。

方案成立的前提：两家 CLI 的登录态均为**文件型存储**（或可导出为文件），且不存在强设备绑定。

---

### 2. 本机侦查结论（2026-10-15 实测，Windows 11）

| 对象 | 结论 |
|------|------|
| CodeBuddy CLI（CodeBuddy Code） | 配置目录 `~/.codebuddy/`，当前**无** `.credentials.json` / auth 类文件 → 登录态应在首次 CLI 登录后生成（按 Claude Code 系产品惯例预期为 `~/.codebuddy/.credentials.json`，**V-4a 实测确认**） |
| CodeBuddy IDE（CodeBuddy CN 桌面版） | 登录态在 `%APPDATA%\CodeBuddy CN\User\globalStorage\`（SQLite vscdb + 扩展目录），结构复杂，**不作为切换对象**——本工具只切换 CLI 的登录态 |
| WorkBuddy CLI | `~/.workbuddy/` 下未发现文件型凭证（connectors 凭证按用户 UUID 目录存放，桌面主登录态存储方式未知），**V-4b 实测确认** |
| Windows 凭据管理器 | 无 WorkBuddy / CodeBuddy 条目 → 两家均**未使用系统密钥库**存登录态，文件级备份方案可行概率高 |
| CodeBuddy 手册 | CLI 首次启动需选登录站点（Chinese Site / International / Enterprise / iOA）→ 站点选择也属于账号差异的一部分，需一并备份 |

> ⚠️ 侦查只确认了「目前没有」，不等于「登录后也没有」。第 10 章验证项是本设计的硬前置。

---

### 3. 核心模型：Live / Snapshot / Vault

```
┌─────────────────────────────────────────────────────────┐
│  Vault（仓库，工具私有目录，按账号命名）                    │
│  %LOCALAPPDATA%\BatchAgentRunner\vault\                  │
│    ├─ codebuddy\                                         │
│    │   ├─ account-a\  ← 账号 A 的快照集                   │
│    │   │   ├─ files\          (凭证文件本体)              │
│    │   │   └─ manifest.json   (清单+哈希)                 │
│    │   └─ account-b\ ...                                 │
│    └─ workbuddy\                                         │
│        ├─ account-a\ ...                                 │
│        └─ account-b\ ...                                 │
└─────────────────────────────────────────────────────────┘
                 │ 复制（切换）          ▲ 复制（备份/保活回写）
                 ▼                     │
┌─────────────────────────────────────────────────────────┐
│  Live（CLI 实际读取的当前凭证位置）                        │
│    CodeBuddy: ~/.codebuddy/.credentials.json 等           │
│    WorkBuddy: （V-4b 确认）                               │
└─────────────────────────────────────────────────────────┘
```

- **Snapshot（快照）**：某账号某时刻全部凭证文件的集合 + 清单。
- **Vault**：所有快照的持久化仓库，目录名 = `accounts[].id`（与冷却记录同键，要求稳定）。
- **Live**：CLI 正在使用的凭证文件。**同一时刻每个 CLI 只有一个身份。**

---

### 4. 目录与清单契约

#### 4.1 快照目录

```
vault\<cli>\<account_id>\
  ├─ files\                    # 按原始相对路径镜像（保持子目录结构）
  │    └─ .credentials.json    # 例：CodeBuddy CLI
  └─ manifest.json
```

#### 4.2 manifest.json（snake_case，与项目约定一致）

```jsonc
{
  "account_id": "account-a",          // 必须与配置中 accounts[].id 一致
  "cli": "codebuddy",                 // codebuddy | workbuddy
  "display_name": "A 账号",
  "site": "chinese",                  // 登录站点（CodeBuddy CLI 首启选择，切换时若不一致需提示）
  "backed_up_at": "2026-10-15T14:00:00+08:00",
  "last_verified_at": "...",          // 最近一次预检通过时间
  "files": [
    { "relative_path": ".credentials.json", "sha256": "…", "size": 1234 }
  ],
  "schema_version": 1
}
```

#### 4.3 Live 位置注册表（代码内 `CredentialLocationRegistry`）

每个 CLI 注册：凭证文件路径列表（含相对路径映射）、进程名（用于互斥检测）、可选的「logout 清理动作」。**路径全部来自注册表，不散落硬编码**——M0 实测后只改注册表，不动流程代码。

> **2026-10-19 修订（技术债务 P1-16 已清偿）**：注册表由 `public static class` + 静态 `Dictionary`（`SetLiveRootOverride` 全局覆写、非线程安全）改为 `sealed class : ICredentialLocationRegistry` **显式注入的实例服务**：规格表为不可变静态数据（全部实例共享），Live 根覆写为实例状态（`ConcurrentDictionary`，线程安全、无跨实例全局副作用）。组合根（`Desktop/Composition/EngineFactory`）持唯一实例并一次性覆写 Mock Live 根，Vault / 引导登录 / CLI 适配器 / 积分领取全链路显式注入同一实例——消费方不再经静态入口获取路径。背景与验证见 `docs/TECH-DEBT-技术债务治理方案-v2.0.md` §1.2 P1-16 / §5.5。

---

### 5. 引导登录流程（登录成功 → 按账号备份）

```
界面「引导登录」
  → ① 选/新建 account_id（Vault 目录名）
  → ② 清场：确认无该 CLI 运行中的进程；删除/移走当前 Live 凭证（logout 或手工清理，V-4e 确认）
  → ③ 拉起 CLI 交互式登录（如 codebuddy login，浏览器 OAuth）
  → ④ 用户在浏览器完成登录，回到界面点「我已登录」
  → ⑤ 检测：Live 凭证文件出现 / mtime 变化（轮询 + 超时，如 5 分钟）
  → ⑥ 校验：调用 CLI 做一次极简请求（复用 REQ-010 预检逻辑），确认凭证有效
  → ⑦ 备份：Live 文件集 → vault\<cli>\<account_id>\files\，写 manifest（含 sha256）
  → ⑧ 界面提示成功；该账号进入可用账号池
```

关键点：
- **⑤ 的变化检测**以「文件出现或哈希变化」为准，不依赖 CLI 的退出码。
- **⑦ 备份的是文件副本，不是移动**——Live 始终保留一份，保证备份后 CLI 立即可用。

---

### 6. 切换流程（把备份复制回 Live）

```
引擎请求切换到账号 X（或用户手动「切换到此账号」）
  → ① 前置互斥：无该 CLI 的运行中进程（批处理空闲时才允许；运行中切换只发生在任务边界）
  → ② 保活回写：当前 Live 若属于账号 Y 且其文件哈希 ≠ Y 快照记录 → 先把 Live 复制回 Y 的快照
       （理由：CLI 运行期间会自动刷新 access/refresh token，最新 token 在 Live 里；
        不回写则 Y 的快照会随时间失效）
  → ③ 原子覆盖：X 快照 files → Live 位置（先写临时文件 + rename，失败自动回滚②的副本）
  → ④ 校验：极简请求确认身份 = X（对比 CLI 输出的账号标识，V-4c 确认可获取）
  → ⑤ 发出 AccountSwitched 事件（原账号、新账号、触发原因）
```

**并发延后切换（REQ-021，见 §9.2）**：`Concurrency>1` 时账号切换只允许在「全局派发暂停解除、所有在飞任务结束（in-flight=0）」之后调用本流程**一次**（即「统一切换」）。调用瞬间无运行中进程，天然满足 ① 前置互斥；本流程的 ② 保活回写 / ③ 原子覆盖 / ④ 校验步骤不变，仅触发时机由「即时」改为「当前在飞任务全部结束后」。

**保活回写（②）是本设计的灵魂**：把「Live 永远是最被频繁刷新的那份」这个事实，转化为「每次离开某账号时把 Live 还给它」，Vault 里的快照因此始终是各账号最新一次使用时的凭证。

失效兜底：refresh token 若有「闲置天数上限」，久未使用的账号切回时可能 AuthExpired → 预检发现 → 界面提示对该账号重新「引导登录」（流程同第 5 章，覆盖旧快照）。**不做自动续命。**

---

### 7. 与引擎 / 配置 / 界面的衔接

| 现有约定 | 本设计的落点 |
|----------|-------------|
| `accounts[].credential_ref` | 语义明确化为 **Vault 内账号目录名**（= `accounts[].id`， credential_ref 保留做显式覆盖，默认取 id） |
| `IAccountValidator` / REQ-010 预检 | 预检前先执行「切换到该账号」；预检即验证快照有效性，通过后更新 `manifest.last_verified_at` |
| `AuthExpired` 错误分类 | 触发「该账号需重新引导登录」提示 + 停用该账号走既有降级逻辑 |
| `AccountSwitched` 事件/日志 | 切换成功后发出，日志**只记账号 id 与文件哈希前 8 位，不记凭证内容** |
| 配置页账号卡 | 新增「引导登录」「切换到此账号」「快照状态（备份时间/最近验证/已失效）」 |
| 手动「切换到此账号」禁用条件（REQ-021） | `in-flight>0`（含派发暂停期间）禁用；仅 `in-flight=0` 或串行态可操作，立即执行同 §6 并清除 `PendingSwitchTarget` |
| MockAdapter | 模拟同一套 Vault 流程（假凭证文件），全链路可先行联调 |

---

### 8. 安全设计

1. Vault 目录 DPAPI 加密（Windows `CryptProtectData`，按当前用户绑定），明文只存在于内存与 Live 位置。
2. Vault 与 Live 均设置 ACL：仅当前用户可读。
3. 凭证内容不进日志、不进任务文件、不进报告（PRD 安全约定）。
4. 备份/切换操作写审计日志（时间、账号 id、动作、结果），不含内容。
5. 删除账号时同时删除其 Vault 快照，需界面二次确认。

---

### 9. 约束、风险与开放问题

| 风险 | 等级 | 应对 |
|------|------|------|
| 凭证非文件型（存 keychain/加密 SQLite） | 高（待 V-4b） | 降级方案：环境变量注入 token（若 CLI 支持）或改用 CLI 自带的多 profile 机制；实在不行该 CLI 仅支持单账号 |
| 设备指纹绑定（machineid / device-id 参与鉴权） | 中 | V-4c 实测：切换后是否需要同步备份 machineid 类文件；若强绑定则此方案不可行 |
| refresh token 闲置过期 | 中 | 预检拦截 + 引导重登；文档明示「长期不用的账号切回前先预检」 |
| 多账号高频轮换触发平台风控 | 中 | PRD 第 14 章已有约定：仅用户自有合法账号免费额度；切换频率跟随任务失败/额度耗尽，不做无意义轮换 |
| 切换时 CLI 进程仍存活导致文件被覆盖 | 中 | 互斥检测（进程名 + 任务状态机仅 Idle/任务边界允许切换） |
| 站点/租户不一致（A 账号国际站、B 账号国内站） | 低 | manifest 记录 site，切换时校验，不一致则提示并按账号携带站点配置 |

**开放问题：** WorkBuddy CLI 是否存在独立于桌面端的 `login` 命令；桌面端登录态是否与 CLI 共享（V-4b 一并确认）。

---

### 10. M0 验证项细化（V-4 拆分）

| 编号 | 验证内容 | 通过标准 |
|------|---------|---------|
| V-4a | CodeBuddy CLI 登录后凭证文件路径、格式、是否含 refresh token | 找到文件且为可复制的自包含 JSON |
| V-4b | WorkBuddy CLI 登录态存储位置与形态 | 同上；确认是否文件型 |
| V-4c | 备份→清空→回填后，CLI 无感使用回填身份 | CLI 正常调用，且能从输出/配置确认当前账号身份 |
| V-4d | refresh token 是否轮换、闲置多久过期 | 得到轮换行为与过期时长的结论，确定保活回写必要性 |
| V-4e | logout / 删除凭证后是否回到「干净未登录」态 | 可作为引导登录的可重复起点 |

---

### 11. 测试要点

- 快照备份：文件集哈希与 manifest 一致；重复备份覆盖并更新时间戳。
- 切换：保活回写触发条件（哈希不一致）；覆盖失败回滚；进程存活时拒绝切换。
- 预检集成：快照缺失账号预检直接失败并提示引导登录。
- 安全：日志/报告/任务文件中 grep 不到凭证片段。
- MockAdapter 全流程：登录模拟 → 备份 → 切换 → AuthExpired → 重登覆盖。

## 第4部分：专题设计 B — 额度耗尽暂停续跑与暂停时长剔除（原 DESIGN-额度耗尽 v1.0）

### 1. 需求（Requirements）

- **R1 暂停续跑**：当所有启用账号的所有候选模型组合**因额度耗尽**（`WaitKind == QuotaReset`）而同时不可用时，引擎进入 `Waiting` 暂停，等待最早恢复的组合额度重置后，**自动恢复并继续执行**剩余任务；不转为 `Blocked`。
- **R2 暂停时长剔除**：暂停/等待期间消耗的真实时间**不计入**对外报告的「时长」。批级与单任务级时长均为「净执行时长 = 墙钟时长 − 等待时长」。
- **R3 作用域限制**：无限等待只针对**纯额度耗尽**（`QuotaReset`）。纯免费时段外（`FreeWindow`）仍受 `max_wait_minutes` 约束（避免跨天/跨周末无限挂起）；时段+额度混合（`Mixed`）维持上限约束（时段可能主导恢复点）。
- **R4 可随时中止**：暂停期间用户仍可通过「取消等待」（`CancelWaiting` → `Blocked`）或「终止」（`TerminateAsync`）退出，行为不变。
- **R5 可配置开关**：新增 `wait_for_quota_reset`（默认 `true`）。关闭后退回现状（按 `max_wait_minutes` 判定阻塞）。
- **R6 可续跑（重启安全）**：`QuotaCooldownRecord` 已落盘并在 `InitializeAsync` 重新加载，暂停后若进程退出/重启，再次启动会重新评估可用性并在额度恢复后自动继续。

---

### 2. 设计（Design）

#### 2.1 配置项（RunOptions）
新增字段：
```
wait_for_quota_reset  : bool  (默认 true)   // 额度耗尽时无限等待重置
```
派生属性 `WaitForQuotaReset`（与 `MaxWait` 等同类）。

#### 2.2 引擎判定（BatchEngine）
- 新增私有判定 `IsQuotaUnboundedWait(RestoreCandidate? c)`：
  `_options.WaitForQuotaReset && c is { WaitKind: WaitKind.QuotaReset }`。
- **单任务重试循环**（`ExecuteTaskAsync` → P1-2 内拆 1790708 后由 `HandleNoComboAsync` 子方法承载）：原 `canWait = earliest is not null && earliest.RestoreAt <= now + MaxWait`
  改为 `canWait = earliest is not null && (IsQuotaUnboundedWait(earliest) || earliest.RestoreAt <= now + MaxWait)`。
- **启动前预派发**（`StartAsync`）：原 `if (waitIfUnavailable && earliest is not null)`
  改为 `if (earliest is not null && (waitIfUnavailable || IsQuotaUnboundedWait(earliest)))`（额度耗尽即便未勾选「等待自动启动」也自动挂起等待，因其必定恢复）。
- `WaitForRestoreAsync` 增加 `string? taskId` 参数：累计等待时长除计入 `_windowWaitSeconds` / `_cooldownWaitSeconds`（批级）外，若 `taskId` 非空，额外累加进 `_taskWaitSeconds[taskId]`（单任务级）。
- 调用点：
  - `ExecuteTaskAsync` 内 `WaitForRestoreAsync(earliest!, ...)`（P1-2 内拆后实际由 `HandleNoComboAsync` 委派）→ 传入 `task.Id`。
  - `StartAsync` 内调用 → 传 `null`（不归属任何任务）。

#### 2.3 暂停时长剔除
- 终止方法（`CompleteTask` / `FailTask` / `MarkInterrupted`）在 `task.Finish(status, now)` 后调用 `ApplyPausedExclusion(task)`：
  若 `_taskWaitSeconds[task.Id]` 有值，从 `task.DurationSeconds` 中减去并夹紧到 ≥ 0。
- `BuildReport` 新增 `ActiveDurationSeconds = max(0, DurationSeconds − WindowWaitSeconds − CooldownWaitSeconds)`，
  原 `DurationSeconds` 保留为墙钟总时长（含等待），便于对照。

#### 2.4 事件/日志文案
`WaitForRestoreAsync` 的 `DescribeWaiting` 在 `QuotaReset` 分支文案补充「…等待额度重置（暂停不计入时长），可取消」。

#### 2.5 报告与界面
- `BatchReport` 新增 `active_duration_seconds`。
- `ReportViewModel.DurationText`：主显示净执行时长，括号附注墙钟总时长，例如 `净执行 12.3 秒（总耗时含等待 5402.0 秒）`。
- 单任务行 `TaskRowViewModel.DurationText` 已读 `Item.DurationSeconds`，因 2.3 已剔除本任务等待，自动正确。
- `ConfigView`：在「⑦ 并发·重试·超时·单任务上限」区新增复选框「额度耗尽时无限等待重置（暂停不计入时长）」绑定 `WaitForQuotaReset`；`ConfigViewModel` 增加对应属性并在 `Reload` / `SaveAsync` 中读写。

#### 2.6 边界与并发
- `_waitGate` 已保证同时仅一个等待在进行；并发模式（`concurrency > 1`）下每任务等待也按 `taskId` 独立累计，互不串扰。
- `MockAdapter` 与 `FakeClock` 已支持推进时钟，测试可直接验证越过 `max_wait` 的额度等待。

---

### 3. 测试计划（覆盖点）
1. 全部组合额度耗尽且恢复时间 > `max_wait` → 仍自动等待重置后完成（证明 R1/R3 放上限）。
2. 上述场景：`report.ActiveDurationSeconds` 不含等待、单任务 `DurationSeconds` 不含本任务等待（R2）。
3. 关闭 `wait_for_quota_reset` 且恢复时间 > `max_wait` → 维持 `Blocked`（证明 R5 开关有效）。
4. 纯免费时段外（无额度）超出 `max_wait` → 仍 `Blocked`（证明 R3 不误用无限等待）。
5. 暂停期间 `CancelWaiting` → `Blocked`（R4）。

---

### 4. 影响面
- 仅新增配置项与一处等待上限判定 + 时长计算；不改动降级矩阵、冷却登记、兜底、凭证切换既有契约。
- 现有 `全部组合冷却中_按最早恢复时间等待后继续执行` 等测试不受影响（`max_wait` 内仍走原路径）。
