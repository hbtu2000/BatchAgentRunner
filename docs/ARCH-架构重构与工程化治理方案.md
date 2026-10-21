# BatchAgentRunner 架构重构与工程化治理方案（ARCH）

> 版本：v1.0 ｜ 日期：2026-10-21 ｜ 角色：资深软件架构师  
> 更新：P-01（WaitCoordinator 抽取）已收口，见 §6.2 执行记录。
> 关联文档：`docs/TECH-DEBT-技术债务治理方案-v2.0.md`（战术台账，本方案的落地清单来源）、`docs/DESIGN-BatchAgentRunner-v2.0.md`（单一设计权威源）、`docs/PRD-BatchAgentRunner-v2.0.md`、`docs/CONFIG-配置项说明.md`

## 文档定位（必读）

本方案是**架构战略层**，负责回答"怎么分层、边界在哪、改造顺序怎么排、工程质量怎么内建"；  
`TECH-DEBT-技术债务治理方案-v2.0.md` 是**战术台账层**，负责回答"每一项具体债务在哪、怎么还、还了没"。  
两者关系：**本方案第 3/4/5/6 章的决策，由 TECH-DEBT 的 P0/P1/P2 条目逐条承接落地**；本方案不复述债务清单，只引用。

**现状基线（已扎实，必须保留，禁止回潮）**：截至 2026-10-20，TECH-DEBT 阶段 −1/0/1/2 已完成——`BatchEngine` 已按 strangler fig 拆成 6 个协作者（ITaskDispatcher / ITaskRunner / IFallbackBilling / IGitWorkflow / IBatchReportBuilder+IBatchNotifier / IPreflight），已提 `IBatchEngine`、`IEngineFactory`/`IEngineHost`，进程/时钟/文件系统/IO 已抽端口，CI 已建（全量 `--no-incremental`）、`TreatWarningsAsErrors=true`、`Directory.Packages.props` 中央包管理、`.editorconfig` + commit 钩 + 494 个测试方法。本方案在**这套地基之上**继续做架构收口与工程化深化，不推倒重来。

---

## 1. 现状诊断

### 1.1 资产盘点（已扎实，列为"保留项"）

| 维度    | 现状                                                                                            | 评估                           |
| ----- | --------------------------------------------------------------------------------------------- | ---------------------------- |
| 分层    | Core / Agents / Infrastructure / Desktop 四工程，目录语义清晰                                           | 良好，需补强依赖方向约束                 |
| 引擎解耦  | `BatchEngine` 已拆 6 协作者 + 提 `IBatchEngine` 接口 + 下沉 `WaitCoordinator`（P-01）                       | 良好，facade 待 P-02 收口 ≤400 行（见 1.2 / §6.2） |
| 组合根   | `IEngineFactory`/`IEngineHost` 已落，UI 倒置已缓解                                                    | 良好，靠 IL 断言维持（脆弱，需文化固化）       |
| 可测性缝  | 进程 `IProcessLauncher`、时钟 `IClock`、IO `IFileSystem`/`ITaskFileStore` 已抽                        | 良好，覆盖率不均                     |
| 构建/分析 | `TreatWarningsAsErrors=true`、`AnalysisLevel=latest-recommended`、`Nullable`/`ImplicitUsings` 开 | 良好                           |
| 依赖管理  | `Directory.Packages.props`（CPM）已上                                                             | 良好                           |
| 测试    | 494 个测试方法，已分快/慢 bucket                                                                        | 数量足，**缺覆盖率量化与门禁**            |
| CI    | `.github/workflows/ci.yml` 已建，全量重建 + 分层测试                                                     | 基础有，**"只报告不阻断"、单平台、无扫描/覆盖率** |

### 1.2 架构问题清单（待治理，带代码证据）

按类别归因，每条标注严重度（**高/中/低**）与对应 TECH-DEBT 条目。

| #    | 类别         | 问题                                                                                                                        | 证据（实测）                                                                | 严重度 | 关联                     |
| ---- | ---------- | ------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------- | --- | ---------------------- |
| A-1  | 上帝类·编排     | `BatchEngine` facade 仍承载"批次编排 + 等待循环 + 唤醒 + 可用性轮询编排"，瘦身不彻底；**P-01 已下沉 `WaitCoordinator`（等待循环/唤醒/可用性轮询）**，facade 由 1018→953 行，剩余"≤400 行"收口见 P-02 | `src/.../Core/Engine/BatchEngine.cs` **953 行**（P-01 后）／`WaitCoordinator.cs` 156 行 | 高   | P1-1（已拆协作者）＋P-01（WaitCoordinator 下沉）＋P-02（facade ≤400 收口） |
| A-2  | 上帝类·状态机    | 单任务状态机虽外置，但 `TaskRunner` 仍是巨类，分支/重试/退避/降级/超时全挤一处                                                                          | `TaskRunner.cs` **903 行**                                             | 高   | P1-2（已外置，待策略对象化）       |
| A-3  | 上帝类·UI     | Desktop 真实剩余热点在 VM：`ConfigViewModel` 1065 / `MonitorViewModel` 983 / `LoginViewModel` 751 / `TemplateEditorViewModel` 674 | `src/.../Desktop/ViewModels/*`                                        | 高   | P1-19（已补测试，待分解）        |
| A-4  | 重复·样板      | 三份 `Json*Store` 逐行同构（构造+默认目录+Load+Atomic 保存）；"BatchAgentRunner" 路径魔法串在 **9 处**硬编码                                         | `JsonCooldownStore`/`JsonCreditsClaimStore`/`JsonConfigStore`、`P2-31` | 中   | P2-31 ✅ 已收口（2026-10-21，见 TECH-DEBT §5.11） |
| A-5  | 重复·领域      | `Truncate`×3、终态集合×3、`Clone`×6、失败分派状态机逐字重复                                                                                 | `P2-1~P2-6`                                                           | 中   | P2-1~6                 |
| A-6  | God Config | `RunOptions` 369 行 + `Clone()` 靠 JSON 往返；`ConfigEditors` 509 行手写 DTO 编辑器                                                  | `RunOptions.cs` / `ConfigEditors.cs`                                  | 中   | P2-9 / P2-15           |
| A-7  | 依赖·脆弱      | 跨层引用靠 IL 扫描断言"硬维持"，一旦断言漏写即回潮；组合根外偶有 `new` 具体类型                                                                            | `P1-4` 收口项、`§4.3`                                                     | 中   | P1-4 / P4.3            |
| A-8  | 可运维·可观测    | 无运行级指标（降级次数/兜底触发/冷却跳过/误切换率/吞吐/平均耗时）；日志是否结构化、是否带 RunId 关联未统一                                                               | `IRunLogger` 存在但未统一语义                                                 | 高   | 工程化缺口                  |
| A-9  | 工程·CI      | CI "只报告不阻断"；单平台（windows-latest，Avalonia 跨平台却只验 Windows）；无覆盖率、无静态安全扫描、无 Release 流水线                                        | `ci.yml`                                                              | 高   | P0-16 / P4.9           |
| A-10 | 工程·规范      | `.editorconfig` 仅 28 行，团队级关键规则（命名/CA/IDExxxx）未成集；`CS1591` 已关，公共 API 文档无约束                                                 | `Directory.Build.props:NoWarn CS1591` 已移除                             | 中   | P0-15                  |
| A-11 | 风险·安全      | `CliCommandBuilder`/`CliAdapterBase` 392/470 行是命令构造安全敏感区；凭证格式兼容面（`FileCredentialVault` 671 行）                             | `Agents/*`、`CredentialVault/*`                                        | 中   | P0-3（已修注入，需守卫防回潮）      |
| A-12 | 文档·同步      | 无 ADR 记录重大架构决策；设计文档与代码靠人工同步；配置 schema 无机器可校验的单一事实来源                                                                       | 无 `docs/adr/`                                                         | 低   | P2-26（注释已清，决策未留痕）      |

> 诊断口径：严重度 = 对"正确性/可演进性/可运维性"三者的综合冲击，非仅看行数。A-1/A-2/A-3/A-8/A-9 为本期重点。

---

## 2. 重构目标与边界

### 2.1 目标原则

1. **业务功能零回归**：降级、免费时段、额度冷却、断点续跑、汇总报告行为与原版一致。
2. **对外接口契约不变**（最高优先级）：
   - CLI 调用方式（命令行参数、stdin/stdout 协议、headless 行为）
   - `config.json` 字段语义与向后兼容
   - 任务文件 sidecar（`<name>.md.result.json`）写入格式
   - `BatchReport` 报告结构
3. **架构可演进**：新增模型/账号/适配器不触达引擎核心；改动单点化。
4. **质量内建**：每一次提交都比上一次更难引入债务。

### 2.2 不动清单（禁止重写，继承并扩充 TECH-DEBT §3.3）

| 对象                                         | 禁止理由                                      |
| ------------------------------------------ | ----------------------------------------- |
| `BatchEngine` 的**业务语义**（降级矩阵/冷却/兜底计费/时段窗口） | 承载未被文档完整覆盖的领域知识，重写必丢语义 → 只能 strangler fig |
| `RunOptions` 40+ 配置项                       | 产品契约，重写 = 破坏用户配置兼容                        |
| `FileCredentialVault` 凭证格式与迁移路径            | 含隐性兼容约束                                   |
| Desktop 整体                                 | 先补测试再谈结构（P1-19 已补，但类仍胖，须"拆"非"重写"）         |
| 上述 4 类对外契约（CLI/config/sidecar/报告）          | 用户与上游集成依赖                                 |

### 2.3 改造清单（本期范围）

- 编排层瘦身（A-1）+ 单任务策略对象化（A-2）+ VM 分解（A-3）
- 重复代码收敛（A-4/A-5/A-6）
- 依赖方向固化与防腐层（A-7）
- 可观测性与日志统一（A-8）
- 工程化深化：CI 阻断 / 覆盖率门禁 / 静态扫描 / 多平台 / Release（A-9）、规范集（A-10）
- 安全守卫固化（A-11）、ADR 与配置 schema 单一事实来源（A-12）

### 2.4 禁止范围（红线）

- ❌ **禁止大爆炸式全量重写**（与约束一致，优先增量 strangler fig）。
- ❌ 禁止在未建立契约测试前改动降级/兜底/冷却/时段行为。
- ❌ 禁止引入改变部署形态的新运行时依赖（保持 Avalonia 自包含发布）。
- ❌ 禁止为"好看"做无收益 churn（沿用 TECH-DEBT §3.6 Boy Scout Rule，清理单独 `chore(debt):` 提交）。

### 2.5 兼容策略

- **接口契约优先**：所有对外行为经 `IBatchEngine`/`IEngineFactory` 等接口暴露，内部重构不影响调用方。
- **配置向后兼容**：新增字段 opt-in + 版本化迁移器；未知字段宽容解析；破坏性变更走主版本号。
- **sidecar 版本化**：写入带 `schema_version`，旧读取器能降级兼容。
- **内部开关仅用于可观测性**：不向用户暴露 feature flag；可观测埋点可经编译开关关闭。

---

## 3. 架构优化方案

### 3.1 目标分层（Ports & Adapters / 洋葱模型）

```
┌──────────────────────────────────────────────────────────┐
│  组合根 Composition Root                                   │
│  Desktop/Composition + EngineFactory（唯一 new 具体处）      │
└───────────────┬──────────────────────────────────────────┘
                │ 装配
   ┌────────────┴─────────────┬───────────────────┬──────────────────┐
   ▼                          ▼                   ▼                  ▼
[Agents 适配器]          [Infrastructure 适配器]   [Desktop UI]      [Tests]
 CLI: WB/CB/Mock          持久化/凭证/Git/Diff/     Avalonia VM/View  单元/集成/E2E
 (仅依赖 Core 抽象)        通知/日志/模板            (仅依赖 Core 抽象   契约/架构断言
                            (实现 Core 端口)          + 组合根端口)     可观测性埋点
                │                  │                     │
                └──────────────────┴─────────────────────┘
                                   │ 依赖（仅抽象）
                    ┌──────────────┴───────────────┐
                    ▼                               ▼
           [应用层 Core.Engine/           [领域内核 Core.Domain]
            Scheduling/Abstractions]       纯模型/值对象/枚举/策略
            用例编排·降级/冷却/兜底/        （零外部依赖）
            时段·工作流/DAG·报告聚合
```

**依赖方向铁律**（均可被 IL 扫描断言强制，见 §3.4）：

1. **领域内核**零外部依赖。
2. **应用层（Core）** 只依赖领域内核 + `Core.Abstractions` 中的端口接口；**不得**引用 Infrastructure/Agents/Desktop 任何具体类型。
3. **Infrastructure / Agents** 实现 Core 端口，不得被 Core 反向依赖。
4. **Desktop** 仅依赖 Core 抽象与组合根暴露的端口；除组合根外不得 `new` Infrastructure/Agent 具体类型。
5. **组合根**是唯一允许 `new` 具体类型并装配依赖图之处。

### 3.2 模块解耦与边界（上下文映射）

将系统划分为高内聚上下文，各自边界清晰、可独立测试：

| 上下文         | 边界/职责                                                           | 关键抽象                                                                                     |
| ----------- | --------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| 批处理上下文      | 批次级生命周期（Idle/Running/Waiting/Blocked/Terminated/Completed）、任务派发 | `IBatchEngine`、`ITaskDispatcher`                                                         |
| 单任务上下文      | 单任务状态机（重试/退避/降级/超时/终止）                                          | `ITaskRunner`、状态/转移/动作                                                                   |
| 可用性策略上下文    | 降级矩阵展开、冷却判定、兜底选取、免费时段窗口                                         | `DegradationPolicy`/`CooldownPolicy`/`FallbackPolicy`/`FreeTimeWindowPolicy`（**新增策略对象**） |
| 凭证上下文       | 多账号凭证切换、存储、迁移                                                   | `ICredentialVault`、`IEngineFactory` 装配                                                   |
| 工作流/Git 上下文 | 项目级任务、分支/PR、diff、Markdown 回写                                    | `IGitWorkflow`                                                                           |
| 报告上下文       | 用量/时间线/等待累计/汇总                                                  | `IBatchReportBuilder`、`IBatchNotifier`                                                   |
| 配置上下文       | schema、迁移、校验、快照                                                 | `IConfigStore`、迁移器、JSON Schema                                                           |
| UI 上下文      | 绑定、交互、呈现                                                        | ViewModel（退化）+ Coordinator/Presenter                                                     |

### 3.3 解耦抓手（本期新增的结构性改造）

- **策略对象化（A-2 根治）**：把 `TaskRunner` 中"降级/冷却/兜底/时段"判定抽为纯函数式 `Policy` 对象，输入领域状态、输出决策，可独立单测，不再与状态机纠缠。
- **WaitCoordinator（A-1 根治）**：把 `BatchEngine` 的"等待循环 + 唤醒 + 可用性轮询编排"下沉为独立协作者，facade 退化为"按序编排协作者 + 维护批次级状态"。**P-01 已落地**：`src/BatchAgentRunner.Core/Engine/WaitCoordinator.cs`（含 `WaitCoordinatorContext` 上下文注入），独占等待门 `_waitGate`/唤醒源 `_wake`/取消标记 `_waitingCancelled`，对外暴露 `WaitForRestoreAsync`/`CancelWaiting`/`SignalWake`，facade 仅委托调用，公开行为逐字节等价。
- **防腐层固化（A-7）**：`TaskItem`（领域）↔ `TaskItemViewModel`（UI）之间禁止 UI 直接绑领域（沿用 P2-7 已铺路）；XAML 色值一律走 token 映射层（P2-10/11）。
- **VM 分解（A-3）**：引入 Coordinator/Presenter 承载交互逻辑，VM 退化为"状态持有 + 绑定"；`ConfigViewModel` 按"账号卡 / 降级 / 通知 / Git / 通用"切片为子编辑器 VM + 共享校验器。

### 3.4 架构约束自动化（IL 扫描断言清单）

将 §3.1 铁律固化为测试（沿用既有 `§4.3` 架构断言，扩充条目）：

| 断言                                                          | 失败即红    |
| ----------------------------------------------------------- | ------- |
| Core 不得引用 Infrastructure/Agents/Desktop 具体命名空间              | 跨层倒置    |
| Infrastructure 不得被 Core 工程引用（反向依赖）                          | 端口反转    |
| Desktop 除 `Composition` 外不得 `new` Infrastructure/Agent 具体类型 | UI 成组合根 |
| 公共资源（JsonStore/路径常量/Truncate/Clone）须来自 Core 或共享 Internal    | 重复实现回潮  |
| `BatchEngine`/`TaskRunner`/最大 VM 行数 ≤ 阈值                    | 上帝类回潮   |

---

## 4. 代码重构策略

### 4.1 命名与可读性

- 语义命名统一：消除残留"二期扩展/遗留/暂不支持"等误导性注释（P2-26 已清大部分，余下随重构确认）。
- 端口命名 `I*` 一致；避免缩写与模糊名（如 `evaluator` → `availabilityEvaluator`）。
- 恢复 `CS1591` 要求，公共 API 必须 XML 文档（A-10）。

### 4.2 职责拆分模式

| 模式                         | 适用                                       | 落点                                 |
| -------------------------- | ---------------------------------------- | ---------------------------------- |
| 编排 → 策略对象                  | 降级/冷却/兜底/时段判定                            | 抽 `Policy` 对象（§3.3），纯单测            |
| 巨类 → 协作者                   | `BatchEngine` facade                     | `WaitCoordinator` + 既有 6 协作者（§3.3） |
| 状态机 → 状态/转移/动作三层           | `TaskRunner` 903 行                       | 状态表 + 动作委托，CC 降到可测区间               |
| VM → Coordinator/Presenter | `Config/Monitor/Login/TemplateEditor` VM | 交互逻辑外移，VM 退化                       |
| 防腐层                        | 领域 ↔ UI                                  | `TaskItemViewModel` 全量落地           |

### 4.3 消除重复（A-4/A-5/A-6）

- **Json*Store 抽 `JsonStoreBase<T>`**（P2-31）✅ **已落地（2026-10-21，见 TECH-DEBT §5.11）**：构造/默认目录/`LoadAsync` 三条回落（文件缺失 / `JsonException` / 反序列化为空）/ `AtomicFileWriter` 原子保存统一到基类，派生类只留 `CreateEmpty()` 与可选 `NormalizeAsync()` 自愈·迁移钩子；9 处魔法串收为 `AppPaths` 常量类（`Root` / `ConfigDirectory` / `TemplatesDirectory` / `VaultDirectory` / `RunsDirectory` / `LogsDirectory` / `MockLiveDirectory`），并由 `AppPathsHygieneTests` 守卫防复发。行为与落盘字节零变化。
- **领域工具收口到 Core**（P2-1~6）：`Truncate`/`IsTerminal`/`Clone`/`失败分派` 提为内核纯函数，旧三份实现删除。
- **RunOptions/ConfigEditors 泛型化**（P2-9/P2-15）：编辑器走 `FieldEditor<T>` + 统一校验反馈；配置项用记录类型或源码生成收口，弃用 JSON 往返克隆。

### 4.4 简化逻辑

- 用多态/查表替代 `switch + goto`（P1-2 已在外层做，内层继续）。
- 消除魔法串：API 常量已抽 `CodeBuddyApiConstants`（P1-21），路径/配置键同理。
- 圈复杂度目标：单方法 CC ≤ 15，单文件 ≤ 400 行（A-1/A-2/A-3 验收）。

### 4.5 异常处理与关键不变量

- **异常分类**：预期业务异常（时段外/额度冷却/降级切换）vs 意外异常（IO/进程/网络/解析）。
- **预期异常显式化**：用 `Result`/`Outcome` 类型表达，不靠 try-catch 吞掉；预期异常**不打日志、不计失败率、不触发误切换告警**（设计已定义，代码层显式落地）。
- **原子写守卫**：结果文件一律 `AtomicFileWriter`（P0-8 已做），以 §3.4 断言防回潮。
- **危险操作**：统一经 `DangerousOperationGuard`（已有），不新增绕过路径。

---

## 5. 工程化治理方案

### 5.1 目录与工程规范

- 保留现有四工程分层；每工程下明确 `Abstractions/`（端口，public）、`Internal/`（内部实现，`InternalsVisibleTo` 仅对测试开放）。
- 测试工程与被测工程 1:1 镜像；新增 `docs/adr/` 存架构决策记录。
- 禁止在 `src` 外提交产物（`_harness`/备份/`*.backup-*` 已由 `.gitignore` 目录级规则覆盖，P2-29）。

### 5.2 编码规范

- 已启用：`Nullable`、`ImplicitUsings`、`LangVersion=latest`、`TreatWarningsAsErrors=true`、`EnforceCodeStyleInBuild=true`、`AnalysisLevel=latest-recommended`。
- **目标**：`.editorconfig` 从 28 行扩充为团队级规则集（命名 `naming_style`、关键 `CAxxxx`/`IDExxxx`、文件头/排序）；恢复 `CS1591` 并强制公共 API 文档。
- 命名空间 = 目录结构；`using` 静态导入克制使用。

### 5.3 测试金字塔与覆盖率

| 层级                | 占比目标 | 内容                                         | 覆盖率目标                       |
| ----------------- | ---- | ------------------------------------------ | --------------------------- |
| 单元（纯逻辑/策略/领域）     | ~70% | Policy 对象、降级矩阵、终态判定、报告聚合                   | Core/Engine **≥ 85%**       |
| 集成（端口实现/注入真实进程）   | ~20% | CLI 适配器、Git、凭证、存储（经 `IProcessLauncher` 注入） | Infrastructure **≥ 70%**    |
| E2E（headless 全流程） | ~10% | 真实任务清单跑批、降级/续跑/报告                          | Desktop **≥ 50%**（headless） |


- **契约测试专桶**：守护 `IBatchEngine` 行为、config schema、sidecar 格式、报告结构——任何破坏对外契约的改动直接红。
- 继承既有 `Trait("Category",...)` 分层，CI 快跑单元 / 慢跑集成。

### 5.4 CI/CD 进阶（A-9）

当前 CI "只报告不阻断、单平台、无扫描/覆盖率"。目标管线：

1. **门禁转阻断**：`build`（全量 `--no-incremental`）+ 单元测试作为 required status check（branch protection）。
2. **覆盖率采集 + 门禁**：`coverlet` 采集，`< 目标阈值`即失败。
3. **格式门禁**：`dotnet format --verify-no-changes` 进 CI（目前仅 pre-commit 钩）。
4. **架构测试桶**：§3.4 的 IL 扫描断言独立跑。
5. **静态安全扫描**：Roslyn 分析器分级 + 安全扫描（命令注入/路径遍历/凭证硬编码），重点 `CliCommandBuilder`/`CliAdapterBase`/`FileCredentialVault`。
6. **多平台矩阵**：`windows / macOS / linux`（Avalonia 跨平台，当前只验 Windows 是缺口），先非阻断跑、稳定后转阻断。
7. **Release 流水线**：`dotnet publish` 自包含包 + 版本号（`GitVersion`）+ 产物签名/校验。

### 5.5 静态扫描与质量门

- 分析器规则集：warning→error 分级，新警告本地与 CI 均失败（已靠 `TreatWarningsAsErrors` 兜底）。
- 依赖扫描（ Supply-chain ）：CPM 已 centralized，定期 `dotnet list package --vulnerable`。
- 代码质量看板：复杂度、重复率、覆盖率趋势（TECH-DEBT §4.8 度量看板复用）。

### 5.6 日志与可观测性（A-8，本期重点）

- **统一结构化日志**：`IRunLogger` 接入结构化实现（如 `ILogger`/Serilog），统一级别与字段。
- **关联标识**：`RunId`/`TaskId` 贯穿批次与任务，日志可按批次聚合。
- **运行级指标**（新增，不侵入业务语义）：任务吞吐、平均耗时、降级次数、兜底触发、冷却跳过、失败率、**误切换率**（设计已定义"时段外/冷却跳过不计失败"）。
- **敏感脱敏**：凭证/密钥绝不落日志；报告与日志可关联但分离存储。
- 指标与 `BatchReport` 互补：报告给用户看，指标给运维看。

### 5.7 配置治理（A-6/A-12）

- **schema 单一事实来源**：由 `RunOptions` 生成 JSON Schema，CI 校验 `config.json`。
- **迁移框架**：版本化迁移器，新增字段默认兼容；破坏性变更走主版本。
- **约定固化**：snake_case + 派生属性 `[JsonIgnore]`（已实现，需文档化 + §3.4 断言守卫）。
- **密钥隔离**：凭证不入 config（已由 Vault 处理），配置仅存非敏感项。

### 5.8 文档与决策留痕

- `DESIGN` 维持单一设计权威源；重大架构决策写 **ADR**（`docs/adr/`，含状态/上下文/决策/后果）。
- 对外契约（CLI 参数、config schema、sidecar 格式、报告结构）半自动生成文档。
- 代码↔文档同步靠 PR 评审清单 + 架构测试守卫，而非人肉。

---

## 6. 分阶段落地计划（增量 strangler fig，禁止大爆炸）

> **节奏校准（用户确认）**：每阶段 ≈ **5 人天**，小批量增量交付。每个阶段必须**独立可编译、全量测试绿、不破坏对外契约**，并可单独回滚。阶段间按依赖顺序串行，主题内可并行。
>
> 基线（已完成，2026-10-17~20）= TECH-DEBT 阶段 −1/0/1/2：止血、建网、动骨（BatchEngine 拆 6 协作者、提接口、DI、端口抽离、Desktop 测试基座）。以下为**剩余 14 个 5 人天阶段**（合计 ≈ 70 人天），分主题 A/B/C；主题 D 为长期持续化，不计入固定阶段。

### 6.1 阶段总览（5 人天/阶段）

| 阶段       | 主题     | 范围          | 关键交付                                                                | 依赖   | 退出标准                            |
| -------- | ------ | ----------- | ------------------------------------------------------------------- | ---- | ------------------------------- |
| **P-01** | A 架构收口 | A-1 (①)     | 抽取 `WaitCoordinator`（等待循环/唤醒/可用性轮询下沉）                               | —    | ✅ 已收口（2026-10-21）：diff<400 行；facade 1018→953 行；IL 绿；测试绿 |
| **P-02** | A 架构收口 | A-1 (②)+A-7 | `BatchEngine` facade 收口 ≤400 行 + 防腐层/依赖方向 IL 断言固化                   | P-01 | facade ≤400 行；IL 断言全绿           |
| **P-03** | A 架构收口 | A-2         | `TaskRunner` 策略对象化（Degradation/Cooldown/Fallback/FreeTime → Policy） | —    | TaskRunner ≤400 行；Policy 单测覆盖   |
| **P-04** | A 架构收口 | A-3 (①)     | `ConfigViewModel` 分解（账号卡/降级/通知/Git/通用 子编辑器 + 共享校验）                  | P-02 | 最大 VM ≤400 行；headless 契约测试绿     |
| **P-05** | A 架构收口 | A-3 (②)     | `TemplateEditorViewModel` 分解（引入 Coordinator/Presenter）              | P-02 | 同上                              |
| **P-06** | A 架构收口 | A-3 (③)     | `MonitorViewModel` + `LoginViewModel` 分解收口                          | P-02 | 同上                              |
| **P-07** | B 去重   | A-4         | `JsonStoreBase<T>` + `AppPaths` 常量（消除 3 份同构 + 9 处魔法串）               | —    | §3.4 重复断言绿；测试绿                  |
| **P-08** | B 去重   | A-5         | 领域工具收口 Core（Truncate/终态/Clone/失败分派 单一来源）                            | —    | 无三份同构；内核工具单测                    |
| **P-09** | B 去重   | A-6         | `ConfigEditors` 泛型化 + `RunOptions` 收口（弃 JSON 往返克隆）                  | P-07 | 编辑器泛型化；配置契约测试绿                  |
| **P-10** | C 工程化  | A-8         | 可观测性：结构化日志接线 + RunId 关联 + 运行级指标埋点                                   | —    | 日志可关联；指标可采集；脱敏有效                |
| **P-11** | C 工程化  | A-9(①)      | CI 转阻断（required status）+ 覆盖率门禁 + 格式门禁进 CI                           | P-07 | 构建/单测阻断生效；覆盖率阈值拦截               |
| **P-12** | C 工程化  | A-10/A-11   | `.editorconfig` 规则集扩充 + 恢复 `CS1591` + 静态安全扫描固化命令/凭证守卫               | P-11 | 扫描 0 高危；公共 API 有文档              |
| **P-13** | C 工程化  | A-9(②)      | 多平台 CI 矩阵（win/mac/linux）+ Release 流水线（publish+版本号+签名）               | P-11 | 多平台绿；制品可发布                      |
| **P-14** | C 工程化  | A-12        | ADR 机制 + 配置 JSON Schema 单一事实来源 + CI 校验                              | P-09 | ADR≥关键决策；schema 校验生效            |

> 主题 D（持续化）：机会主义偿还剩余 P2、月度台账复核、性能基线——**长期进行，不绑定 5 人天阶段**。

### 6.2 各阶段细则（范围 / 准入 / 退出 / 风险）

**P-01 · WaitCoordinator 抽取（A-1 ①，5 人天）**

- 步骤：识别 `BatchEngine` 内"等待循环/唤醒(`_wake`)/可用性轮询编排"代码 → 抽为 `WaitCoordinator` 协作者，facade 仅委托调用；保留 `IBatchEngine` 公开行为不变。
- 准入：先确认/补充 `BatchEngine` 编排相关的契约测试（防等待/唤醒回归）。
- 退出：单步 `git diff --stat` < 400 行；facade 行数下降且编译/全量测试绿；§3.4 IL 断言绿。
- 风险：R-1（语义回归）→ 契约测试 + 不变量测试守护。

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - 新增 `src/BatchAgentRunner.Core/Engine/WaitCoordinator.cs`（156 行，含 `WaitCoordinatorContext` 上下文注入，沿用 `TaskDispatcherContext`/`TaskRunnerContext` 范式）：封装等待循环 / 唤醒（`_wake`）/ 可用性轮询编排，独占 `_waitGate`/`_wake`/`_waitingCancelled`，对外 `WaitForRestoreAsync`/`CancelWaiting`/`SignalWake`（实现 `IDisposable` 释放等待门，由 `BatchEngine.Dispose()` 一并释放）。
> - `BatchEngine` 改造：移除 3 个等待态字段与 ~80 行等待循环体，新增 `_waitCoordinator` 字段并在 ctor 经 `BuildWaitCoordinatorContext()` 装配；`CancelWaiting`/`NotifyConfigurationChanged`/`TerminateAsync`/`Dispose` 改为委托/调用协调者；保留 `IBatchEngine` 公开行为逐字节等价。
> - **准入契约测试**：既有 waiting 测试已覆盖等待→恢复/超时阻塞/额度耗尽无限等待；**新增 2 项契约测试**锁死 P-01 抽取路径——「等待中 `CancelWaiting()` → 转入 Blocked」、「等待中 `NotifyConfigurationChanged()` 补充可用组合 → 提前退出等待并继续」（用 `FakeClock` 冻结模式确定性复现唤醒时机）。
> - **退出验证**：`git diff --stat`（BatchEngine.cs）135 行变动（1018→953，下降 65 行），新文件 156 行，单步远 < 400 行；全量 `--no-incremental` 构建 **0 警告/0 错误**；全量测试 **652 通过/0 失败**（主 540 + Desktop 112，较收口前 +2）；§3.4 IL 断言（LayeringRulesTests 12 项）全绿；facade 行数下降达标（剩余 "≤400 行" 收口见 P-02）。
> - 配套：测试 helper `FakeClock.AutoAdvance` 由 `init` 改为 `set`（仅测试项目，支持"冻结→唤醒"确定性等待测试）。

**P-02 · facade 收口 + 依赖方向固化（A-1 ② + A-7，5 人天）**

- 步骤：将 facade 残余编排下沉/委托给既有 6 协作者，使其 ≤400 行；落地"防腐层 + 依赖方向"IL 扫描断言（§3.4 第 1~3 条），固化 Desktop 不得直 `new` Infrastructure/Agent 具体类型。
- 退出：facade ≤400 行；IL 断言全绿；全量测试绿。
- 风险：R-2（大改动 review 失效）→ 拆为 <400 行小提交。

**P-03 · TaskRunner 策略对象化（A-2，5 人天）**

- 步骤：将"降级/冷却/兜底/免费时段"判定抽为纯函数式 `Policy` 对象（输入领域状态、输出决策），`TaskRunner` 退为"状态机驱动 + 调 Policy"，目标 ≤400 行、CC 可测。
- 准入：降级矩阵/冷却/兜底不变量测试先就位（P0-1/P0-5 不变量）。
- 退出：TaskRunner ≤400 行；Policy 单测覆盖四类决策；业务行为等价（契约测试锁定）。
- 风险：R-1 → 不变量 + E2E 真实任务清单回归。

**P-04~P-06 · VM 分解（A-3，各 5 人天）**

- 步骤：引入 Coordinator/Presenter 承载交互逻辑，VM 退化为"状态持有 + 绑定"；按切片拆子编辑器 + 共享校验器；全量落地 `TaskItemViewModel` 防腐层（UI 不得直绑 `TaskItem`）。
- 准入：目标 VM 先补 Avalonia headless 契约测试（P1-19 基座已具备）。
- 退出：最大单个 VM ≤400 行；headless 契约测试绿；UI 行为等价。
- 风险：R-4（破坏 UI 行为）→ headless 契约测试先补再改。

**P-07 · JsonStoreBase<T> + AppPaths（A-4，5 人天）**

- 步骤：抽 `JsonStoreBase<T>`（构造/默认目录/`LoadAsync` catch/`AtomicFileWriter.SaveAsync` 统一），9 处路径魔法串收为 `AppPaths` 常量；三份 `Json*Store` 改为继承。
- 退出：无三份同构实现；§3.4 重复断言绿；测试绿。

**P-08 · 领域工具收口（A-5，5 人天）**

- 步骤：`Truncate`/`IsTerminal`/`Clone`/`失败分派状态机` 提为 Core 纯函数，删除重复实现（P2-1~6）。
- 退出：公共工具单一来源；内核工具单测；§3.4 重复断言绿。

**P-09 · ConfigEditors/RunOptions 收口（A-6，5 人天）**

- 步骤：`ConfigEditors` 改为泛型 `FieldEditor<T>` + 统一校验反馈（P2-15）；`RunOptions` 弃用 JSON 往返克隆，改用记录类型/显式映射（P2-9）。
- 退出：编辑器泛型化；配置读写契约测试绿；克隆行为等价。

**P-10 · 可观测性（A-8，5 人天）**

- 步骤：`IRunLogger` 接结构化实现 + `RunId`/`TaskId` 贯穿；新增运行级指标（吞吐/平均耗时/降级次数/兜底触发/冷却跳过/失败率/误切换率）；敏感信息脱敏。
- 退出：日志按批次聚合；指标可采集；凭证/密钥不落日志。

**P-11 · CI 阻断 + 覆盖率/格式门禁（A-9 ①，5 人天）**

- 步骤：`build` + 单元测试设为 required status check；`coverlet` 采集覆盖率并按阈值拦截；`dotnet format --verify-no-changes` 进 CI。
- 退出：阻断生效；覆盖率低于目标即失败；格式门禁生效。
- 风险：R-3（质量下滑）→ 本阶段即转阻断。

**P-12 · 规范集 + 静态安全扫描（A-10/A-11，5 人天）**

- 步骤：`.editorconfig` 扩充团队级规则集（命名/关键 `CAxxxx`/`IDExxxx`），恢复 `CS1591` 强制公共 API 文档；接入静态安全扫描，固化 `CliCommandBuilder`/`CliAdapterBase`/`FileCredentialVault` 守卫。
- 退出：扫描 0 高危；公共 API 有 XML 文档；新警告本地+CI 均失败。

**P-13 · 多平台矩阵 + Release（A-9 ②，5 人天）**

- 步骤：CI 加 `macOS`/`linux` 矩阵（先非阻断跑、稳定后转阻断）；新增 Release 流水线（publish 自包含 + `GitVersion` 版本号 + 签名/校验）。
- 退出：多平台绿；制品可发布且可回退。
- 风险：R-6（多平台暴露隐蔽缺陷）→ 先非阻断。

**P-14 · ADR + 配置 Schema（A-12，5 人天）**

- 步骤：建立 `docs/adr/`，为重大架构决策补 ADR；由 `RunOptions` 生成 JSON Schema 并接入 CI 校验 `config.json`。
- 退出：ADR≥关键决策数；schema 校验生效；设计文档与代码同步机制就位。

### 6.3 人天预算汇总

| 主题            | 阶段数          | 人天     | 占比   |
| ------------- | ------------ | ------ | ---- |
| A 架构收口        | P-01~P-06（6） | 30     | 43%  |
| B 去重          | P-07~P-09（3） | 15     | 21%  |
| C 工程化         | P-10~P-14（5） | 25     | 36%  |
| **合计（A/B/C）** | **14**       | **70** | 100% |
| D 持续化         | 长期           | —      | 不计入  |

> 注：5 人天为单阶段上限估算，含设计/编码/自测/评审；实际按"完成定义"（退出标准）而非日历时间收口。每阶段建议 1 人专注或 2 人结对（总投入≈5 人天），避免跨阶段并行导致契约不一致。

**每步护栏（继承 TECH-DEBT §2 阶段 2）**：单步 `git diff --stat` > 400 行则回退重切；每步独立提交且信息合规（含 `chore(debt):` 清理单独提交）；每步可编译、全量测试绿、不破坏对外契约。

---

## 7. 风险清单 + 回滚方案 + 验收标准

### 7.1 风险清单

| ID  | 风险                 | 可能性 | 影响 | 缓解                                         |
| --- | ------------------ | --- | -- | ------------------------------------------ |
| R-1 | 重构引入语义回归（降级/兜底错）   | 中   | 高  | 契约测试 + 不变量测试守护（P0-1/P0-5 不变量）；E2E 真实任务清单回归 |
| R-2 | 大文件改动致 review 失效   | 高   | 中  | 单步 ≤ 400 行；清理单独 `chore(debt):` 提交          |
| R-3 | CI 不阻断致质量下滑        | 中   | 高  | Wave C 转阻断 + 覆盖率门禁                         |
| R-4 | VM 分解破坏 UI 行为      | 中   | 高  | Avalonia headless 契约测试先补（P1-19 基座）         |
| R-5 | 配置 schema 变更破坏用户配置 | 低   | 高  | 迁移器 + 向后兼容 + 保存前备份（P0-9 思路）                |
| R-6 | 多平台 CI 暴露隐蔽缺陷      | 中   | 中  | 先非阻断跑，稳定后转阻断                               |
| R-7 | 覆盖率目标过高拖累交付        | 中   | 低  | 分层目标、关键路径优先；不追求 100%                       |

### 7.2 回滚方案

- **增量提交 + 每步可编译可测**：任何一步出问题 `git revert` 单提交，不影响其他。
- **strangler 双轨**：新代码走接口/协作者，旧路径经 facade shim 保留，必要时 env/开关或 revert 切回；不删除旧路径直到新路径契约测试全绿且灰度通过。
- **配置可逆**：迁移器带版本号 + 保存前备份（`*.backup-*` 已 ignore）；破坏性变更独立主版本，旧读取器降级兼容。
- **发布灰度**：自包含包发布，失败回退上一版本（制品保留 + 版本号）。
- **文件格式变更**：sidecar 带 `schema_version`，旧读取器能读新写、新读取器能降级读旧。

### 7.3 验收标准（可量化）

| 维度 | 验收阈值                                                                                                      |
| -- | --------------------------------------------------------------------------------------------------------- |
| 构建 | 全量 `--no-incremental` **0 警告 0 错误**（继承）                                                                   |
| 测试 | 全量绿；Core/Engine **≥ 85%**、Infrastructure **≥ 70%**、Desktop **≥ 50%**；契约测试覆盖 `IBatchEngine`/config/sidecar |
| 架构 | §3.4 IL 断言全绿；`BatchEngine` facade **≤ 400 行**；`TaskRunner` **≤ 400 行**；最大单个 VM **≤ 400 行**                |
| 工程 | CI 阻断生效；覆盖率门禁生效；静态扫描 **0 高危**；多平台 CI 绿                                                                    |
| 业务 | 端到端跑真实任务清单，降级/兜底/冷却/时段/续跑行为与原版一致（契约测试锁定）                                                                  |
| 文档 | ADR ≥ 关键决策数；设计文档与代码同步；配置 JSON Schema 存在且 CI 校验                                                            |

---

## 8. 交付物清单

### 8.1 文档类

- `docs/ARCH-架构重构与工程化治理方案.md`（本方案）
- `docs/TECH-DEBT-技术债务治理方案-v2.0.md`（持续更新剩余项）
- `docs/adr/`（架构决策记录：分层模型、策略对象化、契约守卫、CI 阻断等）
- `docs/API-契约文档.md`（CLI 参数 / config schema / sidecar 格式 / 报告结构）
- `docs/config.schema.json`（配置机器可校验单一事实来源）
- `docs/回滚-Runbook.md`（发布回退、配置迁移回滚步骤）

### 8.2 代码模块类

- 重构后引擎协作者：`WaitCoordinator` 等
- 策略对象集：`DegradationPolicy` / `CooldownPolicy` / `FallbackPolicy` / `FreeTimeWindowPolicy`
- VM 子编辑器 / Coordinator / `TaskItemViewModel` 防腐层
- `JsonStoreBase<T>` + `AppPaths` 常量（去重）
- Core 共享工具（Truncate/终态/Clone 收口）
- 可观测性组件（结构化日志接线 + 运行级指标采集）

### 8.3 工程配置类

- 扩充后的 `.editorconfig`（团队级规则集）
- `ci.yml` 阻断版（覆盖率 + 格式 + 架构测试 + 静态扫描 + 多平台矩阵）
- 静态扫描配置（分析器规则集 + 安全扫描）
- `Release` 流水线（publish + 版本号 + 签名）
- `GitVersion` / 版本策略配置

### 8.4 测试类

- 契约测试套件（IBatchEngine / config / sidecar / 报告）
- Policy 对象单测（降级/冷却/兜底/时段）
- VM 契约测试（headless）
- §3.4 架构 IL 扫描断言
- 覆盖率报告与趋势看板

### 8.5 度量类

- 代码度量基线表（行数/复杂度/覆盖率，附录 A）
- 月度技术债务趋势看板（复用 TECH-DEBT §4.8）

---

## 附录 A：代码度量基线（现状，2026-10-20 实测）

| 文件                                                      | 行数   | 角色         | 本期目标                      |
| ------------------------------------------------------- | ---- | ---------- | ------------------------- |
| `Core/Engine/BatchEngine.cs`                            | 953  | 编排 facade  | ≤ 400（WaitCoordinator 已下沉，剩余 facade 收口见 P-02） |
| `Core/Engine/TaskRunner.cs`                             | 903  | 单任务状态机     | ≤ 400（策略对象化）              |
| `Desktop/ViewModels/ConfigViewModel.cs`                 | 1065 | 配置 VM      | ≤ 400（切片子编辑器）             |
| `Desktop/ViewModels/MonitorViewModel.cs`                | 983  | 监控 VM      | 分解                        |
| `Desktop/ViewModels/LoginViewModel.cs`                  | 751  | 登录 VM      | 分解                        |
| `Desktop/ViewModels/TemplateEditorViewModel.cs`         | 674  | 模板 VM      | 分解                        |
| `Infrastructure/CredentialVault/FileCredentialVault.cs` | 671  | 凭证存储       | 守卫固化（禁重写）                 |
| `Core/Domain/WorkflowTypes.cs`                          | 512  | 工作流类型      | 维持                        |
| `Desktop/Models/ConfigEditors.cs`                       | 509  | 手写编辑器      | 泛型化（P2-15）                |
| `Core/Options/RunOptions.cs`                            | 369  | God Config | 收口（P2-9）                  |
| `Agents/Adapters/CliAdapterBase.cs`                     | 392  | CLI 适配基类   | 安全守卫                      |
| `Agents/Internal/CliProcessRunner.cs`                   | 470  | 进程运行       | 安全守卫                      |
| `Core/Engine/WaitCoordinator.cs`                       | 156  | 等待协调者（P-01 新增） | 维持（从 BatchEngine 下沉）      |

> 测试：652 个测试方法（截至 2026-10-21；P-01 新增 2 项等待/唤醒契约测试，由 650 → 652）；CI 触发 `push`/`PR` 至 `master`，全量 `--no-incremental` 构建 + 分层测试，当前"只报告不阻断"。

## 附录 B：本方案 ↔ TECH-DEBT v2.0 映射

| 本方案章节            | 对应债务条目                       |
| ---------------- | ---------------------------- |
| §1.2 A-1/A-2/A-3 | P1-1 / P1-2 / P1-19（收口）      |
| §1.2 A-4/A-5/A-6 | P2-31 ✅（2026-10-21 已收口，见 TECH-DEBT §5.11）/ P2-1~6 / P2-9、P2-15  |
| §1.2 A-7         | P1-4（§4.3 断言）                |
| §1.2 A-8         | 工程化新增缺口                      |
| §1.2 A-9         | P0-16 / §4.9                 |
| §1.2 A-10        | P0-15                        |
| §1.2 A-11        | P0-3（守卫）                     |
| §1.2 A-12        | P2-26 / 文档同步                 |
| §3~§6 全部         | 阶段 3（持续化）+ 阶段 4 机会主义偿还的架构化编排 |

## 附录 C：架构依赖规则（IL 扫描断言，§3.4 落地）

1. `Core` 工程禁止引用 `Infrastructure`/`Agents`/`Desktop` 具体命名空间。
2. `Infrastructure`/`Agents` 不得出现在 `Core` 的 `ProjectReference`（仅 `Core.Abstractions`）。
3. `Desktop`（除 `Composition`）禁止 `new` Infrastructure/Agent 具体类型。
4. 公共工具（`JsonStore`/`AppPaths`/`Truncate`/`Clone`/`IsTerminal`）仅允许来自 Core 或声明为 `Internal` 的共享库。
5. `BatchEngine`/`TaskRunner`/最大 VM 行数硬上限（见 §7.3）。
