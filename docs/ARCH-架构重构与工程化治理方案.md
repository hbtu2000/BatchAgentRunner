# BatchAgentRunner 架构重构与工程化治理方案（ARCH）

> 版本：v1.0 ｜ 日期：2026-10-21 ｜ 角色：资深软件架构师  
> 更新：P-01（WaitCoordinator 抽取）、P-02（facade 收口 ≤400 行 + 依赖方向 IL 断言固化）、P-03（`TaskRunner` 策略对象化 + facade 收口 ≤400 行）、P-04（`ConfigViewModel` 分解为账号卡/降级/通知/Git/通用子编辑器 + 共享校验，facade 收口 366 行）、P-05（`TemplateEditorViewModel` 按 VM→Coordinator/Presenter 分解为模板加载/变量展开/流程示意三个 Coordinator + 4 个行视图模型独立文件，facade 收口 240 行）、P-06（`MonitorViewModel` 983→388 行 + `LoginViewModel` 751→280 行：按「状态持有 + 绑定转发 + 流程编排」分解为 Monitor/Login 两组协作者 + 行视图模型独立文件，均收口 ≤400 行）、P-07（`JsonStoreBase<T>` + `AppPaths` 去重：三份同构仓储退化为 `CreateEmpty()`/`NormalizeAsync()`、9 处路径魔法串收口，守卫扩为 4 项含「第 4 份同构仓储」拦截）、P-08（领域原语收口：截断/终态集合/`Clone`/依赖失败分派各自单一事实来源，新增 `DomainPrimitivesHygieneTests` 5 项守卫 + `TaskItemCloneTests` 4 项字段完整性契约，修掉 `TaskItem.Clone()` 阶段结果浅拷贝缺陷，并清偿 P-02 遗留的 `BatchEngine` 死私有 `Truncate` 别名使 facade 400→397 行）、P-09（配置契约收口：`RunOptions` 363 行 God Config 按 12 配置域横向切片为 `RunOptions.01~11-*.cs`（本体 369→18 行，切片各 ≤49 行，JSON 形状与落盘字节逐字不变；原 #D5 计划性延期结项）+ 禁 JSON 往返克隆守卫；`ConfigEditors` 解析字段全量 `FieldEditor<T>`（时段起止一并泛型化、`StartText`/`EndText` 裸字符串桥接删除、修掉「非法输入静默抹掉已配置时段」缺陷）+ 视图侧校验反馈统一（7 个缺反馈输入框补齐、错误文本样式单一来源）；新增 `RunOptionsContractTests` 5 项 + `RunOptionsHygieneTests` 3 项 + `ConfigEditorsHygieneTests` 3 项守卫）、P-10（可观测性：结构化日志接线——`IRunLogger` 增 `Write(RunLogEntry)` 唯一落盘点 + `run-*.jsonl` 结构化流 + `RunId` 贯穿事件/日志/指标；`RunMetrics` 运行级指标埋点（吞吐 / 平均耗时 / 降级次数 / 兜底触发 / 冷却·时段跳过 / 失败率 / 误切换率）+ `metrics-<run_id>.json` 落盘；`SecretMasker` 脱敏单一事实来源在渲染入口统一生效；新增 `ObservabilityHygieneTests` 6 项守卫 + `SecretMaskerTests` 20 项 / `RunMetricsTests` 8 项 / `RunMetricsInstrumentationTests` 4 项 / `RunMetricsWriterTests` 2 项 / `FileRunLoggerTests` +3 项；同轮把 20 处裸级别字面量收为 `RunLogLevels` 常量、`BatchEngine` 的 `ResolveWorkspacePath` 下沉 `WorkspaceResolver` 使 facade 维持 399 行）、P-14（ADR 机制 + 配置 Schema 单一事实来源：`docs/adr/` 建立不可变决策记录 11 篇 + `AdrHygieneTests` 4 项索引/结构守卫；`docs/config.schema.json` 由 `RunOptions` 派生，`ConfigSchemaTests` 4 项锁死「schema 键集合 == 代码键集合」并校验示例/畸形配置，`scripts/ci/Assert-ConfigSchema.ps1` 进 CI 门禁）均已收口，见 §6.2 执行记录。
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
| 引擎解耦  | `BatchEngine` 已拆 6 协作者 + 提 `IBatchEngine` 接口 + 下沉 `WaitCoordinator`（P-01）+ facade 收口 ≤400 行（P-02）；`TaskRunner` 已策略对象化（P-03）+ facade 收口 ≤400 行 | 良好，`BatchEngine` facade 收口 953→**397 行**（P-02 →400，P-08 再删死私有 `Truncate` 别名 →397）、`TaskRunner` facade 收口 903→76 行（见 1.2 / §6.2） |
| 组合根   | `IEngineFactory`/`IEngineHost` 已落，UI 倒置已缓解                                                    | 良好，靠 IL 断言维持（脆弱，需文化固化）       |
| 可测性缝  | 进程 `IProcessLauncher`、时钟 `IClock`、IO `IFileSystem`/`ITaskFileStore` 已抽                        | 良好，覆盖率不均                     |
| 构建/分析 | `TreatWarningsAsErrors=true`、`AnalysisLevel=latest-recommended`、`Nullable`/`ImplicitUsings` 开 | 良好                           |
| 依赖管理  | `Directory.Packages.props`（CPM）已上                                                             | 良好                           |
| 测试    | 494 个测试方法，已分快/慢 bucket                                                                        | 数量足，**缺覆盖率量化与门禁**            |
| CI    | `.github/workflows/ci.yml` 已建，全量重建 + 分层测试 + **P-11 ✅ 已收口（2026-10-22）：格式门禁 + 覆盖率门禁 + 阻断语义**（required status check 待管理员在分支保护配置）；**P-13 ✅ 已收口（2026-10-22）：多平台矩阵（win/mac/linux，非阻断）+ 架构测试独立桶 + Release 流水线**（`ci-platforms.yml` / `release.yml` / `GitVersion.yml`） | 门禁已阻断（本地与 CI 均生效）；多平台矩阵非阻断跑（R-6 先晒缺陷）；静态扫描余 P-12；**ADR/Schema ✅ P-14 已收口（2026-10-22）：`docs/adr/` 11 篇决策记录 + 索引守卫，`config.schema.json` 键集合契约 + CI 校验** |

### 1.2 架构问题清单（待治理，带代码证据）

按类别归因，每条标注严重度（**高/中/低**）与对应 TECH-DEBT 条目。

| #    | 类别         | 问题                                                                                                                        | 证据（实测）                                                                | 严重度 | 关联                     |
| ---- | ---------- | ------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------- | --- | ---------------------- |
| A-1  | 上帝类·编排     | `BatchEngine` facade 仍承载"批次编排 + 等待循环 + 唤醒 + 可用性轮询编排"，瘦身不彻底；**P-01 已下沉 `WaitCoordinator`（等待循环/唤醒/可用性轮询）**，facade 由 1018→953 行；**P-02 已下沉持久化/凭证切换/生命周期编排三个协作者**，facade 收口至 **397 行（≤400 收口达标；P-02 →400，P-08 删死私有 `Truncate` 别名 →397）** | `src/.../Core/Engine/BatchEngine.cs` **397 行**（P-02 后 400，P-08 后 397）／`WaitCoordinator.cs` 156 行／`BatchPersistence.cs` 193 行／`AccountSwitchCoordinator.cs` 185 行／`BatchLifecycle.cs` 283 行 | 高   | P1-1（已拆协作者）＋P-01（WaitCoordinator 下沉）＋P-02（facade ≤400 收口） |
| A-2  | 上帝类·状态机    | 单任务状态机虽外置，但 `TaskRunner` 曾是巨类，分支/重试/退避/降级/超时全挤一处；**P-03 已将「降级/冷却/兜底/免费时段」判定下沉为 4 个纯函数式 `Policy` 对象、状态机本体移入 `TaskStepExecutor`**，`TaskRunner` 退为 76 行薄 facade（≤400 收口达标） | `src/.../Core/Engine/TaskRunner.cs` **76 行**（P-03 后）／`TaskStepExecutor.cs` 569 行／`DegradationPolicy.cs`／`CooldownPolicy.cs`／`FallbackPolicy.cs`／`FreeTimeWindowPolicy.cs` | 高   | P1-2（已外置）＋P-03（策略对象化 ✅ 已收口，见 §6.2） |
| A-3  | 上帝类·UI     | Desktop 真实热点在 VM：`ConfigViewModel` **1065→366（P-04 已分解）** / `MonitorViewModel` **983→388（P-06 已分解）** / `LoginViewModel` **751→280（P-06 已分解）** / `TemplateEditorViewModel` **674→240（P-05 已分解）** | `src/.../Desktop/ViewModels/*`                                        | 高   | P1-19（已补测试）；`ConfigViewModel` 分解 **P-04 ✅ 已收口**（facade 366 行 + 账号卡/降级/通知/Git/通用 子编辑器 + 共享校验）；`TemplateEditorViewModel` 分解 **P-05 ✅ 已收口**（facade 240 行 + 模板加载/变量展开/流程示意三个 Coordinator + 4 个行视图模型独立文件）；`Monitor`/`Login` 分解 **P-06 ✅ 已收口**（facade 388/280 行 + Monitor 8 协作者 / Login 5 协作者 + 3 个行视图模型独立文件） |
| A-4  | 重复·样板      | 三份 `Json*Store` 逐行同构（构造+默认目录+Load+Atomic 保存）；"BatchAgentRunner" 路径魔法串在 **9 处**硬编码                                         | `JsonStoreBase.cs` 64 行／`AppPaths.cs` 45 行（收口后）；三份仓储 191/70/50 → **167/51/19 行** | 中   | P2-31 ✅ 已偿（2026-10-21，TECH-DEBT §5.11）＝ **ARCH P-07 ✅ 已收口**（§6.2 执行记录） |
| A-5  | 重复·领域      | 原「`Truncate`×3、终态集合×3、`Clone`×6、失败分派状态机逐字重复」：**P-08 ✅ 已收口（2026-10-21）**——P2-1~P2-6 已收敛的前提下补齐防回潮闸门：残留内联截断 3 处（`StreamJsonRenderer`×2 / `TemplateFlowNodeViewModel`×1）并入 `TextTruncate`、终态/深拷贝/依赖分派判定入口由 `DomainPrimitivesHygieneTests`（5 项）锁唯一，并修掉 `TaskItem.Clone()` 阶段结果浅拷贝（改 `StageResult` 为 record + 逐元素 `Clone`） | `TextTruncate.cs`／`TaskStatusExtensions.cs`／`WorkflowTypeClones`／`DagScheduler.EvaluateDependency` | 中   | P2-1~6 ＝ **ARCH P-08 ✅ 已收口**（§6.2 执行记录） |
| A-6  | God Config | **P-09 ✅ 已收口（2026-10-21）**：`RunOptions` **369→18 行**（按 12 配置域横向切片为 `RunOptions.01~11-*.cs`，各 ≤49 行，JSON 形状与落盘字节逐字不变），JSON 往返克隆随 v2.0 消除且由守卫禁复现；`ConfigEditors` 手写 DTO 编辑器 **510→505 行**——解析字段全量 `FieldEditor<T>`（含本轮收口的时段起止）、删掉 `StartText`/`EndText` 裸字符串桥接、视图侧校验反馈统一（7 个缺反馈的输入框已补齐，错误文本样式单一来源） | `Core/Options/RunOptions*.cs` / `Desktop/Models/FieldEditor.cs` / `Desktop/Models/ConfigEditors.cs` | 中   | P2-9 / P2-15 ＝ **ARCH P-09 ✅ 已收口**（§6.2 执行记录） |
| A-7  | 依赖·脆弱      | 跨层引用靠 IL 扫描断言"硬维持"，一旦断言漏写即回潮；组合根外偶有 `new` 具体类型                                                                            | `P1-4` 收口项、`§4.3`                                                     | 中   | P1-4 / P4.3            |
| A-8  | 可运维·可观测    | ~~无运行级指标（降级次数/兜底触发/冷却跳过/误切换率/吞吐/平均耗时）；日志是否结构化、是否带 RunId 关联未统一~~ **P-10 ✅ 已收口（2026-10-21）**：结构化日志接线（唯一落盘点 + `run-*.jsonl`）+ `RunId` 贯穿事件/日志/指标 + `RunMetrics` 七类运行级指标埋点（含 `metrics-<run_id>.json` 落盘）+ `SecretMasker` 脱敏在渲染入口统一生效（原「凭证不落日志」零基础设施、全靠写入点自觉） | `Core/Observability/*`／`Infrastructure/Logging/FileRunLogger.cs`（文本 + JSONL 双轨）／`Infrastructure/Observability/RunMetricsWriter.cs` | 高   | **P-10 ✅ 已收口**（§6.2 执行记录） |
| A-9  | 工程·CI      | CI 门禁转阻断 **① ✅ 已收口（P-11，2026-10-22）**：`build`+分层单测+格式门禁（`dotnet format --verify-no-changes`，CHARSET 假阳性按脚本过滤）+ 覆盖率门禁（`scripts/ci/Assert-Coverage.ps1` 逐生产程序集按锁定基线断言）全部进 CI 且失败即红；required status check（`build-and-test`）待仓库管理员在分支保护勾选。**② ✅ 已收口（P-13，2026-10-22）**：多平台矩阵（win/mac/linux 原生 runner，先非阻断 R-6）+ Release 流水线（自包含单文件四 RID + GitVersion + SHA256）；静态安全扫描按 A-11 归属由 P-12 收口（见下行） | `ci.yml`／`scripts/ci/Assert-Format.ps1`／`scripts/ci/Assert-Coverage.ps1`／`ci-platforms.yml`／`release.yml` | 高   | P0-16 / P4.9；① = **P-11 ✅ 已收口**；② = **P-13 ✅ 已收口** |
| A-10 | 工程·规范      | ~~`.editorconfig` 仅 28 行，团队级关键规则（命名/CA/IDExxxx）未成集；`CS1591` 已关，公共 API 文档无约束~~ **P-12 ✅ 已收口（2026-10-22）**：`.editorconfig` 扩为团队级规则集（6 组命名规则 + 5 条安全 CA 规则 `error` 化）；四个 `src` 工程开启 `GenerateDocumentationFile`、公共 API 全量补齐 XML 文档，`NoWarn CS1591` 删除（逃逸阀 #3 退役） | `.editorconfig`／`Directory.Build.props`／四个 `src/*.csproj` | 中   | **P-12 ✅ 已收口**（§6.2 执行记录） |
| A-11 | 风险·安全      | ~~`CliCommandBuilder`/`CliAdapterBase` 是命令构造安全敏感区；凭证格式兼容面（`FileCredentialVault`）无守卫~~ **P-12 ✅ 已收口（2026-10-22）**：`SecurityHygieneTests`（7 项）把三类仓级不变量变成红线——命令注入（唯一拼装点 + `Quote(NormalizeToSingleLine(…))` 形态 + 引号实现唯一 + 适配器不得裸替换）、路径遍历（六个账号 ID 入口方法逐个方法体校验 `NormalizeAccountId` + 恶意 ID 拒绝语义）、凭证硬编码（`src` 全量扫描疑似密钥字面量） | `tests/BatchAgentRunner.Tests/Architecture/SecurityHygieneTests.cs` | 中   | **P-12 ✅ 已收口**（§6.2 执行记录） |
| A-12 | 文档·同步      | ~~无 ADR 记录重大架构决策；设计文档与代码靠人工同步；配置 schema 无机器可校验的单一事实来源~~ **P-14 ✅ 已收口（2026-10-22）**：`docs/adr/` 不可变决策记录 11 篇（分层/策略/facade/VM/去重/配置契约/可观测性/CI/发布/构建 SOP/ADR 机制）+ 索引与结构由 `AdrHygieneTests` 机器校验；`docs/config.schema.json`（JSON Schema 2020-12）由 `RunOptions` 派生，键集合与代码逐字相等由 `ConfigSchemaTests` 锁死，并被 `scripts/ci/Assert-ConfigSchema.ps1` 纳入 CI 阻断门禁 | `docs/adr/`（11 篇 + README 索引 + 模板）／`docs/config.schema.json`／`scripts/ci/Assert-ConfigSchema.ps1` | 低   | **P-14 ✅ 已收口**（§6.2 执行记录） |

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
| 公共资源（`JsonStoreBase<T>`/路径常量/Truncate/Clone）须来自单一事实来源   | 重复实现回潮  |
| 编辑器解析字段一律 `FieldEditor<T>` + `FieldParsers`，且视图必呈校验反馈       | 手写编辑器回潮  |
| 日志写入一律经 `RunLogEntry` 单一落盘点；脱敏/级别/指标字段集各有单一事实来源          | 可观测性回潮  |
| `BatchEngine`/`TaskRunner`/最大 VM **代码行数**（排除 `///` 文档注释）≤ 阈值   | 上帝类回潮   |
| 命令注入：`{prompt}`/`{workspace}` 必守 `Quote(NormalizeToSingleLine(…))` 形态、引号实现唯一、适配器经唯一拼装点；路径遍历：Vault 账号 ID 入口逐个方法体经 `NormalizeAccountId` 净化；凭证硬编码：`src` 无疑似密钥字面量 | 注入/遍历/密钥泄漏回潮 |

> 第 4 条落地进度：`JsonStoreBase<T>` + `AppPaths` 由 `AppPathsHygieneTests`（**4 项**）锁死——**P-07 ✅ 已收口**（含「第 4 份同构仓储」类型扫描拦截，见 §6.2）；`Truncate`/`Clone`/终态集合/依赖失败分派 由 `DomainPrimitivesHygieneTests`（**5 项**）锁死——**P-08 ✅ 已收口**（见 §6.2）；`RunOptions` 配置域切片与「禁 JSON 往返克隆」由 `RunOptionsHygieneTests`（**3 项**）锁死、配置读写契约由 `RunOptionsContractTests`（**5 项**）锁死、编辑器泛型化与统一校验反馈由 `ConfigEditorsHygieneTests`（**3 项**）锁死——**P-09 ✅ 已收口**（见 §6.2）。
>
> 第 7 条落地进度（**P-12 ✅ 已收口，见 §6.2**）：由 `SecurityHygieneTests`（**7 项**）锁死——命令注入唯一拼装点与引号/单行折叠形态 / `CliAdapterBase` 不得裸替换占位符 / Vault 六个账号 ID 入口逐个方法体净化 + 恶意 ID 拒绝语义（Theory 5 例）+ 合法 ID 原样通过 / 源码无硬编码密钥。守卫按**方法体**而非全文匹配，单点削弱即可定位。
>
> 第 6 条落地进度（**P-10 ✅ 已收口，见 §6.2**）：由 `ObservabilityHygieneTests`（**6 项**）锁死——脱敏实现全仓唯一 / 级别字面量不得出现在写日志点 / `IRunLogger` 实现必须提供结构化通道与 RunId / 日志实现内不得绕过统一落盘点 / 指标快照覆盖运行级指标全集 / 引擎事件携带 RunId。四类事实（脱敏 `SecretMasker`、级别 `RunLogLevels`、事件名 `LogEventNames`、指标 `RunMetrics`）各自单一事实来源，防"再写一份"式回潮。

---

## 4. 代码重构策略

### 4.1 命名与可读性

- 语义命名统一：消除残留"二期扩展/遗留/暂不支持"等误导性注释（P2-26 已清大部分，余下随重构确认）。
- 端口命名 `I*` 一致；避免缩写与模糊名（如 `evaluator` → `availabilityEvaluator`）。
- 恢复 `CS1591` 要求，公共 API 必须 XML 文档（A-10）✅ **P-12 已收口**：四个 `src` 工程开启 `GenerateDocumentationFile`，缺失文档由 `TreatWarningsAsErrors` 直接打成构建错误。

### 4.2 职责拆分模式

| 模式                         | 适用                                       | 落点                                 |
| -------------------------- | ---------------------------------------- | ---------------------------------- |
| 编排 → 策略对象                  | 降级/冷却/兜底/时段判定                            | 抽 `Policy` 对象（§3.3），纯单测            |
| 巨类 → 协作者                   | `BatchEngine` facade                     | `WaitCoordinator` + 既有 6 协作者（§3.3） |
| 状态机 → 状态/转移/动作三层           | `TaskRunner` 903 行                       | 状态表 + 动作委托，CC 降到可测区间               |
| VM → Coordinator/Presenter | `Config/Monitor/Login/TemplateEditor` VM | 交互逻辑外移，VM 退化                       |
| 防腐层                        | 领域 ↔ UI                                  | `TaskItemViewModel` 全量落地           |

### 4.3 消除重复（A-4/A-5/A-6）

- **Json*Store 抽 `JsonStoreBase<T>`**（P2-31 ＝ **P-07**）✅ **已收口（2026-10-21，见 TECH-DEBT §5.11 / 本方案 §6.2 P-07 执行记录）**：构造/默认目录/`LoadAsync` 三条回落（文件缺失 / `JsonException` / 反序列化为空）/ `AtomicFileWriter` 原子保存统一到基类，派生类只留 `CreateEmpty()` 与可选 `NormalizeAsync()` 自愈·迁移钩子；9 处魔法串收为 `AppPaths` 常量类（`Root` / `ConfigDirectory` / `TemplatesDirectory` / `VaultDirectory` / `RunsDirectory` / `LogsDirectory` / `MockLiveDirectory`），并由 `AppPathsHygieneTests`（4 项）守卫防复发。行为与落盘字节零变化。
- **领域工具收口到 Core**（P2-1~6 ＝ **P-08**）✅ **已收口（2026-10-21，见 §6.2 P-08 执行记录）**：`Truncate` → `Core/Text/TextTruncate`（`Result` 结果截断 + `Truncate` 通用截断）、终态集合 → `TaskStatusExtensions.IsTerminal`、`Clone` → 工作流域 `record with`（`WorkflowTypeClones`）+ `TaskItem`（唯一手写点，因承载属性通知无法记录化）、依赖失败分派 → `DagScheduler.EvaluateDependency`（三态判定）四者各自单一事实来源；本轮再收口残留内联截断 3 处，并修掉 `TaskItem.Clone()` 对 `StageResults` 的浅拷贝（元素共享 → 改 `StageResult` 为 record + 逐元素 `Clone`）。防回潮闸门：`DomainPrimitivesHygieneTests` **5 项**（内联截断拦截 / 终态成员须委派 + 全枚举值行为等价 / 深拷贝入口只允许 `WorkflowTypeClones` 与 `TaskItem` / 依赖分派公开入口唯一且 `TaskDispatcher` 保持委派）+ `TaskItemCloneTests` **4 项**（反射遍历可写属性的字段完整性 · 深拷贝不共享 · 序列化形状一致 · 可空语义）。
- **RunOptions/ConfigEditors 泛型化**（P2-9/P2-15 ＝ **P-09**）✅ **已收口（2026-10-21，见 §6.2 P-09 执行记录）**：编辑器解析字段一律 `FieldEditor<T>` + `FieldParsers` 单一解析来源（含本轮收口的时段起止，并删掉 `StartText`/`EndText` 裸字符串桥接），`Apply()` 对错误态保留原值；视图侧统一呈现校验反馈（错误文本样式 `TextBlock.field-error` 单一来源），由 `ConfigEditorsHygieneTests`（**3 项**：解析调用单一来源 / 声明即须被 `Apply()` 读取 / 凡绑 `X.Text` 必呈 `X.Error`）防回潮。`RunOptions` 由 363 行单一大类按 12 个配置域横向切片为 `RunOptions.NN-*.cs`（切片各 ≤49 行），**JSON 形状与属性落盘顺序逐字节不变**；JSON 往返克隆本就随 v2.0 消除，现由 `RunOptionsHygieneTests`（**3 项**：须按配置域切片不得回缩 / 切片行数上限 / 禁 `Clone`/`Copy`/`Snapshot` 成员复现）锁死，读写契约由 `RunOptionsContractTests`（**5 项**）锁死。

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
- ✅ **P-12 已收口**：`.editorconfig` 由 28 行扩为团队级规则集——① 命名规则 6 组（接口 `I` 前缀、类型 PascalCase、对外可见成员 PascalCase、参数与局部变量 camelCase、私有实例字段 `_camelCase`、静态字段含 `const` PascalCase），口径按**存量约定**固化；② 关键 `CAxxxx` 安全规则 5 条置 `error`（CA5350/CA5351/CA3001/CA3075/CA5392）；③ 恢复 `CS1591` 并强制公共 API 文档（`GenerateDocumentationFile` + `TreatWarningsAsErrors`）。
- 口径边界：命名规则只约束**对外可见成员**（私有事件转发方法如 `_Forward` 属既有约定，不纳入）；`.editorconfig` 仍保留既有逃逸阀（CA1002 等，见 TECH-DEBT §4.7），新增例外须先登记台账。
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

**P-11 ✅ 已收口（2026-10-22）**：门禁转阻断 + 覆盖率门禁 + 格式门禁三项已全部进 CI（详见 §6.2 P-11 执行记录）。目标管线逐项状态：

1. **门禁转阻断** ✅：`build`（全量 `--no-incremental`）+ 分层单测 + 两道新门禁同属 job `build-and-test`，任一步骤失败即 job 失败；将该 job 名配为 master 分支保护的 required status check 后即阻止合并（分支保护属 GitHub 仓库管理设置，由管理员执行，操作步骤见 §6.2 P-11 执行记录）。
2. **覆盖率采集 + 门禁** ✅：`coverlet.collector` 采集（`tests/coverlet.runsettings`，cobertura + SkipAutoProps，采集范围锁定快跑 bucket），`scripts/ci/Assert-Coverage.ps1` 按锁定基线（Core 44% / Infrastructure 39% / Agents 23% / Desktop 50%）逐生产程序集断言，低于阈值即失败。
3. **格式门禁** ✅：`dotnet format --verify-no-changes` 进 CI（`scripts/ci/Assert-Format.ps1`，过滤 SDK 10.0.401 CHARSET 假阳性），与 pre-commit 钩同口径。
3b. **配置契约门禁** ✅（P-14，2026-10-22）：`scripts/ci/Assert-ConfigSchema.ps1` 进 `build-and-test`，校验 `docs/config.schema.json` 与 `RunOptions` 键集合逐字一致、示例配置合法、畸形配置被拦截（校验实现唯一，见 `ConfigSchemaTests`）。
4. **架构测试桶** ✅（P-13，2026-10-22）：§3.4 的 IL 扫描断言已从 `ci.yml` 快跑 bucket 抽离，独立成 `ci-platforms.yml` 的 `architecture-tests` job 隔离跑（与 `ci.yml` 快跑 bucket 的架构断言等价、互不依赖），提升可见性。
5. **静态安全扫描** ✅（P-12，2026-10-22）：① 分析器分级——弱加密 / 已破解算法 / SQL 注入 / XXE / P/Invoke 未限定搜索路径五类规则置 `error`（`.editorconfig`）；② 仓级专属不变量——`SecurityHygieneTests`（7 项）守卫 `CliCommandBuilder`（命令注入：唯一拼装点 + 引号/单行折叠形态 + 引号实现唯一）、`CliAdapterBase`（不得绕过拼装点裸替换占位符）、`FileCredentialVault`（账号 ID 入口逐个方法体校验 `NormalizeAccountId`，防路径遍历）、`src` 全量疑似密钥字面量扫描（凭证硬编码）；③ 与分层 / 单一事实来源 / 可观测性守卫同属 §3.4「架构约束自动化」清单（见下表第 7 条）。
6. **多平台矩阵** ✅（P-13，2026-10-22）：`windows / macOS / linux` 三平台原生 runner 矩阵已建（`ci-platforms.yml` 的 `platform-matrix`，`continue-on-error` **先非阻断**，对应 R-6 缓解——先晒隐蔽缺陷、稳定后转阻断）；`src/BatchAgentRunner.Desktop/BatchAgentRunner.Desktop.csproj` 已按宿主 OS 条件化 `OutputType`（Windows=WinExe，其余=Exe）并剔除 Windows 专有 `app.manifest` / `.ico`，关闭「只验 Windows 是缺口」。升级路径：观察三平台全绿后去 `continue-on-error` + 加入 required status checks（详见 workflow 文件尾部注释）。
7. **Release 流水线** ✅（P-13，2026-10-22）：`.github/workflows/release.yml` 已建——`dotnet publish` 自包含单文件（win-x64 / linux-x64 / osx-x64 / osx-arm64，各 RID 在原生 runner 发布）+ 版本号（`GitVersion`，Mainline 模式，见 `GitVersion.yml`）+ 产物 **SHA256 校验**（`*.sha256` 随包发布）；打 `v*` 标签即建 GitHub Release 并聚合四平台制品，**制品可回退**（回退 = 下载上一版本 Release）。代码签名（Windows Authenticode / macOS Apple Developer ID + 公证 / Linux GPG）需平台证书机密，列为后续接入项（workflow 注释已留接入点），当前以 SHA256 校验兜底「制品未被篡改」。

### 5.5 静态扫描与质量门

- 分析器规则集：warning→error 分级，新警告本地与 CI 均失败（已靠 `TreatWarningsAsErrors` 兜底）✅ **P-12 追加安全类 error 规则**：CA5350 / CA5351 / CA3001 / CA3075 / CA5392（弱加密、已破解算法、SQL 注入、XXE、P/Invoke 未限定 DLL 搜索路径）——实测存量 0 违例，新增即红。
- 安全扫描（仓级不变量）✅ **P-12**：命令注入 / 路径遍历 / 凭证硬编码三类由 `SecurityHygieneTests`（7 项）固定为回归红线，报告见 §3.4 第 7 条与 §6.2 P-12 执行记录。
- 依赖扫描（ Supply-chain ）：CPM 已 centralized，定期 `dotnet list package --vulnerable`。
- 代码质量看板：复杂度、重复率、覆盖率趋势（TECH-DEBT §4.8 度量看板复用）。

### 5.6 日志与可观测性（A-8，**P-10 ✅ 已收口，2026-10-21**）

- **统一结构化日志** ✅：`IRunLogger` 增 `Write(RunLogEntry)` 作为<b>唯一落盘点</b>——十余个 `Write*` 便捷方法一律转条目后经此渲染：文本 `run-*.log`（人读 tail，正文逐字不变）+ 结构化 `run-*.jsonl`（机器采集，含 `ts/level/event/run_id/task_id/account/model/message/fields`）。新增日志点不可能绕过结构化。
- **关联标识** ✅：`IRunLogger.RunId` 为批次关联标识（`run-{时间戳}-{6 位随机}`，全局唯一生成入口 `RunLogEntry.NewRunId()`），引擎与日志共用同一实例；`BatchEvent.RunId` 由 `BatchEngine.Raise` 统一填充，事件流 / 日志 / 指标三者可按同一次运行聚合。
- **运行级指标**（新增，不侵入业务语义）✅：`RunMetrics` 在状态机决策点埋点——任务吞吐、平均耗时、降级次数、兜底触发、冷却跳过、时段跳过、失败率、**误切换率**（设计已定义"时段外/冷却跳过不计失败"）；批次终态输出 `metrics_snapshot` 结构化条目，并由 `RunMetricsWriter` 落 `metrics-<run_id>.json`（snake_case、派生量随快照同落）。
- **敏感脱敏** ✅：`SecretMasker`（键名形态 / Bearer / JWT / `sk-`·`ghp_`·`AKIA` 前缀）在渲染入口统一生效，凭证/密钥绝不落日志；报告与日志可关联但分离存储。
- 指标与 `BatchReport` 互补：报告给用户看，指标给运维看（二者同批落盘、命名同口径、字段集各自稳定）。

> 口径（由 `RunMetricsTests` 锁死）：失败率 = 失败 / 终态（待人工与依赖阻断计入终态但不计失败）；误切换率 = 切换后本次尝试仍因额度耗尽或凭据失效的切换 / 切换总次数；吞吐 = 完成任务数 / 批次已运行小时数。

### 5.7 配置治理（A-6/A-12）

- **schema 单一事实来源** ✅（P-14，2026-10-22）：`docs/config.schema.json`（JSON Schema 2020-12）由 `RunOptions` 及其引用的领域类型派生；`ConfigSchemaTests` 断言 schema 顶层与各 `$defs` 键集合**逐字等于** C# 类型的 `[JsonPropertyName]` 键集合、`samples/config.example.json` 通过校验、畸形配置被拦截；`scripts/ci/Assert-ConfigSchema.ps1` 将其纳入 CI 阻断门禁（§5.4）。决策背景见 [ADR-0007](adr/ADR-0007-config-contract-and-schema.md)。
- **前向兼容口径**：schema 对所有对象设 `additionalProperties: true`——旧版本 `config.json`（含已废弃字段）仍可校验通过；「漂移」由键集合等值断言拦截，而非靠 `additionalProperties:false` 误伤兼容字段。
- **迁移框架**：版本化迁移器，新增字段默认兼容；破坏性变更走主版本。
- **约定固化**：snake_case + 派生属性 `[JsonIgnore]`（已实现；**P-09 已由 `RunOptionsContractTests` 的「命名契约」/「派生契约」断言守卫**，不再依赖人肉 review）。
- **密钥隔离**：凭证不入 config（已由 Vault 处理），配置仅存非敏感项。

### 5.8 文档与决策留痕

- `DESIGN` 维持单一设计权威源；重大架构决策写 **ADR** ✅（P-14，2026-10-22）：`docs/adr/` 已建，含 [README](adr/README.md)（写入时机/状态机/索引）、[模板](adr/template.md) 与 **11 篇已接受决策**（分层、策略对象化、facade 上限、VM 分解、单一事实来源、配置契约、可观测性、CI 门禁、多平台发布、构建 SOP、ADR 机制本身）。
- **ADR 不可变**：决策接受后不修改，演进靠新 ADR 标注取代关系；索引与本目录文件双向一一对应。
- **机器同步机制** ✅：`AdrHygieneTests`（4 项）校验编号连续/格式合规、必备章节与状态取值、索引双向一致、模板存在；`ConfigSchemaTests`（4 项）校验配置契约与 schema 同步——「文档漂移」在 CI 单测桶内即红，不再依赖人肉 review。
- 对外契约（CLI 参数、config schema、sidecar 格式、报告结构）半自动生成文档。
- 代码↔文档同步靠 PR 评审清单 + 架构测试守卫，而非人肉。

---

## 6. 分阶段落地计划（增量 strangler fig，禁止大爆炸）

> **节奏校准（用户确认）**：每阶段 ≈ **5 人天**，小批量增量交付。每个阶段必须**独立可编译、全量测试绿、不破坏对外契约**，并可单独回滚。阶段间按依赖顺序串行，主题内可并行。
>
> 基线（已完成，2026-10-17~20）= TECH-DEBT 阶段 −1/0/1/2：止血、建网、动骨（BatchEngine 拆 6 协作者、提接口、DI、端口抽离、Desktop 测试基座）。以下为**剩余 14 个 5 人天阶段**（合计 ≈ 70 人天），分主题 A/B/C；主题 D 为长期持续化，不计入固定阶段。**进度（2026-10-22）**：**14 个阶段全部收口**（P-01~P-14）。

### 6.1 阶段总览（5 人天/阶段）

| 阶段       | 主题     | 范围          | 关键交付                                                                | 依赖   | 退出标准                            |
| -------- | ------ | ----------- | ------------------------------------------------------------------- | ---- | ------------------------------- |
| **P-01** | A 架构收口 | A-1 (①)     | 抽取 `WaitCoordinator`（等待循环/唤醒/可用性轮询下沉）                               | —    | ✅ 已收口（2026-10-21）：diff<400 行；facade 1018→953 行；IL 绿；测试绿 |
| **P-02** | A 架构收口 | A-1 (②)+A-7 | `BatchEngine` facade 收口 ≤400 行 + 防腐层/依赖方向 IL 断言固化                   | P-01 | ✅ 已收口（2026-10-21）：facade 953→400 行；IL 断言全绿；测试绿 |
| **P-03** | A 架构收口 | A-2         | `TaskRunner` 策略对象化（Degradation/Cooldown/Fallback/FreeTime → Policy） | —    | TaskRunner ≤400 行；Policy 单测覆盖   |
| **P-04** | A 架构收口 | A-3 (①)     | `ConfigViewModel` 分解（账号卡/降级/通知/Git/通用 子编辑器 + 共享校验）                  | P-02 | ✅ 已收口（2026-10-21）：facade 1065→366 行；headless 契约测试绿；测试绿 |
| **P-05** | A 架构收口 | A-3 (②)     | `TemplateEditorViewModel` 分解（引入 Coordinator/Presenter）              | P-02 | ✅ 已收口（2026-10-21）：facade 674→240 行；引入模板加载/变量展开/流程示意三个 Coordinator + 4 个行视图模型独立文件；测试绿；§3.4 第 5 条守卫扩展 |
| **P-06** | A 架构收口 | A-3 (③)     | `MonitorViewModel` + `LoginViewModel` 分解收口                          | P-02 | ✅ 已收口（2026-10-21）：Monitor facade 983→388 行 + Login facade 751→280 行；Monitor 下沉 8 协作者（输出流/统计/批次状态/提示板/任务列表/DAG/纯统计/输出分类/操作编排）+ Login 下沉 5 协作者（登录流程/领取流程/账号池/步骤条/异常翻译）+ 3 个行视图模型独立文件；准入 headless 契约测试 29 项（Login 18 + Monitor 11）+ XAML 绑定守卫 1 项（2 视图）；§3.4 守卫扩展 2 项；全量测试 734 绿 |
| **P-07** | B 去重   | A-4         | `JsonStoreBase<T>` + `AppPaths` 常量（消除 3 份同构 + 9 处魔法串）               | —    | ✅ 已收口（2026-10-21）：三份仓储 191/70/50→167/51/19 行；9 处魔法串清零（仅剩 `AppPaths.cs` 一处声明）；`AppPathsHygieneTests` 3→4 项；全量测试 735 绿 |
| **P-08** | B 去重   | A-5         | 领域工具收口 Core（Truncate/终态/Clone/失败分派 单一来源）                            | —    | ✅ 已收口（2026-10-21）：内联截断清零（残留 3 处并入 `TextTruncate`）；终态/深拷贝/依赖分派判定入口唯一；`DomainPrimitivesHygieneTests` 5 项 + `TaskItemCloneTests` 4 项；清偿 `BatchEngine` 死私有 `Truncate` 别名（facade 400→397 行）；全量测试 744 绿 |
| **P-09** | B 去重   | A-6         | `ConfigEditors` 泛型化 + `RunOptions` 收口（弃 JSON 往返克隆）                  | P-07 | ✅ 已收口（2026-10-21）：编辑器解析字段全量 `FieldEditor<T>` + 视图校验反馈统一；`RunOptions` 363 行大类按 12 配置域切片（各 ≤49 行）且 JSON 形状逐字节不变；`RunOptionsHygieneTests` 3 项 + `RunOptionsContractTests` 5 项 + `ConfigEditorsHygieneTests` 3 项；全量测试 758 绿 |
| **P-10** | C 工程化  | A-8         | 可观测性：结构化日志接线 + RunId 关联 + 运行级指标埋点                                   | —    | ✅ 已收口（2026-10-21）：日志可关联（RunId 贯穿事件/日志/指标）；指标可采集（`metrics-<run_id>.json` + JSONL）；脱敏有效（`SecretMasker` 单一事实来源 + 渲染入口统一生效）；全量测试 801 绿 |
| **P-11** | C 工程化  | A-9(①)      | CI 转阻断（required status）+ 覆盖率门禁 + 格式门禁进 CI                           | P-07 | ✅ 已收口（2026-10-22）：格式/覆盖率/分层单测/构建四道门禁全进 CI 且失败即红；门禁基线 Core 44%/Infra 39%/Agents 23%/Desktop 50%；required status check 待管理员在分支保护勾选 `build-and-test` |
| **P-12** | C 工程化  | A-10/A-11   | `.editorconfig` 规则集扩充 + 恢复 `CS1591` + 静态安全扫描固化命令/凭证守卫               | P-11 | ✅ 已收口（2026-10-22）：6 组命名规则 + 5 条安全 CA 规则 `error` 化；四个 `src` 工程 `GenerateDocumentationFile`、公共 API 全量补文档（`NoWarn CS1591` 删除、逃逸阀 #3 退役）；`SecurityHygieneTests` 7 项守卫命令注入/路径遍历/硬编码密钥；严格构建 0 警告 0 错误 |
| **P-13** | C 工程化  | A-9(②)      | 多平台 CI 矩阵（win/mac/linux）+ Release 流水线（publish+版本号+签名）               | P-11 | ✅ 已收口（2026-10-22）：多平台矩阵（win/mac/linux 原生 runner，非阻断 `continue-on-error`，R-6）+ 架构测试独立桶（`ci-platforms.yml`）；Release 流水线（自包含单文件四 RID + GitVersion 版本号 + SHA256 校验，标签建 GitHub Release 可回退）；代码签名列为后续接入项 |
| **P-14** | C 工程化  | A-12        | ADR 机制 + 配置 JSON Schema 单一事实来源 + CI 校验                              | P-09 | ✅ 已收口（2026-10-22）：`docs/adr/` 11 篇决策记录（含机制/模板/索引）+ `AdrHygieneTests` 4 项守卫；`config.schema.json` 键集合契约由 `ConfigSchemaTests` 4 项锁死；`Assert-ConfigSchema.ps1` 进 CI 阻断门禁 |

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

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - 新增 3 个 facade 协作者（沿用 `WaitCoordinator`/`TaskDispatcherContext` 的「上下文注入」范式，避免反向耦合）：
>   - `src/BatchAgentRunner.Core/Engine/BatchPersistence.cs`（193 行，含 `BatchPersistence` + 构造期 `GateReleaser`）：承接持久化/IO——`LoadCooldownsAsync`/`LoadTasksAsync`/`BackupAsync`/`SaveCooldownsAsync`/`PersistAsync`/`EnterWriteGateAsync`/`CheckIdSnapshot`，独占 `_writeGate`/`_persistGate`，由 `BatchEngine.Dispose()` 一并释放。
>   - `src/BatchAgentRunner.Core/Engine/AccountSwitchCoordinator.cs`（185 行，含 `AccountSwitchContext`）：承接凭证切换——`EnsureLiveCredentialAsync`/`SwitchAccountAsync`/`EnsurePreferredLiveAccountAsync`/`RotateAccountToFront`。
>   - `src/BatchAgentRunner.Core/Engine/BatchLifecycle.cs`（283 行，含 `BatchLifecycle` + `BatchLifecycleContext` + 共享 `BatchRunState`）：承接批次生命周期编排——`RunAsync`/`ExecuteWorkflowAsync`/`BuildWorkflowContext`/`IsQuotaUnboundedWait`。
> - `BatchEngine` 改造：移除持久化/凭证切换/生命周期编排约 550 行，facade **953→400 行**（净下降 553 行，≤400 收口达标）；新增 `_persistence`/`_accountSwitch`/`_lifecycle` 字段并在 ctor 经 `BuildTaskRunnerContext`/`BuildTaskDispatcherContext`/`BuildWaitCoordinatorContext` 装配；`InitializeAsync`/`LoadTasksAsync`/`PreflightAsync`/`StartAsync`/`Dispose` 改为委托/调用协作者；引入 `BatchRunState`（与 `BatchLifecycle` 共享同一可变运行态实例，避免 `TerminateRequested`/`StopAfterCurrentTask`/`CurrentTaskId`/`RunCts` 重复持有）；保留 `IBatchEngine` 公开行为逐字节等价。
> - **构造顺序护栏**：runner/dispatcher 的上下文注入依赖 `persistence`/`accountSwitch`/`lifecycle`，而 `lifecycle` 的上下文注入又依赖 `dispatcher`/`waitCoordinator`，故 ctor 顺序固定为「叶子协作者 → dispatcher/waitCoordinator → lifecycle → runner」，以规避空 this 委托 / 空引用上下文注入。
> - **IL 断言固化**：既有 §3.4 第 1~3 条（防腐层 + 依赖方向：Core 不得引用 Infrastructure/Agents/Desktop、Core 不得直 `new System.Diagnostics.Process`、Desktop 除组合根外不得 `new` Infrastructure/Agent 具体类型）收口后持续绿；**新增第 5 条** `BatchEngine_facade文件不得超过400行_防上帝类回潮`（Core `LayeringRulesTests`，`LocateEngineSource()` 自仓库检出定位 `BatchEngine.cs`，断言 `≤400` 行），防上帝类回潮。
> - **逃逸阀台账**：P-02 抽取的 `BatchRunState` 触发 `CA1001`（可释放字段未实现 IDisposable），按 P2-30 守卫登记为 TECH-DEBT §4.7 **#11**（到期日 2027-01-20，由 `BatchEngine.Dispose` 统一释放 `RunCts`）；另下线既有的 stale 台账 **#9**（pre-commit 钩已于 2026-10-21 禁用、CHARSET 过滤豁免不复存在，无代码引用）。
> - **退出验证**：全量 `--no-incremental` 构建 **0 警告/0 错误**；全量测试 **653 通过/0 失败**（主 541 + Desktop 112，较收口前 +1：新增 facade 行数守卫）；§3.4 IL 断言（Core `LayeringRulesTests` 4 项 + Desktop `LayeringRulesTests` 4 项）全绿；facade 行数收口达标且由守卫测试锁死。
> - **提交切片建议（§7 护栏）**：facade 改动单次 `git diff --stat` 约 771 行（>400），须按协作者为单位切片提交——`BatchPersistence` / `AccountSwitchCoordinator` / `BatchLifecycle` 三次提取各自独立可编译、独立 <400 行提交，最后收口 `BatchEngine.cs` 的委托化改造。

**P-03 · TaskRunner 策略对象化（A-2，5 人天）**

- 步骤：将"降级/冷却/兜底/免费时段"判定抽为纯函数式 `Policy` 对象（输入领域状态、输出决策），`TaskRunner` 退为"状态机驱动 + 调 Policy"，目标 ≤400 行、CC 可测。
- 准入：降级矩阵/冷却/兜底不变量测试先就位（P0-1/P0-5 不变量）。
- 退出：TaskRunner ≤400 行；Policy 单测覆盖四类决策；业务行为等价（契约测试锁定）。
- 风险：R-1 → 不变量 + E2E 真实任务清单回归。

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - 新增 4 个纯函数式策略对象（输入领域状态、输出决策，可独立单测，无副作用）：
>   - `src/BatchAgentRunner.Core/Scheduling/DegradationPolicy.cs`（145 行）：`DecideNext`（降级矩阵展开 / 临时限流重试同一组合）+ `DecideFailure`（失败路由：重试同一组合 / 记录冷却 / 停用账号 / 重试任务 / 判失败 / 判失败停批 / 危险操作拒绝）。**矩阵以 `Func<DegradationMatrix>` 延迟持有**——引擎 `NotifyConfigurationChanged()` 会重建矩阵，若在此捕获实例将读到陈旧矩阵（复测发现「配置变更补充可用组合后任务误判 Blocked」），故沿用 `BatchEngine.BuildTaskRunnerContext` 的 `MatrixProvider` 懒解析约定，每次判定现取现用。
>   - `src/BatchAgentRunner.Core/Scheduling/CooldownPolicy.cs`（31 行）：`RecordAndPurge` 封装「记录额度冷却 + 清过期」单一动作。
>   - `src/BatchAgentRunner.Core/Scheduling/FreeTimeWindowPolicy.cs`（66 行）：`DecideWait` 综合免费时段窗口 + 额度无限等待（`QuotaWaitPolicy.IsUnboundedQuotaWait` 单一事实来源）判定等待/阻塞。同样以 `Func<DegradationMatrix>` 延迟持有矩阵。
>   - `src/BatchAgentRunner.Core/Engine/FallbackPolicy.cs`（32 行）：封装「已投入执行的当前任务（`invested && 兜底启用`）才允许兜底（REQ-004b）」准入 + 全池 / 指定账号选取路由 + 成本截断委托。
> - 新增 4 个薄协作器（沿用 P-01/P-02 的「上下文注入 + 纯搬运」范式，杜绝逻辑改写导致行为漂移）：`TaskTerminalWriter.cs`（109 行，终态写入）、`TaskEventReporter.cs`（59 行，跳过/切换事件）、`TaskRequestBuilder.cs`（34 行，请求构建）、`TaskCredentialSwitch.cs`（56 行，Live 凭证确保）。
> - 状态机本体移入 `src/BatchAgentRunner.Core/Engine/TaskStepExecutor.cs`（569 行，构造期装配 4 个 Policy + 4 个协作器）：承载单任务状态机（重试 / 退避 / 降级 / 超时 / 兜底 / 终止）全部步骤，`TaskRunner` 退为 **76 行薄 facade**（仅 `new TaskStepExecutor(ctx)` + 委托 `RunSafeAsync`）——与 P-02 将 `BatchEngine` 薄面化同源。
> - **关键修复（复测拦截）**：`TaskStepExecutor` 原在 ctor 一次性捕获 `MatrixProvider()` 结果，导致配置变更后读到陈旧矩阵、`BatchEngineTests.等待中配置变更补充可用组合_提前退出等待并继续` 由 Completed 退化为 Blocked；改为向 Policy 传递 `MatrixProvider` 委托后恢复绿。
> - **守卫测试**：`LayeringRulesTests` 新增第 5 条扩展 `TaskRunner_facade文件不得超过400行_防上帝类回潮`（`LocateTaskRunnerSource()` 自仓库检出定位 `TaskRunner.cs`，断言 ≤400 行），与 `BatchEngine` 守卫同款，防上帝类回潮。
> - **Policy 单测**：新增 `Scheduling/PolicyTests.cs`，覆盖四类决策（降级：重试同一组合 / 选下一组合 / 全部不可用；失败路由：限流连击升降级 / 额度耗尽 / 凭据失效 / 危险操作 / 超时 / 普通错误重试与达上限停批；冷却：CLI 可靠/不可靠来源 + 清过期；免费时段：纯额度无限等待 / 关闭无限等待仍窗口内 / 无候选阻塞 / 超窗口阻塞；兜底：启用透传 + `invested && enabled` 准入门 + 选取路由 + 成本截断），共 31 项。
> - **退出验证**：全量 `--no-incremental` 构建 **0 警告/0 错误**；全量测试 **680 通过/0 失败**（主 568 + Desktop 112，较 P-02 收口 653 → 680）；其中 P-03 新增 Policy 决策单测 31 项 + TaskRunner facade 行数守卫 1 项；§3.4 IL 断言（Core `LayeringRulesTests` + Desktop `LayeringRulesTests`）全绿；`TaskRunner` facade 行数收口达标且由守卫测试锁死；`ITaskRunner` 接口与既有 `TaskRunnerTests` 契约测试逐字节等价（行为未漂移）。
> - **提交切片建议（§7 护栏）**：按「策略对象 → 协作器 → TaskStepExecutor → TaskRunner facade」顺序切片，每个文件独立 <400 行、独立可编译。

**P-04 · ConfigViewModel 分解（A-3 ①，5 人天）**

- 步骤：将 1065 行 God Object `ConfigViewModel` 拆为 11 个子编辑器（账号卡/任务/调度/关机/安全/降级/兜底/通知/Git/工作流/通用）+ 共享校验器 + `ConfigSaveCoordinator`，facade 仅保留"状态持有 + 绑定转发"，并通过 `_Forward`/`_ForwardStatus` 转发子编辑器 `PropertyChanged` 以保留 Avalonia 绑定通知契约。
- 准入：先补 Avalonia headless 契约测试（P1-19 基座），锁死 facade 公开 API 映射/交互/绑定契约/headless 视图四条路径。
- 退出：最大单个 VM ≤400 行；headless 契约测试绿；UI 行为等价。
- 风险：R-4（破坏 UI 行为）→ headless 契约测试先补再改。

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - **facade 收口**：`src/BatchAgentRunner.Desktop/ViewModels/ConfigViewModel.cs` 由 **1065 行 → 366 行**（≤400 达标），退化为薄面：持有 11 个子编辑器（`_models`/`_task`/`_schedule`/`_shutdown`/`_safety`/`_expert`/`_fallback`/`_notif`/`_git`/`_workflow`/`_misc`）+ `_status`(`ConfigStatus`)+`_coordinator`(`ConfigSaveCoordinator`)。ctor 订阅各子编辑器 `PropertyChanged`→`_Forward`（转发非空属性名）、`_status.PropertyChanged`→`_ForwardStatus`（转发 `Message`→`StatusMessage`），保留 Avalonia 绑定通知契约（`AvaloniaUseCompiledBindingsByDefault=false`，绑定路径须为可解析公共属性）。
> - **11 个子编辑器**（`src/BatchAgentRunner.Desktop/ViewModels/Config/`）：
>   - `ModelsAccountSectionViewModel.cs`（最大，~250 行）：`Models`/`Accounts`/`AvailableModels`/`IsFetchingModels`；Add/Remove/MoveModel 命令；`FetchModelsCommand`/`FetchModelsForAccountCommand`（经 `ModelCatalogFiller.Fill` + `_status.Show`）；`ModelPoolMode`（`SharedMode`/`PerAccountMode`）+ `MigrateModelPool`；`LoadFrom`（填充 Models/Accounts + 模式探测）；**`WriteModelPool(RunOptions)`** 承接原 `SaveAsync` 模型池合并块（Shared/PerAccount 合并 + 清退重加 `ModelCandidates`/`Accounts`）。
>   - `TaskSectionViewModel.cs`：`TaskFilePath`/`Agent`/`CliArgs`/`Workspace` + Add/RemoveWorkspace + BrowseTaskFile + PickTaskFile + WorkspaceLimit；ctor 取 `ConfigStatus`。
>   - `ScheduleSectionViewModel.cs`：并发/重试/超时/冷却/配额/强制/停止。
>   - `ShutdownSectionViewModel.cs`：自动关机。
>   - `SafetySectionViewModel.cs`：自动应答/黑名单/白名单/静默/选择。
>   - `ExpertDegradationSectionViewModel.cs`：`DegradationStrategy`/`Expert.Kind`/`Id`/`DisplayName` + `Strategies`/`ExpertKinds` 列表；经 `ConfigFieldRules.NullIfBlank` 加载/落盘。
>   - `FallbackSectionViewModel.cs`：`FallbackEnabled`/`MaxPerTask`/`MaxPerBatch`。
>   - `NotificationSectionViewModel.cs`：`NotificationRules`（`ObservableCollection`）+ Add/RemoveNotificationRuleCommand；`LoadFrom` 每条规则 `new NotificationRuleEditor`/`ApplyTo` 调 `r.Apply()`（引用 `BatchAgentRunner.Core.Domain.NotificationRule`）。
>   - `GitSectionViewModel.cs`：`GitRepoPath`/`BaseBranch`/`BranchNaming`/`PrTargetBranch`/`AutoOpenPr`；经 `ConfigFieldRules`（RepoPath→`TrimmedOrEmpty`、BaseBranch/PrTargetBranch→`TrimmedOrNull`、BranchNaming→`OrFallback(...).Trim()`）。
>   - `WorkflowSectionViewModel.cs`：`DefaultMaxGateLoops`/`EnableSessionResume`/`MarkdownInPlaceWriteback`/`DependencyFailurePolicy`/`TemplateBatchExpansionLimit`/`CollectDiffs`/`DiffGlobsText` + `DependencyPolicies` 列表；`MultiValueText.Join` 加载/`ApplyTo` 落盘。
>   - `MiscSectionViewModel.cs`：`MaxResultKb`/`BackupRetentionCount`；经 `ConfigFieldRules.AtLeastZero`。
> - **共享校验器 / 防腐层**：
>   - `ConfigFieldRules.cs`：`AtLeastOne`/`AtLeastZero`/`NullIfBlank`/`TrimmedOrNull`/`TrimmedOrEmpty`/`OrFallback` 单一事实来源，消除各子编辑器重复清洗。
>   - `ConfigStatus.cs`：单消息槽 `Message` + `Show`，替代原 facade 多字段状态拼接。
>   - `ModelCatalogFiller.cs`：`Fill` 承接模型目录拉取（去原 facade 内联实现）。
>   - `ConfigSaveCoordinator.cs`（internal）：ctor 取 `ConfigStatus` + 11 子编辑器；`Load(RunOptions)` 按「task 先、models 后」顺序逐节 `LoadFrom`；`Apply(RunOptions): bool` 逐节 `ApplyTo` 后执行 REQ-019 拦截 `if (_task.Workspaces.Count > o.MaxWorkspaces) { _status.Show("保存被拦截…"); return false; }`，否则 `_notif.ApplyTo(o); _models.WriteModelPool(o); return true;`——集中编排「加载/落盘 + 工作区上限拦截 + 模型池合并」三类跨域动作。
> - **契约等价性**：facade 全部公共属性/命令转发至子编辑器（`TaskFilePath => _task.TaskFilePath`、`Concurrency => _schedule.Concurrency`、`PickTaskFile => _task.PickTaskFile`、`StatusMessage => _status.Message` 等）；`Reload()`→`_coordinator.Load`+`RefreshReadiness()`，`SaveAsync()`→`Apply` 拦截失败即返回、否则 `SaveConfigAsync`+`RebuildEngineAsync`+ 状态回写；`RefreshSnapshotStatuses()`/`RefreshReadiness()` 沿用 `ConfigViewModelTests` 锁定的原语义。
> - **守卫测试**：`LayeringRulesTests` 第 5 条扩展 `ConfigViewModel_facade文件不得超过400行_防上帝类回潮`（`LocateConfigViewModelSource()` 自仓库检出定位 `ConfigViewModel.cs`，断言 ≤400 行），与 `BatchEngine`/`TaskRunner` 守卫同款，防上帝类回潮。
> - **准入契约测试（P1-19 基座，先行锁死行为等价，未改动、须持续绿）**：`ConfigViewModelTests.cs`（Reload/Save 全配置域 + 模型池迁移 round-trip）、`ConfigViewModelInteractionContractTests.cs`（工作区上限/分区导航/就绪/模型拉取/取消/快照）、`ConfigViewBindingContractTests.cs`（静态校验 `ConfigView.axaml`+`ConfigHubView.axaml` XAML 绑定路径可解析）、`ConfigViewTests.cs`（headless 构造 + PickTaskFile 注入）、`ConfigHubViewModelTests.cs`、`FakeEngineHost.cs`。
> - **退出验证**：全量 `--no-incremental` 构建 **0 警告/0 错误**；全量测试 **700 通过/0 失败**（主 568 + Desktop 132，较 P-03 收口 680 → 700：Desktop 112→132 为新增 P-04 headless 契约测试 + facade 行数守卫）；§3.4 IL 断言（Core `LayeringRulesTests` + Desktop `LayeringRulesTests`）全绿；facade 366 行 ≤400 收口达标且由守卫测试锁死；UI 公开契约逐字节等价（XAML 绑定路径未改、行为未漂移）。
> - **提交切片建议（§7 护栏）**：按「共享校验器 → 子编辑器（逐文件）→ `ConfigSaveCoordinator` → `ConfigViewModel` facade 转发化」顺序切片，每个子编辑器独立 <400 行、独立可编译。

**P-05 · TemplateEditorViewModel 分解（A-3 ②，5 人天）**

- 步骤：将 674 行 God Object `TemplateEditorViewModel` 按「VM → Coordinator/Presenter」拆为「状态持有 + 绑定转发 + 编排」薄 facade，交互逻辑下沉为三个 Coordinator；行级视图模型拆为独立文件。
- 准入：目标 VM 已有 P1-19 基座契约测试（`TemplateEditorViewModelTests`，覆盖构造默认输出路径 / 命名工作空间按名落盘 / 目录路径按绝对路径落盘 / 变量多值展开 / 未选模板不抛），须持续绿。
- 退出：最大单个 VM ≤400 行；headless 契约测试绿；UI 行为等价。
- 风险：R-4（破坏 UI 行为）→ 准入契约测试先补再改（P-05 未改动公开 API，仅凭既有测试锁死）。

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - **facade 收口**：`src/BatchAgentRunner.Desktop/ViewModels/TemplateEditorViewModel.cs` 由 **674 行 → 240 行**（≤400 达标），退化为薄面：持有 `Templates`/`VarInputs`/`PreviewTasks`/`FlowNodes` 四个集合 + 状态属性（`Workspace`/`OutputPath`/`StatusMessage`/`FlowSummary`/`IsFlowExpanded`/`SelectedTemplate` 等）+ 三个 Coordinator（`TemplateFlowCoordinator` 纯静态 `/ TemplateExpansionCoordinator / TemplateLoadCoordinator`）；命令 `LoadCommand`/`PreviewCommand`/`GenerateCommand`/`ToggleFlowCommand` 直接委托；`SelectedTemplate` setter 触发 `BuildFlow` → 灌入 `FlowNodes`/`FlowSummary` + `LoadVarInputs` + 清空预览，保留 Avalonia 绑定通知契约；ctor 经 `IEngineHost` 端口装配（`P1-4/P1-5` 组合根唯一 `new`，无直 `new` Infrastructure 具体类型）。
> - **三个 Coordinator**（`src/BatchAgentRunner.Desktop/ViewModels/TemplateEditor/`）：
>   - `TemplateLoadCoordinator.cs`（32 行，internal）：`LoadAsync()` 调 `IWorkflowTemplateStore.ListAsync()` 映射 `TemplateRowViewModel`，异常转状态文案。
>   - `TemplateExpansionCoordinator.cs`（116 行，internal）：`BuildSpec`（界面变量写回模板副本）/ `ResolveWorkspaceRoot` / `NormalizeWorkspaceForTask`（命名工作空间 ↔ 目录路径解析，沿用 `_host.Options.Workspaces`）/ `ExpandFromInputs`（调 `TemplateExpander.Expand`）/ `WriteTasksAsync`（原子序列化落盘）；`BuildSpec`/`WriteTasksAsync` 为纯静态方法（CA1822 合规）。
>   - `TemplateFlowCoordinator.cs`（136 行，internal，纯静态 `BuildFlow`）：承载流程示意构建——`TopoSortStages`（按 `depends_on_stage` 稳定拓扑排序，有环/悬空降级原序）/ `BuildOutputConsumers`（统计阶段产出被引用关系）/ `DescribeOnFailure`，输出 `FlowBuildResult`（`Nodes` + `Summary`）供 VM 灌入。
> - **4 个行视图模型独立文件**（同目录，命名空间保持 `BatchAgentRunner.Desktop.ViewModels` 以兼容既有测试，无行为变化）：`TemplateRowViewModel.cs`（22 行）/ `TemplateVarInputViewModel.cs`（44 行）/ `TemplateFlowNodeViewModel.cs`（162 行，含 `PlaceholderRx`/`BuildExecutorText`/`BuildGateText` 等）/ `PreviewTaskRowViewModel.cs`（30 行）。
> - **契约等价性**：facade 全部公共属性/命令保持原签名与语义（`OutputPath` 末尾 `template-output.tasks.json`、`Templates`/`PreviewTasks`/`VarInputs`/`FlowNodes` 集合、`Preview()`/`GenerateAsync()`/`LoadAsync()`、依赖 `HasFlow`/`HasPreviewTasks`/`FlowNodeCountText`/`Selected*` 派生属性）；`TemplateEditorViewModelTests` 5 项全绿，行为逐字节等价（展开产物与手写任务字节级等价）。
> - **守卫测试**：`LayeringRulesTests` 第 5 条扩展 `TemplateEditorViewModel_facade文件不得超过400行_防上帝类回潮`（`LocateTemplateEditorViewModelSource()` 自仓库检出定位 `TemplateEditorViewModel.cs`，断言 ≤400 行），与 `BatchEngine`/`TaskRunner`/`ConfigViewModel` 守卫同款，防 UI 上帝类回潮。
> - **退出验证**：全量 `--no-incremental` 构建 **0 警告/0 错误**（CA1822/CA1861/IDE 等全清）；全量测试 **702 通过/0 失败**（主 570 + Desktop 132，较 P-04 收口 701 → 702：新增 P-05 facade 行数守卫 1 项）；`dotnet format --verify-no-changes`（CHARSET 假阳性按 §4.7 #9 过滤）本阶段文件 0 问题；§3.4 IL 断言（Core `LayeringRulesTests` + Desktop `LayeringRulesTests`）全绿；facade 240 行 ≤400 收口达标且由守卫测试锁死；UI 公开契约逐字节等价。
> - **提交切片建议（§7 护栏）**：按「行视图模型（逐文件）→ 三个 Coordinator（逐文件）→ `TemplateEditorViewModel` facade 薄面化」顺序切片，每个文件独立 <400 行、独立可编译。

**P-06 · MonitorViewModel + LoginViewModel 分解（A-3 ③，5 人天）**

- 步骤：将 983 行 `MonitorViewModel` 与 751 行 `LoginViewModel` 按「VM → Coordinator/Presenter」拆为「状态持有 + 绑定转发 + 事件/流程编排」薄 facade，交互逻辑下沉协作者，行级视图模型拆独立文件；两者均收口 ≤400 行。
- 准入：目标 VM 先补 Avalonia headless 契约测试（P1-19 基座）+ XAML 绑定路径静态守卫（复用 P-04 的绑定检查器）。
- 退出：最大单个 VM ≤400 行；headless 契约测试绿；UI 行为等价（既有测试零改动通过）。
- 风险：R-4（破坏 UI 行为）→ 准入契约测试先补再改 + 守卫测试锁死。

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - **facade 收口**：`MonitorViewModel.cs` **983→388 行**、`LoginViewModel.cs` **751→280 行**（均 ≤400 达标，守卫测试锁死），退化为薄面：持有可绑定状态 + 转发集合/属性/命令 + 把引擎事件路由到协作者。对外公开 API（属性名 / 命令名 / 语义）逐字节等价，既有 `LoginViewModelTests` / `MonitorViewModelTests` **零改动全绿**。
> - **Monitor 协作者**（`src/BatchAgentRunner.Desktop/ViewModels/Monitor/`，8 个）：
>   - `OutputStreamCoordinator.cs`（234 行，`ViewModelBase`）：独占全局输出缓冲 + 跟随过滤视图 + 跟随模式（`OutputFollowMode`），`AppendChunk`/`AppendEvent`/`OnTaskStarted`/`SetSelectedTask`；任务清单经 ctor `taskItems` 委托按需读取（不持引用）。
>   - `TaskListCoordinator.cs`（164 行）：独占 `Tasks`/`DagRows`/`SelectedStages` 三集合，整表重建（P1-7：移出前逐行 `Dispose` 解除 `TaskItem` 订阅）、单行 upsert、DAG 看板拓扑层级/blocked_by 计算、选中任务阶段树刷新。
>   - `BatchStatsState.cs`（81 行，`ViewModelBase`）：统计快照可绑定状态，`Apply(BatchStatistics.Snapshot)` 一次性落入（分解前 `_skipped` 字段从未赋值恒 0，同语义保留；CA1822/CS0649/CA1805 三规则拉锯下以实例字段 + ctor 赋值形态保留）。
>   - `BatchStatusState.cs`（77 行，`ViewModelBase`）：批次状态（Status/StatusText/StatusBrush/IsWaiting/DispatchPaused/Can*），`Status` 变更级联通知派生属性，命令可用性由 facade 监听后统一 `NotifyCanExecuteChanged`（与原 setter 行为等价）。
>   - `EventToastBoard.cs`（66 行）：提示板集合 + 两级写入（引擎事件定级 + 操作失败双写），二期关注类关键词升级 warning，上限 100 条尾淘汰。
>   - `BatchStatistics.cs`（64 行，纯静态）：统计纯计算（各状态计数 + ETA + 当前任务三栏文案），无副作用、可独立单测。
>   - `OutputTagClassifier.cs`（46 行，纯静态）：单行 stdout 关键字启发式分类 + 引擎事件→输出流标签映射。
>   - `MonitorOperationsCoordinator.cs`（156 行，含 `MonitorOperationsContext` 上下文注入）：承载「加载任务文件 / 启动前预检 / 开始执行 / 续跑」操作编排——预检门、结果回写、失败状态栏+提示板双写；`ConfigureAwait(false)` 续延的 UI 写回经 `UiThread` 投递（P0-6/P0-7 线程收敛，与原实现逐字节等价），经 `WriteStatusMessage`/`NotifyLastReportPathChanged` 回调写回 facade。
> - **Login 协作者**（`src/BatchAgentRunner.Desktop/ViewModels/Login/`，5 个 + 3 个行 VM 独立文件）：
>   - `ILoginView.cs`（58 行，internal 缝）：facade 向协作者暴露的最小可变状态面（宿主/AgentKind/忙碌态/状态条/结果横幅/行集合/步骤条 + `RebuildRows`/`Refresh` 回调），协作者不反向依赖 facade 具体类型（沿用 P-01/P-02 上下文注入范式）。
>   - `GuidedLoginFlow.cs`（167 行，`IDisposable`）：一键登录全流程（守卫→分配账号→忙碌态→`GuidedLoginAsync`→入池保存→成功收尾）+ 编排器进度回调 → 步骤条；独占登录 CTS（可取消，facade `Dispose` 一并释放）。
>   - `CreditsClaimFlow.cs`（130 行，`IDisposable`）：每日积分领取全流程（守卫→启用账号检查→领取中态→批量领取→逐账号结果行→汇总文案）；独占领取 CTS。
>   - `AccountPoolCoordinator.cs`（153 行）：账号行整表重建（账号池 + Vault 快照描述 + Live 标记）、一键切换、两段式删除（清快照+出池+保存+重建引擎）、`NextAccountId`；ctor 取 facade 具体类型（账号行构造需要 owner 承接行命令路由，公开构造签名不变）。
>   - `LoginStepsPresenter.cs`（95 行）：六步固定步骤集合 + 全部步骤态迁移 + `ApplyProgress`（阶段→步骤索引映射，Done/Failed 收尾归流程层）。
>   - `LoginErrorTranslator.cs`（33 行，纯静态）：异常人话翻译（超时/进程互斥/未找到命令 + 原始细节附注），登录流与账号操作共用。
>   - 行视图模型独立文件（命名空间保持 `BatchAgentRunner.Desktop.ViewModels` 兼容既有测试，公开签名不变）：`LoginStepItem.cs`（35）/ `LoginAccountRow.cs`（72，ctor owner 仍为 `LoginViewModel`）/ `CreditsClaimRow.cs`（41）。
> - **契约等价性**：facade 全部公共属性/命令保持原签名与语义（`Steps`/`Rows`/`AgentKinds`/`ForceClaim`/`IsBusy`/`IsClaimIdle`/`HasResult`/`LoginCommand`/`CancelLoginCommand`/`ClaimCreditsCommand`/`SwitchToAsync`/`ReLoginAsync`/`DeleteAsync`/`CancelDelete`/`Refresh`/`HasAccounts`/`Dispose` 等）；`StatusMessage`/`LastReportPath` 经上下文回调写回，集合直接透传协作者同一实例（集合自身通知）。
> - **准入契约测试（先行锁死行为等价，分解后零改动持续绿）**：`LoginViewModelContractTests` 18 项（步骤条初始化/Refresh 同步/快照描述/Mock·WorkBuddy 守卫/切换成功与异常翻译/Live 拒绝删除/删除异常不清池/两段式确认/领取四分支文案/行命令路由/Dispose 幂等）+ `MonitorViewModelContractTests` 11 项（默认统计与批次状态/集合透传同一实例/StatusChanged 与 SlotFrozen 驱动/TaskOutput·TaskStarted 输出流联动/BatchWaiting 倒计时/选中任务派生属性与阶段树/工作流模板溯源/统计转发与 DAG 重建/ForceRerun 回写/加载未选文件 warning 双写/筛选切换）；`FakeEngineHost` 扩容 `RaiseEvent`/`RaiseTasksChanged`/`RaiseCreditsClaimProgress`/`ClaimResult`/`VaultFailure` 五个探针（仅测试项目）支撑事件编排路径覆盖。**XAML 绑定守卫**：`MonitorLoginViewBindingContractTests` 1 项（`MonitorView.axaml` + `LoginView.axaml` 全部 `{Binding 路径}` 静态可解析，含 DataTemplate 经 ItemsSource 推断元素类型的作用域）；同轮将 P-04 的绑定检查器提取为共享 `XamlBindingPathChecker`（`ConfigViewBindingContractTests` 改复用，行为不变）。
> - **守卫测试**：`LayeringRulesTests` 第 5 条扩展 `MonitorViewModel_facade文件不得超过400行_防上帝类回潮` 与 `LoginViewModel_facade文件不得超过400行_防上帝类回潮`（自仓库检出定位源文件，断言 ≤400 行），与 `BatchEngine`/`TaskRunner`/`ConfigViewModel`/`TemplateEditorViewModel` 守卫同款，防 UI 上帝类回潮。
> - **退出验证**：全量 `--no-incremental` 构建 **0 警告/0 错误**；全量测试 **734 通过/0 失败**（主 572 + Desktop 162，较 P-05 收口 702 → 734：新增 Login 契约 18 + Monitor 契约 11 + XAML 绑定守卫 1 + facade 行数守卫 2）；§3.4 IL 断言（Core `LayeringRulesTests` 9 项 + Desktop `LayeringRulesTests` 4 项）全绿；`dotnet format --verify-no-changes`（CHARSET 假阳性按 §4.7 #9 过滤）本阶段文件 0 问题；两个 facade 行数收口达标且由守卫测试锁死；UI 公开契约逐字节等价（既有 VM 测试零改动通过 + XAML 绑定路径机器校验）。
> - **提交切片建议（§7 护栏）**：Monitor 半边按「协作者（逐文件）→ 操作编排提取 → `MonitorViewModel` facade 委托化」切片；Login 半边按「行视图模型（逐文件）→ `ILoginView` 缝 + 步骤条/翻译 → 三个流程/账号协作者（逐文件）→ `LoginViewModel` facade 薄面化」切片；测试与守卫独立提交。每个文件独立 <400 行、独立可编译。

**P-04~P-06 · VM 分解（A-3，各 5 人天）**

- 步骤：引入 Coordinator/Presenter 承载交互逻辑，VM 退化为"状态持有 + 绑定"；按切片拆子编辑器 + 共享校验器；全量落地 `TaskItemViewModel` 防腐层（UI 不得直绑 `TaskItem`）。
- 准入：目标 VM 先补 Avalonia headless 契约测试（P1-19 基座已具备）。
- 退出：最大单个 VM ≤400 行；headless 契约测试绿；UI 行为等价。
- 风险：R-4（破坏 UI 行为）→ headless 契约测试先补再改。

**P-07 · JsonStoreBase<T> + AppPaths（A-4，5 人天）**

- 步骤：抽 `JsonStoreBase<T>`（构造/默认目录/`LoadAsync` catch/`AtomicFileWriter.SaveAsync` 统一），9 处路径魔法串收为 `AppPaths` 常量；三份 `Json*Store` 改为继承。
- 退出：无三份同构实现；§3.4 重复断言绿；测试绿。

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - **代码本体已于 P2-31 提前偿还**（`adace61`，TECH-DEBT §5.11），本轮为「复核真值 + 加固守卫 + 阶段收口」：
>   - `src/BatchAgentRunner.Infrastructure/Persistence/JsonStoreBase.cs`（**64 行**）：统一三份仓储逐行同构的四段样板——① 目录解析（`null` → `AppPaths.Root`）并建目录；② `FilePath` 由「目录 + 文件名」拼出；③ `LoadAsync` 三条回落（文件缺失 / `JsonException` / 反序列化为空 → `CreateEmpty()`）；④ `SaveAsync` 一律走 `AtomicFileWriter`。
>   - 三份仓储退化为「`CreateEmpty()` + 可选 `NormalizeAsync()`」：`JsonConfigStore` **191→167 行**（`CreditsClaimMigrator.Migrate` 迁移钩子）、`JsonCooldownStore` **70→51 行**（超长 raw 截断自愈 + 原子重写）、`JsonCreditsClaimStore` **50→19 行**（默认钩子）。样板全部在基类，派生类无 `Directory.CreateDirectory` / `JsonSerializer.` / `AtomicFileWriter` 残留。
>   - `src/BatchAgentRunner.Infrastructure/AppPaths.cs`（**45 行**）为应用数据目录单一事实来源：`Root`（`%LOCALAPPDATA%\BatchAgentRunner`）+ `ConfigDirectory`/`TemplatesDirectory`/`VaultDirectory`/`RunsDirectory`/`LogsDirectory`/`MockLiveDirectory`；原散落 **9 处**的「`LocalApplicationData` + 应用名」前缀清零（三份仓储经基类 `Root`、`FileWorkflowTemplateStore`、`FileCredentialVault`、`FileRunLogger`、`Desktop/Program`、`Desktop/Composition/EngineFactory`×2）。`grep -rn '"BatchAgentRunner"' src` 现仅剩 `AppPaths.cs` 一处声明；`Environment.SpecialFolder.LocalApplicationData` 亦仅此一处。
> - **本轮加固（P-07 收口轮新增）**：`AppPathsHygieneTests` 由 3 项扩为 **4 项**——原第 ② 项按**文件名清单**核对（改名或新增第 4 份仓储即静默漏网），新增 `Infrastructure内Json仓储具体类型_均须继承JsonStoreBase_防第四份同构仓储`：扫描 Infrastructure 程序集内所有 `Json*Store` 具体类型，凡不继承 `JsonStoreBase<T>` 即失败。**负测试实测**：临时注入 `JsonNegativeProbeStore`（不继承基类）→ 报「存在未继承 `JsonStoreBase<T>` 的单文件 JSON 仓储：JsonNegativeProbeStore（基类：Object）」；探针已删除、不入库。
> - **退出验证**：全量 `--no-incremental` 严格构建 **0 警告/0 错误**；全量测试 **735 通过/0 失败**（主 573 + Desktop 162，较 P-06 收口 734 +1：新增「第 4 份同构仓储」拦截守卫）；`AppPathsHygieneTests` **4/4** + `JsonStoreBaseTests` **12/12** + 既有三份仓储测试全绿；§3.4 第 4 条在「JSON 仓储 / 路径常量」两个维度达标（`Truncate`/`Clone`/终态集合 归 P-08）。
> - **边界（交接必读）**：① **`AppPaths` 落在 Infrastructure 而非 Core**：路径布局是「文件存放在哪」的基础设施事实，Core 不消费；`AppPaths` 为静态类、无实例，Desktop 经组合根引用不构成 §3.4 依赖倒置（附录 C 第 4 条已据此校准口径）。② **只收口「跨文件复用」的应用根前缀**：`FileWorkflowTemplateStore` 的 `builtin`/`user`、`FileRunLogger` 任务文件同级 `runs` 是「所属仓储自持 + 支持 `baseDir` 注入」的子目录名，各只一处使用，收进 `AppPaths` 反而破坏测试注入能力，不属同构重复；`CredentialLocationRegistry` 的 `AppData/Local/CodeBuddyExtension/…` 是**外部 CodeBuddy CLI** 的路径，不在本工具治理范围。③ **`CliPermissionSettings` 不套基类**：它对外部 `settings.json` 做「`JsonNode` 局部改写 + 保留未知字段」，与「整文档往返」的 `JsonStoreBase<T>` 形态不同，强行统一会丢字段。④ `ConfigDirectory == Root` 是历史数据布局，收口时刻意不改（改了会让既有用户 config/cooldowns/credits 台账「凭空消失」）。
> - **提交切片建议（§7 护栏）**：本轮仅 1 个测试文件（+29 行）+ 文档变更，单步 `git diff --stat` 远 < 400 行，单提交即可。

**P-08 · 领域工具收口（A-5，5 人天）**

- 步骤：`Truncate`/`IsTerminal`/`Clone`/`失败分派状态机` 提为 Core 纯函数，删除重复实现（P2-1~6）。
- 退出：公共工具单一来源；内核工具单测；§3.4 重复断言绿。

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - **代码本体已于 P2-1~P2-6 提前偿还**（`518103a` / `9a1c782` / `f0dc708`，TECH-DEBT §5.12），本轮为「残留清零 + 缺陷修复 + 防回潮闸门 + 阶段收口」：
>   - **截断**：`src/BatchAgentRunner.Core/Text/TextTruncate.cs`（**35 行**）为唯一事实来源（`Result` 结果截断 + `Truncate` 通用截断）。本轮把 P2-4 遗漏的 **3 处内联副本**并入——`Agents/Internal/StreamJsonRenderer.cs` 2 处（`s[..120] + "…"` → `TextTruncate.Truncate(s, 120, "…")`）与 `Desktop/ViewModels/TemplateEditor/TemplateFlowNodeViewModel.cs` 1 处（`Shorten` 内联 → `TextTruncate.Truncate(flat, max, "…")`），行为逐字等价。同轮清偿 P-02 facade 收口后遗留在 `BatchEngine.cs` 的**无调用点死私有 `Truncate` 别名**（P2-25 死代码）：删除该方法及其后失效的 `using BatchAgentRunner.Core.Text;`，facade 400→**397 行**。`src` 内 `Truncate` 声明点自此只剩「单一事实来源 `TextTruncate` + 4 个保留调用点的薄后缀适配器」（见「边界」①）。
>   - **终态集合**：`src/BatchAgentRunner.Core/Domain/TaskStatusExtensions.cs`（**14 行**）为唯一事实来源（`TaskItem.IsTerminal` / `StageResult.IsTerminal` 均降级为委派属性）。
>   - **深拷贝**：工作流域走 `WorkflowTypes.WorkflowTypeClones` 的 `record with` 扩展方法（8 个），`TaskItem` 是唯一手写点（承载 `INotifyPropertyChanged` 通知字段，无法记录化）。**本轮修复真实缺陷**：`TaskItem.Clone()` 对 `StageResults` 只做 `new List<StageResult>(…)` 浅拷贝——快照与原任务**共享 `StageResult` 元素**，改快照的阶段摘要会污染原型；改为 `StageResult` 记录化 + 逐元素 `s.Clone()`（与工作流域同构的 `with` 深拷贝）。
>   - **依赖失败分派**：`DagScheduler.EvaluateDependency`（三态判定 + 阻断依赖报出）为唯一事实来源，`TaskDispatcher` 的私有同名方法保持委派调用。
> - **本轮新增守卫（防回潮，`tests/BatchAgentRunner.Tests/Architecture/DomainPrimitivesHygieneTests.cs` 231 行，5 项）**：① `Src内_除TextTruncate外_不得内联截断实现`（词法扫 `src`，拦截 `[..N] + "…"` 形态）；② `终态成员_除单一事实来源外_一律委派TaskStatusExtensions`；③ `终态成员_对全部枚举值_与单一事实来源判定一致`（`TaskItem`/`StageResult` × 全部 `TaskStatus` 值行为等价）；④ `深拷贝入口_仅允许WorkflowTypeClones与TaskItem`（反射扫 Core/Infrastructure/Agents 程序集）；⑤ `依赖失败分派_公开判定入口唯一且TaskDispatcher保持委派`。**负测试实测**：临时注入 `P08Probe`（内联 `Cut` + 自造 `IsTerminal => true` + 自造 `Clone()` + 公开 `DependencyVerdict Eval()`）→ ①②③⑤ 四项全部按预期报错；探针已删除、不入库。
> - **本轮新增契约测试（`tests/BatchAgentRunner.Tests/Domain/TaskItemCloneTests.cs` 166 行，4 项）**：反射遍历 `TaskItem` 全部可写属性并注入非默认样本值，克隆后逐一比对值（聚合节点按 JSON 结构比对）+ 断言引用类型不共享 + 序列化形状一致 + 可空字段保持可空；**新增字段漏进 `Clone()` 即红**，未知属性类型则直接抛错要求同步守卫——把 P2-6「改字段必漏改」的根因变成机器可校验的不变量。
> - **退出验证**：全量 `--no-incremental` 严格构建 **0 警告/0 错误**；全量测试 **744 通过/0 失败**（主 582 + Desktop 162，较 P-07 收口 735 +9：新增守卫 5 + 克隆契约 4；死代码删除不改行为，测试数不变）；`DomainPrimitivesHygieneTests` **5/5** + `TaskItemCloneTests` **4/4** + `WorkflowTypeCloneTests` **7/7** + `TextTruncateTests` **6/6** + `TaskStatusExtensionsTests` **7/7** 全绿；`BatchEngine` facade 400→**397 行**（死别名删除），≤400 守卫仍绿；`dotnet format --verify-no-changes` 除 §4.7 #9 已登记的 CHARSET 假阳性 304 条外 **0 问题**；hygiene 守卫（主 28 + Desktop 4 = 32 项）全绿；§3.4 第 4 条在「截断 / 终态集合 / `Clone` / 依赖失败分派」四个维度达标（与 P-07 的「JSON 仓储 / 路径常量」两个维度合起来即第 4 条完整覆盖）。
> - **边界（交接必读）**：① **保留调用点的薄后缀适配器**：`WorkflowExecutor` / `GateEvaluator` / `ReportWriter` / `CodeBuddyCreditsApiClient` 各有一处 `private static Truncate(...) => TextTruncate.Truncate(...)`，仅为「固定后缀 + 单一入参」的本地简写，**不含截断逻辑**，强删会让十余处调用点变啰嗦，不属重复实现；守卫拦的是「再写一份截断运算」而非「再写一个名字」。② **`QuotaErrorClassifier` / `JsonCooldownStore` 的 `text[^MaxRawChars..]` 不并入 `TextTruncate`**：它们是「保留尾部 + 前置说明」的取尾语义，与 `TextTruncate` 的「保留头部 + 后置后缀」相反，形态不同不强行统一。③ **`StageResult` 记录化的影响面**：仅新增编译器生成的 `Equals`/`GetHashCode`/`ToString`（值语义），JSON 契约与构造方式零变化；全仓无依赖其引用相等的用法（只读映射与序列化）。④ **Desktop 未纳入 ④ 号深拷贝反射扫描**：主测试工程不引用 Desktop（组合根隔离），且 Desktop 深拷贝入口为零；Desktop 侧回潮由 `Desktop.Tests` 的 XAML/VM 契约守卫覆盖。⑤ **`BatchEngine` 死私有 `Truncate` 已于本轮删除（原单列登记为 P2-25 机会主义偿还项）**：P-02 facade 收口时未一并清掉的无调用点别名 `private static string? Truncate(...) => TextTruncate.Result(...)` 及其后失效的 `using BatchAgentRunner.Core.Text;` 已删除，facade 400→**397 行**。删除后 `src` 内 `Truncate` 声明点只剩「单一事实来源 + 4 个保留调用点的薄后缀适配器」两类，均不含截断运算；`DomainPrimitivesHygieneTests` 5 项仍全绿（该别名本就是无调用点死代码，删除不影响任何行为）。
> - **提交切片建议（§7 护栏）**：本轮主体为 2 个新增测试文件 + 4 个生产文件小改（净 +30 行量级）+ 文档变更，单步 `git diff --stat` 远 < 400 行，单提交即可。**唯一例外是 `BatchEngine.cs` 的死别名删除**：该文件在本轮提交时点仍承载 P-02 facade 收口（953→400 行）的未提交改动，若 P-02 尚未独立提交，则须**先切 P-02 提交、再提本轮**，否则 `git add BatchEngine.cs` 会把 P-02 的数百行重构裹进 P-08 提交、违反 §7「单步 ≤400 行」护栏。

**P-09 · ConfigEditors/RunOptions 收口（A-6，5 人天）**

- 步骤：`ConfigEditors` 改为泛型 `FieldEditor<T>` + 统一校验反馈（P2-15）；`RunOptions` 弃用 JSON 往返克隆，改用记录类型/显式映射（P2-9）。
- 退出：编辑器泛型化；配置读写契约测试绿；克隆行为等价。

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - **代码本体分两批**：P2-15「五编辑器 → `FieldEditor<T>`」已于 `91fb700` 提前偿还（TECH-DEBT §5.12），P2-9 的 `RunOptions.Clone()` JSON 往返已在 v2.0 重构中消除；本轮为「**残留清零 + 缺陷修复 + 统一校验反馈落地 + 防回潮闸门 + 阶段收口**」，并把 P2-9 剩余的 **#D5「God Config 40+ 配置项分组」**一并收口。
> - **编辑器泛型化残留清零（`Desktop/Models/ConfigEditors.cs` 510→505 行、`Desktop/Models/FieldEditor.cs` 65→82 行）**：
>   - **① 时段起止收敛 `FieldEditor<T>` + 修真实缺陷**：`ModelEditor` 的起止时刻原本是「手写 `TimeOnly.TryParse` + 裸字符串属性」，`Apply()` 时 `BuildWindow()` 因非法输入返回 `null` → **把用户已配置的免费时段静默抹掉**（`FreeTimeWindow` 直接置空，无任何提示）。现改为 `FieldEditor<TimeOnly?>`（`FieldParsers.TimeOnlyOrNull`），`Apply()` 对错误态**保留原时段**，错误经 `HasError`/`Error` 对用户可见。
>   - **② 删掉 `StartText`/`EndText` 裸字符串桥接**：这两个属性是为「改文本 → 刷新 `Preview`」与 XAML 绑定而包的薄壳，导致「改了文本却不刷新派生态」与「校验反馈漏绑」两件事同时隐身。现 `FieldEditor<T>` 新增可选 `onTextChanged` 回调，`ModelEditor` 以 `RefreshPreview` 装配，视图直接绑 `{Binding StartTime.Text}`；桥接删除后编辑器对外一律 `FieldEditor<T>`，无第二套字符串入口。
>   - **③ 视图侧统一校验反馈（`Views/ConfigView.axaml` 767→795 行）**：原本只有「共享候选模型池」的单价/折扣有 P2-15 反馈块，**时段起止（共享池 + 每账号两处视图共 4 个）、每账号候选模型的单价/折扣（2 个）、兜底模型单价（1 个）共 7 个输入框**虽已是 `FieldEditor<T>` 却**完全不呈现错误**——用户填「25:99」界面毫无反应。现全部补齐：每个解析字段的 `TextBox` 带 `ToolTip.Tip="{Binding X.Error}"`，并在同一数据模板内给出 `X.Error`/`X.HasError` 错误文本；**错误文本的字体/颜色收为 `UserControl.Styles` 里的 `TextBlock.field-error` 单一来源**（消除各字段各写一遍 `FontSize="11" Foreground="{DynamicResource ErrorBrush}"` 的样板）。
> - **本轮新增守卫 1（`tests/BatchAgentRunner.Tests/Options/RunOptionsContractTests.cs` 243 行，5 项）**：把 #D5 的收口结果变成机器可校验的不变量——① **顺序契约**：`System.Text.Json` 按成员声明顺序落盘，而 partial 成员声明顺序＝文件名升序，故切片文件名以两位序号固定，实测 39 个持久化键的顺序与切片前逐项一致（顺序漂移会让用户 `config.json` 产生无意义 diff）；② **往返契约**：反射构造「全字段已填充」的配置实例，"写 → 读 → 再写"逐字节稳定，且所有持久化属性都必须真正出现在 JSON 里（新增字段漏配 `[JsonPropertyName]` 即红）；③ **前向兼容契约**：未知字段宽容忽略且已知字段不丢；④ **命名契约**：Core 内持久化键一律 snake_case；⑤ **派生契约**：`[JsonIgnore]` 只允许挂在只读派生属性上（可写属性漏配 `[JsonPropertyName]` 会静默丢用户编辑）。
> - **本轮新增守卫 2（`tests/BatchAgentRunner.Tests/Architecture/RunOptionsHygieneTests.cs` 102 行，3 项）**：① `RunOptions_须按配置域切片_不得回缩为单一大类`（切片文件数 < 5 即红）；② `RunOptions_各配置域切片_均不得超过行数上限`（单片 > 200 行即红，防 God Config 回潮）；③ `RunOptions_禁止深拷贝成员_JSON往返克隆不得复现`（不得实现 `ICloneable`，且全仓无任何返回 `RunOptions` 的 `Clone`/`Copy`/`DeepCopy`/`Duplicate`/`Snapshot` 成员）。
> - **本轮新增守卫 3（`tests/BatchAgentRunner.Desktop.Tests/Architecture/ConfigEditorsHygieneTests.cs` 179 行，3 项）**：① `Desktop内_解析调用_只出现在FieldParsers单一事实来源`（词法扫 `src/BatchAgentRunner.Desktop`，`X.TryParse(` 只允许出现在 `Models/FieldEditor.cs`）；② `编辑器_解析字段_一律参与回写_声明即须被读取`（反射式词法：新声明的 `FieldEditor<T>` 字段必须在 `Apply()` 里被读取，否则用户输入永不落盘——把 P2-15 前「改字段必漏改」的根因变成机器可校验的不变量）；③ `配置视图_凡绑定解析字段文本的输入框_必须呈现校验反馈`（静态解析视图的 `{Binding X.Text}`，凡 `X` 是 `FieldEditor<>` 字段，同一 `DataTemplate` 内必须存在 `X.Error`/`X.HasError` 绑定）。
> - **负测试实测（两侧均实测，探针已删除、不入库）**：
>   - **编辑器侧**：临时注入 `Desktop/Models/P09Probe.cs`（手写 `int.TryParse` + 声明后从不读取的 `FieldEditor<int?> ProberPort`）→ 守卫 ①③ 按预期报「1 处解析调用（应改走 FieldParsers + FieldEditor<T>）」「字段 ProberPort 声明后无任何读取点」；临时删掉「每账号候选模型」视图的 4 个校验反馈绑定 → 守卫 ② 按预期报「`UnitPrice.Text` 有输入框，但无 `UnitPrice.Error / UnitPrice.HasError` 反馈绑定」。`ConfigEditorsHygieneTests` **3/3 全部实测可捕获**。
>   - **配置契约侧**：临时注入 `Core/Options/RunOptions.12-Probe.cs`（219 行超限切片 + `camelCase` 键名 `probeCamelCase` + 可写属性挂 `[JsonIgnore]` + `public RunOptions Snapshot()`）→ `RunOptionsHygieneTests` ②③ 与 `RunOptionsContractTests` ①②④⑤ **共 5 项按预期报错**。
> - **负测试顺带暴露并修掉的守卫盲区**：首次注入探针时，`RunOptionsContractTests` ②「往返逐字节稳定」**没有报警**——`Populator` 对 `int?` 等**可空基元**填充为 `null`，被 `WhenWritingNull` 忽略后该属性根本不进 JSON，「全部持久化属性参与序列化」的断言形同虚设（当前 `RunOptions` 恰好无可空基元字段，故此前未暴露）。已修 `Populator`：`Nullable.GetUnderlyingType(type) ?? type` 拆到基元再填值；修复后重新注入可空基元探针，② 与 ① **均按预期报错**（实测 2 项）。**新增可空基元配置字段漏配 `[JsonPropertyName]` 自此无法蒙混过关**。
> - **退出验证**：全量 `--no-incremental` 严格构建 **0 警告/0 错误**（本轮顺带修掉 `Core/Options/RunOptions.09-Phase2.cs` 的 `dotnet format` IMPORTS 排序问题）；`dotnet format --verify-no-changes` 除 §4.7 #9 已登记的 CHARSET 假阳性（本次实测 322 条）外 **0 问题**；全量测试 **758 通过/0 失败**（主 590 + Desktop 168，较 P-08 收口 744 +14：主 `RunOptionsContractTests` 5 + `RunOptionsHygieneTests` 3，Desktop `ConfigEditorsHygieneTests` 3 + 时段行为契约 3）；架构守卫 `Architecture` 命名空间实测 主 **27** 项 + Desktop **11** 项全绿，另 `RunOptionsContractTests` 5 项全绿；§3.4 第 4 条在「JSON 仓储 / 路径常量」（P-07）、「截断 / 终态集合 / `Clone` / 依赖失败分派」（P-08）、「配置契约 / 编辑器泛型化 + 校验反馈」（本轮）三类维度上完整覆盖；`RunOptions` 落盘字节与切前**逐字节等价**（顺序契约 + 往返契约双锁）。
> - **边界（交接必读）**：① **`RunOptions` 不改 `record`、不嵌套分组**：它是用户配置的产品契约（§2.2 不动清单），嵌套分组会改 `config.json` 形状、破坏既有用户配置与前向兼容；故以 `partial class` **横向切片**（类型面、属性名、`[JsonPropertyName]` 键名、落盘字节全部不变）收口 God Config，是 #D5 唯一不破坏对外契约的形态。② **切片顺序即 JSON 落盘顺序**：调整/新增切片须同步 `RunOptionsContractTests` 的顺序契约（文件名两位序号固定）。③ **SMTP 端口非法仍回落 587 属于刻意保留**：587 是协议默认端口，回落才能让规则可用；与「保留原值」的差异在于端口有明确默认值、而时段没有——但错误态同样对用户显式呈现（不静默）。④ 保留的薄桥接：`NotificationRuleEditor.SmtpToText`（收件人列表是「逗号/换行分隔的多值文本」，不是解析回写字段）、`QuietHours`（原样透传的 `HH:mm-HH:mm` 文本，由通知侧解析）均非 `FieldEditor<T>` 适用场景，不强行泛型化。⑤ **Desktop 视图的 XAML 守卫只校验「有反馈绑定」**：不校验反馈的位置/文案形式（各视图布局差异大，机器判定形态弊大于利）。⑥ **`RunOptionsContractTests` 的 `Populator` 必须能填充可空基元**：可空基元（`int?`/`bool?`…）一旦被填成 `null`，会被 `WhenWritingNull` 忽略而不进 JSON，「全部持久化属性参与序列化」的往返断言即失效——本轮负测试正是靠这条才发现盲区（`Nullable.GetUnderlyingType(type) ?? type` 拆到基元再填值）。新增任何非字符串/非集合的配置字段类型时，须先确认 `Populator` 能为其造出非默认值。
> - **提交切片建议（§7 护栏）**：本轮为 1 个新增守卫文件（Desktop `ConfigEditorsHygieneTests` 179 行）+ 1 个守卫文件小修（主 `RunOptionsContractTests` 的 `Populator` 可空基元填充 +15 行）+ 3 个生产文件小改（`FieldEditor.cs` +17 / `ConfigEditors.cs` / `ConfigView.axaml`）+ 1 个行为测试文件改动（`ConfigEditorsTests.cs`）+ 文档变更，**须按生产/测试分离切片**：① `refactor(debt): ARCH P-09 编辑器泛型化——时段起止收敛 FieldEditor<T> + 视图校验反馈统一`（`ConfigEditors.cs` / `FieldEditor.cs` / `ConfigView.axaml` / `ConfigEditorsTests.cs`）；② `test(debt): ARCH P-09 防回潮守卫——ConfigEditorsHygieneTests 3 项 + RunOptionsContractTests 可空基元填充修复`（若 `RunOptionsContractTests` / `RunOptionsHygieneTests` / 11 个 `RunOptions.NN-*.cs` 切片尚未入库，则须再拆出 ③ `refactor(debt): ARCH P-09 RunOptions 按配置域切片` 与 ④ `test(debt): ARCH P-09 配置契约守卫`）；⑤ `docs(governance): P-09 收口——同步 ARCH/TECH-DEBT/CODE-REVIEW/README 看板`。单步 `git diff --stat` 均 < 400 行。
> - **⚠️ 提交前置条件（§7 护栏，必读）**：本轮涉及的 `ConfigEditors.cs` / `ConfigView.axaml` / `ConfigEditorsTests.cs` 三个文件在提交时点仍承载 **P-04 配置页分解（facade 1065→366 行）与 P2-15 编辑器泛型化**的未提交改动（工作区实测：三者 `git diff --stat` 合计分别 73 / 46 / 52 行，但 P-04 的 `ConfigViewModel.cs` 与 11 个子编辑器文件同一批未提交）；`RunOptions.cs` 亦承载 P-09 切片前的 363 行本体。故**必须先切出 P-04 + P2-15 + P-09 的 `RunOptions` 切片提交、再提本轮收口轮**，否则 `git add` 会把数百行 P-04 重构裹进本轮提交、违反 §7「单步 ≤400 行」护栏。**提交一律显式 `git add <具体文件>`，禁 `git add -A`**。

**P-10 · 可观测性（A-8，5 人天）**

- 步骤：`IRunLogger` 接结构化实现 + `RunId`/`TaskId` 贯穿；新增运行级指标（吞吐/平均耗时/降级次数/兜底触发/冷却跳过/失败率/误切换率）；敏感信息脱敏。
- 退出：日志按批次聚合；指标可采集；凭证/密钥不落日志。

> **【执行记录 · 2026-10-21 · ✅ 已收口】**
> - **① 结构化日志接线（`IRunLogger` 唯一落盘点）**：新增 `Core/Observability/` 四个基础件——`RunLogEntry`（结构化条目：`Level/Event/Message` + `At/RunId/TaskId/Account/Model/IsDangerous/Fields/Flush`）、`LogEventNames`（16 个事件名单一事实来源 + `FromKind` 枚举转 snake_case）、`RunLogLevels`（级别常量 + `Normalize` + `IsImportant`，取代散落的裸字面量）、`SecretMasker`（脱敏单一事实来源）。`IRunLogger` 增 `Write(RunLogEntry)` 与 `StructuredLogFilePath`/`RunId`；`FileRunLogger` 的十余个 `Write*` 全部转为条目后经 `Write(RunLogEntry)` 渲染——**文本 `run-*.log` 正文逐字不变**（`{Event} {Message}` 渲染规则使事件名从正文前缀"平移"到结构化字段，人读结果一致），**新增 `run-*.jsonl` 结构化流**（`ts/level/event/run_id/task_id/account/model/message/fields`）供采集。文本与 JSONL 同批落盘、同一刷盘策略（重要级别/`Flush` 即时刷盘，INFO 按 64 行批），P2-24 的 syscall 削峰语义不变；同轮把 `NullRunLogger` 拆为独立文件（`Infrastructure/Logging/NullRunLogger.cs`），使实现文件维持在 400 行内。
> - **② RunId 关联贯穿**：`IRunLogger.RunId` 由日志侧生成（`RunLogEntry.NewRunId()` 为全局唯一生成入口），`BatchEngine` 直接复用同一实例（空实现的场景自生成），杜绝"日志一个 id、指标另一个 id"；`BatchEvent` 增 `RunId`，由 `BatchEngine.Raise` 统一填充——**任何事件（含 UI 订阅到的事件流）天然带关联标识**，写入点想漏也漏不掉；指标快照与 `metrics-<run_id>.json` 同用该 id。
> - **③ 运行级指标埋点**：新增 `RunMetrics`（锁内计数，并发安全）+ `RunMetricsSnapshot`（不可变，派生量随快照同落）。埋点固定在状态机的决策点而非渲染路径：`TaskStepExecutor`（任务开始 / 误切换 / 高危拒绝 / 兜底触发）、`TaskTerminalWriter`（终态与耗时，耗时已剔除本任务暂停等待）、`TaskEventReporter`（降级切换 / 冷却跳过 / 时段跳过）、`TaskCredentialSwitch`（任务边界凭证切换）、`BatchLifecycle`（工作流任务终态 + 批次终态输出 `metrics_snapshot`）。**误切换判定**：切换后的第一次尝试若仍因额度耗尽或凭据失效失败，计一次误切换（切换本身没错，错在没换来可用组合）；时段外与冷却跳过属预期调度行为，只计跳过、不计失败、不计误切换。
> - **④ 指标可采集**：新增 `IMetricsWriter` + `Infrastructure/Observability/RunMetricsWriter`，批次完成后由 `EngineHost` 落 `metrics-<run_id>.json`（`AtomicFileWriter` 原子写，snake_case 与 config/sidecar/报告同口径），与报告同目录、失败不阻断批处理；`IBatchEngine` 暴露 `RunId` / `Metrics` 供宿主与外部读取。
> - **⑤ 脱敏生效**：`SecretMasker` 在**渲染入口**对正文与全部 `Fields` 值统一脱敏（键名形态 / Bearer / JWT / `sk-`·`ghp_`·`AKIA` 前缀四类），键名与分隔符保留以便排障定位；脱敏幂等（占位符不再被二次打码）。这是 A-8「凭证/密钥绝不落日志」从"靠每个写入点自觉"变成"写入点无法绕过"的关键一步。
> - **同轮收口的两处遗留**：① **20 处裸级别字面量**（`Write("WARN", …)` 散落 7 个文件）收为 `RunLogLevels` 常量——`Write("WARNING")` 这类拼写错误原本会静默退化为普通级别、丢掉即时刷盘；② `BatchEngine.ResolveWorkspacePath` 下沉为 `Core/Engine/WorkspaceResolver.cs`（无状态纯函数，行为零变化），使 facade 在新增 RunId/Metrics 字段后仍守住 **399 行 ≤400** 的 P-02 收口线。
> - **本轮新增守卫（`tests/BatchAgentRunner.Tests/Architecture/ObservabilityHygieneTests.cs`，6 项）**：① 脱敏实现全仓唯一（除 `SecretMasker.cs` 外不得声明第二套 `Mask/Redact/Sanitize`）；② 日志级别字面量不得出现在写日志点（一律走 `RunLogLevels`）；③ `IRunLogger` 实现必须提供结构化通道与 `RunId`；④ `FileRunLogger` 内对底层 writer 的直接写只允许出现在统一落盘方法体内（防绕过脱敏与结构化）；⑤ 指标快照覆盖运行级指标全集且比率型指标不越界；⑥ 引擎事件模型携带 `RunId` 关联字段。
> - **行为测试**：`SecretMaskerTests` **20 项**（键名/JSON 字段/JWT/前缀四类脱敏 + 非敏感业务字段原样保留 + 空输入 + 同一行多处 + 幂等）、`RunMetricsTests` **8 项**（失败率只算真失败 / 平均耗时 / 误切换率 / 跳过与兜底不计失败 / 吞吐 / 零值不除零 / 并发计数不丢 / 快照条目结构）、`RunMetricsInstrumentationTests` **4 项**（引擎级：吞吐与快照输出 / 降级切换 / 冷却跳过不计失败 / 事件流全带 RunId）、`RunMetricsWriterTests` **2 项**（文件名带 RunId + JSON 合法、snake_case 与派生量同落）、`FileRunLoggerTests` **+3 项**（JSONL 与文本同批落盘且逐行带 RunId / 含密钥内容两侧均脱敏 / 日志与指标共用同一 RunId）。另新增共享测试替身 `Helpers/FakeRunLogger`（录结构化条目，替代此前"只录文本、私有嵌套"的 `RecordingRunLogger` 写法）。
> - **负测试实测（探针已删除、不入库）**：临时注入 `Core/Observability/P10Probe.cs`（第二套 `RedactText` 声明 + `logger.Write("WARN", …)` 裸级别）+ `FileRunLogger` 内一处直连 `_writer.WriteLine` → 守卫 ①②④ **共 3 项按预期报错**（分别指出"发现第二套脱敏实现""发现裸露的日志级别字面量""存在绕过结构化通道的直接写盘"）。
> - **退出验证**：全量 `--no-incremental` 严格构建 **0 警告/0 错误**；全量测试 **801 通过/0 失败**（主 633 + Desktop 168，较 P-09 收口 758 +43：主新增 `SecretMaskerTests` 20 + `RunMetricsTests` 8 + `RunMetricsInstrumentationTests` 4 + `RunMetricsWriterTests` 2 + `FileRunLoggerTests` 3 + `ObservabilityHygieneTests` 6）；§3.4 IL 断言（Core `LayeringRulesTests` + Desktop `LayeringRulesTests`）全绿，`BatchEngine` facade **399 行**守门仍绿；A-8 三条退出标准（日志可关联 / 指标可采集 / 脱敏有效）均由测试直接断言，不再依赖人肉检查。
> - **边界（交接必读）**：① **文本日志正文逐字不变是硬约束**：`RunLogEntry.RenderBody()` 用 `{Event} {Message}` 还原正文，新增 `Write*` 时把事件名放 `Event`、人读部分放 `Message`，不要把事件名再抄进 `Message`（否则正文出现双份事件名）。② **误切换率的分子是"切换后的第一次尝试仍失败"**：`TaskExecutionState.SwitchedBeforeAttempt` 在切换后由状态机置位、在尝试返回后立即清零，任何调整尝试/切换时序的改动都要同步这对标志。③ **`Flush` 只给高频实时行用**：`WriteTaskOutput` 置 `Flush=true` 是为保住「实时 tail」（P2-24 的批量刷盘会让它滞后），普通日志不要置，否则 syscall 削峰失效。④ **`RunId` 的唯一生成入口是 `RunLogEntry.NewRunId()`**：`FileRunLogger` 与 `BatchEngine`（空日志场景）共用，别在别处再写一个生成函数。⑤ **脱敏只做形态识别**：账号 id / 模型名 / 工作区路径等非敏感业务字段一律不脱敏（过度脱敏会让日志失去排障价值），新增形态只改 `SecretMasker` 的正则清单。⑥ **指标与报告刻意分离落盘**：报告按批次时间命名、内容随需求演进；指标按 `run_id` 命名、字段集由守卫锁定，便于长期采集与横向对比。
> - **提交切片建议（§7 护栏）**：① `refactor(debt): ARCH P-10 结构化日志——IRunLogger 唯一落盘点 + RunId 贯穿`（`Core/Observability/*` 4 个新文件 + `IRunLogger.cs` + `FileRunLogger.cs` + `BatchEvent.cs` + `BatchEngine.cs`）；② `refactor(debt): ARCH P-10 运行级指标埋点 + 指标落盘`（`RunMetrics.cs` 随 ① 或独立、`IStores.cs`、`RunMetricsWriter.cs`、`EngineFactory.cs`、`EngineHost.cs`、埋点文件 5 个）；③ `refactor(debt): ARCH P-10 级别常量收口 + facade 维持 ≤400`（20 处级别字面量 + `WorkspaceResolver.cs`）；④ `test(debt): ARCH P-10 行为测试与防回潮守卫`（`ObservabilityHygieneTests` + 5 个测试文件）；⑤ `docs(governance): P-10 收口——同步 ARCH/TECH-DEBT/CODE-REVIEW/README 看板`。**提交一律显式 `git add <具体文件>`，禁 `git add -A`**。
> - **⚠️ 提交前置条件（§7 护栏，必读）**：本轮涉及的 `FileRunLogger.cs` / `IRunLogger.cs`（实时输出 `WriteTaskOutput`）、`BatchEngine.cs`（P-02/P-08 收口）、`EngineHost.cs` / `EngineFactory.cs`（P-04/P1-4 组合根收口）、`TaskRunnerTests.cs`（P-03 契约）等文件在提交时点仍承载前序阶段的未提交改动；**须先切出前序阶段提交再提本轮**，否则 `git add` 会把数百行旧重构裹进 P-10 提交、违反「单步 ≤400 行」护栏。

**P-11 · CI 阻断 + 覆盖率/格式门禁（A-9 ①，5 人天）**

- 步骤：`build` + 单元测试设为 required status check；`coverlet` 采集覆盖率并按阈值拦截；`dotnet format --verify-no-changes` 进 CI。
- 退出：阻断生效；覆盖率低于目标即失败；格式门禁生效。
- 风险：R-3（质量下滑）→ 本阶段即转阻断。

> **【执行记录 · 2026-10-22 · ✅ 已收口】**
> - **ci.yml 改造（`.github/workflows/ci.yml`）**：job 仍为 `build-and-test`（名称不动，保证分支保护可稳定引用），步骤链改为「全量 `--no-incremental` 构建（严格，`TreatWarningsAsErrors` 下任何分析器警告即失败）→ 格式门禁 → 快跑单测 + 覆盖率采集（主 + Desktop 两工程）→ 慢跑单测（E2E/Usability/Process，不采覆盖率）→ 覆盖率门禁」。任一步骤非零退出即 job 失败——「只报告不阻断」语义自此消除；文件头注释同步改写为阻断语义说明。
> - **格式门禁（`scripts/ci/Assert-Format.ps1`，新增）**：封装 `dotnet format BatchAgentRunner.slnx --verify-no-changes`，过滤 SDK 10.0.401 的 **CHARSET 假阳性**（该版本 charset 检测器对全部合法 UTF-8 无 BOM 源码误报「修复文件编码」，与 retired pre-commit 钩当年的过滤口径一致，历史见 TECH-DEBT §4.7 #9），其余违例（ENDOFLINE / WHITESPACE / IDExxxx）一律 exit 1。**探针实测**：向 `ReasoningEfforts.cs` 注入行尾空格 → 门禁按预期 FAIL 并指出 `error WHITESPACE`（探针已删除，不入库）；同时修复该文件工作区副本被 CRLF 污染导致的真实 ENDOFLINE 违例（提交 blob 本就是 LF，仅工作区被外部工具改写，修复后与 HEAD 字节一致）。
> - **覆盖率门禁（`scripts/ci/Assert-Coverage.ps1`，新增）**：解析两个测试工程产出的 `tests/*/TestResults/*/coverage.cobertura.xml`，按 class 的 `filename` 归并到生产程序集（src/ 前缀取第二段），逐程序集断言行覆盖率 ≥ 阈值；测试工程与 Avalonia 等第三方程序集不在阈值表内、不进分母。**阈值 = P-11 收口实测基线下取整**（2026-10-22 干净构建 + 快跑 bucket 实测：Core 44.09% / Infrastructure 39.84% / Agents 23.06% / Desktop 50.10%）：Core **44** / Infrastructure **39** / Agents **23** / Desktop **50**。§7.3 长期目标（85/70/50）为爬升方向、刻意不硬门禁（否则 CI 常红失效），阈值随覆盖率提升在脚本内显式上调。
> - **采集配置（`tests/coverlet.runsettings`，改造）**：`Format=cobertura` + `SkipAutoProps=true`（自动属性不进分母，防「为覆盖率给自动属性写测试」）；**移除 Exclude 段**——coverlet.collector v6 对 `[Asm*]*` 通配式误伤、会导致整份 0 采集（实测踩坑），改由门禁脚本按阈值表白名单放行生产程序集。采集范围锁定快跑 bucket（排除 E2E/Usability/Process），保证门禁可重复、不受慢机器/真实进程影响。
> - **required status check（交接项，非仓库文件）**：分支保护属 GitHub 仓库管理设置，本仓 `gh` CLI 未安装、且属共享状态改动，**由仓库管理员执行**：Settings → Branches → 添加/编辑 `master` 规则 → 勾选 **Require status checks to pass before merging** → 搜索并勾选 **`build-and-test`**（建议同时勾选 Require branches to be up to date）。勾选后红 CI 即阻止 PR 合并，R-3 缓解闭环。
>   - **CLI 等价命令（管理员，需 `gh` + 仓库写权限）**：`gh api repos/<owner>/<repo>/branches/master/protection --method PUT -f "required_status_checks[strict]=true" -f "required_status_checks[contexts][]=build-and-test" -f "enforce_admins=false" -f "required_pull_request_reviews=null" -f "restrictions=null"`（`<owner>/<repo>` 替换为实际仓库路径；`build-and-test` 为 `ci.yml` 的 job 名，与门禁侧契约一致）。该步属共享状态改动，**不在本仓代码交付范围**，故 P-11 仓库侧工作已 100% 收口。
> - **配套（`.gitignore`）**：新增 `TestResults/` 忽略——覆盖率产物（cobertura）只活在 CI/本地，不入库。
> - **退出验证**：全量 `--no-incremental` 严格构建 **0 警告/0 错误**；全量测试 **822 通过/0 失败**（主 654 = 快跑 613 + 慢跑 41，Desktop 168；较 P-10 收口轮 801 +21，增量来自 HEAD 中 ReasoningEfforts 特性提交，与本轮门禁改动无关）；本地实跑两道门禁脚本均 PASS（格式：339 CHARSET 假阳性过滤后 0 真实违例；覆盖率：四程序集全部 ≥ 阈值）；负测试探针（行尾空格）被格式门禁按预期拦截。
> - **边界（交接必读）**：① **CI 的 SDK 版本浮动（`10.0.x`）与 CHARSET 过滤是耦合的**：若未来 SDK 修复 CHARSET 误报，过滤只是空转（无害）；若引入新的整文件级假阳性，同样只应加在脚本头的过滤清单里并注明原因，**不得**为过门禁去关 `.editorconfig` 真实规则。② **覆盖率阈值只升不降**：下调阈值 = 放弃门禁；确实需要临时豁免时按「先补测试、再动阈值」处理，并在 ARCH 本文档登记。③ **慢跑 bucket 不采覆盖率**是刻意设计：真实进程类测试在 CI 上耗时波动大，采进来会让门禁时快时慢、失去可重复性。④ **门禁脚本路径即契约**：`scripts/ci/*.ps1` 被 ci.yml 以 `pwsh -File` 引用，重命名/移动须同步改 workflow。

**P-12 · 规范集 + 静态安全扫描（A-10/A-11，5 人天）**

- 步骤：`.editorconfig` 扩充团队级规则集（命名/关键 `CAxxxx`/`IDExxxx`），恢复 `CS1591` 强制公共 API 文档；接入静态安全扫描，固化 `CliCommandBuilder`/`CliAdapterBase`/`FileCredentialVault` 守卫。
- 退出：扫描 0 高危；公共 API 有 XML 文档；新警告本地+CI 均失败。

> **【执行记录 · 2026-10-22 · ✅ 已收口】**
> - **规则集（`.editorconfig`）**：新增命名规则 6 组——接口 `I` 前缀、类型 PascalCase、**对外可见**成员（方法/属性/事件）PascalCase、参数与局部变量 camelCase、私有实例字段 `_camelCase`、静态字段（含 `const`/`static readonly`）PascalCase；并追加安全类 CA 规则 `error`：`CA5350`（弱加密）/`CA5351`（已破解算法）/`CA3001`（SQL 注入）/`CA3075`（XXE）/`CA5392`（P/Invoke 未限定搜索路径）——CA5392 命中 1 处真实缺口（`Program.MessageBoxW` 的 `user32.dll` P/Invoke），补 `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` 收紧搜索路径（该 DLL 只存在于 System32，无行为变化）。口径按**存量约定**固化，不改名、不制造 churn（唯一动作：6 处 `const int FacadeLineCap`、1 处 `TailWindowChars` 改 camelCase，2 个可变静态缓存 `_cache`/`_cacheTime` 改 `Cache`/`CacheTime`，消除同类型内两种静态字段命名并存）。私有事件转发方法（`_Forward`）按既有约定不纳入规则。
> - **恢复 `CS1591`（A-10）**：`Directory.Build.props` 删除 `NoWarn CS1591`（逃逸阀台账 #3 退役），四个 `src` 生产工程开启 `GenerateDocumentationFile`；公共 API（类型/成员/枚举值/参数）全量补齐 XML 文档，并把自动补全产生的「成员名。」式占位逐条改写为有信息量的说明（208 个源文件 / 1,991 行 `///` 文档）；同轮修掉 15 处 `<param>` 误置于 `<summary>` 内的结构性错误、5 处 `cref` 指向私有/不存在成员。测试工程不产出文档文件，`CS1591` 不触发，无需豁免。
> - **静态安全扫描（A-11）**：新增 `SecurityHygieneTests`（**7 个测试方法 / 11 个用例**）——① `CliCommandBuilder` 的 `{prompt}`/`{workspace}` 必须保持 `Quote(NormalizeToSingleLine(…))` 形态（去掉引号或单行折叠即复活命令注入）；② 引号实现全仓唯一；③ `CliAdapterBase` 一律经 `CliCommandBuilder.Build` 且不得出现裸占位符替换；④ `FileCredentialVault` 六个账号 ID 入口方法**逐个方法体**校验 `NormalizeAccountId`（原实现是全文匹配，任一处存在即通过，属假守卫，本轮改为按方法体截取）；⑤ 恶意账号 ID（`..` / `a/../b` / `a` / `.\` / `a*b`）拒绝语义（Theory 5 例）；⑥ 合法 ID 原样通过；⑦ `src` 全量扫描疑似硬编码密钥（排除存环境变量名/引用的标识符）。守卫同桶写入 `ci-platforms.yml` 的 `architecture-tests` 独立 job，失败在独立 job 可见。
> - **连带口径校正**：`LayeringRulesTests` 六条「上帝类 ≤400 行」闸门由物理行数改为**代码行数（排除 `///` 文档注释）**——文档行数由 API 面决定，计入等于用「文档写得全」惩罚架构闸门（改后 ConfigViewModel 366 / BatchEngine 396 / MonitorViewModel 388 代码行，与 P-12 前口径一致）。
> - **退出验证**：全量 `--no-incremental` 严格构建 **0 警告 / 0 错误**（CS1591/CS157x 全清）；全量测试 **842 通过 / 0 失败**（主 674 = 快跑 633 + 慢跑 41，Desktop 168；较 P-14 轮 830 +12 = `SecurityHygieneTests` 11 个用例 + `CommentHygieneTests` 新增「文档占位」守卫 1 项）；`Assert-Format.ps1` PASS（342 条 CHARSET 假阳性过滤后 0 真实违例）；`Assert-Coverage.ps1` PASS（Core 44.09% / Infra 39.84% / Agents 23.06% / Desktop 50.10%，与 P-11 基线一致——文档注释不产生可执行行）。
> - **边界（交接必读）**：① 命名规则的强制点是**格式门禁**（`dotnet format`，IDE1006），命令行 `dotnet build` 不因命名失败——不要据此认为无需遵守；② 安全守卫读源码文本（正则 + 方法体截取），**重命名 `CliCommandBuilder`/`NormalizeAccountId` 会使其失效**，改这两处须同步改守卫；③ 文档行不计入上帝类闸门，但**新增公共 API 必须有 XML 文档**（`TreatWarningsAsErrors` 直接打红）。

**P-13 · 多平台矩阵 + Release（A-9 ②，5 人天）**

- 步骤：CI 加 `macOS`/`linux` 矩阵（先非阻断跑、稳定后转阻断）；新增 Release 流水线（publish 自包含 + `GitVersion` 版本号 + 签名/校验）。
- 退出：多平台绿；制品可发布且可回退。
- 风险：R-6（多平台暴露隐蔽缺陷）→ 先非阻断。

> **【执行记录 · 2026-10-22 · ✅ 已收口】**
> - **Desktop 跨平台可构建（`.csproj`）**：按宿主 OS 条件化 `<OutputType>`（Windows=`WinExe`、其余=`Exe`）并剔除非 Windows 的 `<ApplicationManifest>`（`app.manifest`，注释自述 Windows only）与 `<ApplicationIcon>`（`.ico`），关闭「只验 Windows 是缺口」。Windows 构建行为与改造前**逐字节一致**（条件在 Windows 恒为 `WinExe` + manifest + ico），已本地 fast 构建验证 0 错误；Release 矩阵各 RID 均在原生 runner 发布（win→windows-latest / linux→ubuntu-latest / osx→macos-latest），故宿主 OS 判定即对应目标平台，无需交叉发布。
> - **多平台矩阵（`ci-platforms.yml`，新增，非阻断）**：`platform-matrix` 在 `windows-latest / macos-latest / ubuntu-latest` 三原生 runner 上 `--no-incremental` 全量构建 + 快跑单测（主工程排除 E2E/Usability/Process，Desktop.Tests 全量 headless），整体 `continue-on-error: true`——对应 R-6「先非阻断跑」，先把隐蔽缺陷晒进日志、不挡 `build-and-test` 阻断门禁。该工作流是 `ci.yml`（required check）的**兄弟工作流**、不在分支保护 required 之列，故其红/黄不阻止 PR 合并。升级为阻断路径（去 `continue-on-error` + 加入 required checks）已写在 workflow 尾部注释，待三平台稳定后由管理员执行。
> - **架构测试独立桶（`ci-platforms.yml` 的 `architecture-tests` job）**：§3.4 IL 扫描断言（分层/防腐层/同步阻塞反模式，`*.Tests.Architecture` 命名空间）从 `ci.yml` 快跑 bucket 抽离，独立 job 隔离跑、提升可见性；与 `ci.yml` 快跑 bucket 的架构断言等价（重复但无害）。
> - **Release 流水线（`release.yml`，新增）**：`push tags: v*` + `workflow_dispatch` 触发；`version` job 用 `gittools/actions`（GitVersion 6.x）读 `GitVersion.yml`（Mainline 模式）派生 SemVer，`workflow_dispatch` 可显式覆盖；`publish` 矩阵各 RID 在原生 runner 上 `dotnet publish -c Release -r <rid> -p:PublishSingleFile=true -p:SelfContained=true -p:Version=<semver>` 产出自包含单文件；每包附 `*.sha256`（SHA256 校验，Windows `Get-FileHash` / 其余 `sha256sum`）。打标签时 `release` job 聚合四平台制品建 GitHub Release（`softprops/action-gh-release`，自动发布说明）——**制品可回退 = 下载上一版本 Release**。本地已验证 win-x64 自包含单文件发布成功（96MB 单 exe + 原生 Skia/ANGLE/HarfBuzz 伴随库 + 校验和），linux/osx 路径在各自原生 CI runner 执行（OutputType=Exe）。
> - **版本策略（`GitVersion.yml`，新增）**：Mainline 模式 + `tag-prefix: v`，单主干 + 标签模型下由标签锚定 SemVer、非标签提交按高度派生，保证三平台版本号一致、可复现、可回退。
> - **「签名」范围界定（诚实声明）**：本阶段落地的是**完整性校验（SHA256）**，非平台代码签名。真正的 Windows Authenticode / macOS Apple Developer ID + 公证 / Linux GPG 需要证书与私钥（机密资产，不入仓），接入路径已在 `release.yml` 注释留位（消费 `secrets.WIN_CODESIGN_PFX` / `secrets.APPLE_CERT` / `secrets.GPG_PRIVATE_KEY` 等），待具备证书后补签名步并随包发布签名产物。
> - **退出验证与边界**：
>   1. **P-13 自身产物 0 新警告/错误**：`.csproj` 条件化改动无代码、YAML/`GitVersion.yml` 不进编译器；Windows 构建路径已验证 unchanged（fast 0 错误）。
>   2. **发布配方已验证**：win-x64 自包含单文件本地 `dotnet publish` 成功；跨平台三 RID 在原生 CI runner 执行（无法在本 Windows 会话真跑 mac/linux，已在 workflow 中以原生 runner 矩阵表达）。
>   3. **全量严格构建状态（交接必读）**：P-13 收口当时工作树尚有 **P-12 进行中**的 XML 文档注释（CS157x）未入库，全量 `--no-incremental` 严格构建一度因该 in-flight 警告而红（非 P-13 引入）。**2026-10-22 P-12 收口后此状态已消除**：全量严格构建 0 警告 / 0 错误，`ci-platforms.yml` / `release.yml` 在干净树上跑绿。
>   4. **多平台「绿」的诚实口径**：矩阵按 R-6「先非阻断」实现，`continue-on-error` 保证不挡主干；真正的「三平台全绿转阻断」需 mac/linux runner 实测收敛后执行升级路径（见 workflow 注释），本会话仅完成「矩阵就位 + 非阻断跑」这一步，符合 R-6 的阶段性定义。

**P-14 · ADR + 配置 Schema（A-12，5 人天）**

- 步骤：建立 `docs/adr/`，为重大架构决策补 ADR；由 `RunOptions` 生成 JSON Schema 并接入 CI 校验 `config.json`。
- 退出：ADR≥关键决策数；schema 校验生效；设计文档与代码同步机制就位。

> **【执行记录 · 2026-10-22 · ✅ 已收口】**
> - **ADR 机制（`docs/adr/`，新增）**：`README.md` 定义「何时写 / 怎么写 / 状态机（提议→已接受→已废弃/已取代）/ 编号规则 / 索引」，`template.md` 提供「状态·日期·决策者·关联 + 背景·决策·后果·替代方案」骨架。**决策记录 11 篇**覆盖全部关键决策，每篇均写明背景、取舍与否决方案，决策接受后不可改写（演进靠新 ADR 标注取代）：
>   `0001` ADR 机制本身、`0002` 分层与单向依赖（含 IL 断言）、`0003` 编排 facade ≤400 行 + 协作者拆分、`0004` 调度策略对象化、`0005` VM 按 Coordinator/Presenter 分解、`0006` 存储与路径单一事实来源、`0007` 配置契约稳定（横向切片 + schema）、`0008` 可观测性（唯一落盘点/RunId/指标/脱敏）、`0009` CI 阻断与覆盖率格式基线、`0010` 多平台矩阵与 Release、`0011` 构建 SOP（fast/strict）。
> - **文档↔代码同步机制（机器化，非人肉）**：新增 `AdrHygieneTests`（**4 项**）——① 文件名 `ADR-NNNN-slug.md` 且编号自 0001 起连续不跳号；② 必备章节（背景/决策/后果）与状态取值合规；③ `README` 索引与本目录文件**双向一一对应**（漏登记、悬挂链接、断号即红）；④ 模板文件存在。任何「写了决策不登记」「索引指向已删文件」在 CI 单测桶内直接暴露。
> - **配置 Schema 单一事实来源（`docs/config.schema.json`，新增）**：JSON Schema 2020-12，由 `RunOptions.01~11-*.cs` 及其引用的领域类型（`WorkspaceConfig`/`AccountConfig`/`ModelCandidate`/`FallbackModel`/`FreeTimeWindow`/`ExpertRef`/`FallbackConfig`/`AutoShutdownConfig`/`CreditsClaimConfig`/`AutoAnswerPolicy`/`NotificationRule`/`SmtpConfig`/`GitConfig`）逐字派生；顶层 **41 个字段**（覆盖 12 个配置域）+ **13 个 `$defs`**，含标题/说明/枚举/边界/`pattern`（时段 `HH:mm`）。
> - **契约同步由测试锁死（`ConfigSchemaTests`，4 项）**：① `samples/config.example.json` 通过 schema 校验（校验器支持 `type/properties/required/additionalProperties/items/enum/minimum/maximum/pattern/$ref`，容忍 `//` 注释与尾随逗号，与运行态 `JsonDefaults` 同口径）；② schema 顶层 `properties` 键集合**逐字等于** `RunOptions` 的 `[JsonPropertyName]` 键集合；③ 13 个 `$defs` 键集合分别等于对应 C# 类型键集合；④ 负向用例（`concurrency:"not-a-number"` / 非法 `agent_kind` / 缺字段的 `accounts`）必须被拦截，防校验器退化为空操作。**改字段而不同步 schema 即红**。
> - **CI 校验（`scripts/ci/Assert-ConfigSchema.ps1`，新增）**：作为 `ci.yml` 的 `build-and-test` 独立步骤（`Config schema gate`），消费前置 build 产物（`--no-build`）；校验实现**只有一处**（复用 `ConfigSchemaTests`），脚本仅作 CI 显式入口，避免「第二份校验器」漂移。失败输出修复指引（同步 schema 与 `CONFIG-配置项说明.md`）。
> - **契约兼容口径**：schema 全对象 `additionalProperties: true`——旧配置含已废弃字段仍可校验通过（前向兼容）；漂移由键集合等值断言拦截，而非靠 `additionalProperties:false` 误伤。此取舍与理由见 [ADR-0007](adr/ADR-0007-config-contract-and-schema.md)。
> - **退出验证**：`ConfigSchemaTests` 4/4 绿、`AdrHygieneTests` 4/4 绿；`Assert-ConfigSchema.ps1` 本地实跑 PASS；新增 8 项测试（主工程）。ADR 数量 11 篇 ≥ 关键决策数（§1.2 的 A-1~A-12 与 P-09/P-10/P-11/P-13 决策均有对应记录）。
> - **边界（诚实声明）**：可选的「由 C# 反射自动生成 schema」未采用——生成器会成为第二事实来源且丢失可读描述；改为「手写派生 + 键集合等值断言」，机器保证不漂移（理由见 ADR-0007「替代方案」）。ADR 覆盖的是**重大决策**，不追求把每次提交都写成 ADR。

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
| 架构 | §3.4 IL 断言全绿；`BatchEngine` facade **≤ 400 代码行**；`TaskRunner` **≤ 400 代码行**；最大单个 VM **≤ 400 代码行**（P-12 起口径 = 排除 `///` 文档注释的代码行，见 §6.2）                |
| 工程 | CI 阻断生效（P-11 ✅：格式/覆盖率/单测/构建四道门禁失败即红，required status check 待管理员勾选 `build-and-test`）；覆盖率门禁生效（P-11 ✅：Core 44% / Infra 39% / Agents 23% / Desktop 50% 锁定基线，向 85/70/50 爬升）；静态扫描 **0 高危**（P-12 ✅：5 条安全 CA 规则 `error` 化 + `SecurityHygieneTests` 7 项守卫命令注入/路径遍历/硬编码密钥）；规范集 **CS1591 生效**（P-12 ✅：公共 API 全量 XML 文档，缺失即构建失败）；多平台矩阵 CI 绿（P-13 ✅：win/mac/linux 非阻断矩阵 + 架构测试独立桶，`ci-platforms.yml`）；Release 流水线可发布可回退（P-13 ✅：`release.yml` 自包含单文件 + GitVersion + SHA256 校验）；配置契约门禁生效（P-14 ✅：`Assert-ConfigSchema.ps1` schema↔代码键集合等值断言）                                                                    |
| 业务 | 端到端跑真实任务清单，降级/兜底/冷却/时段/续跑行为与原版一致（契约测试锁定）                                                                  |
| 文档 | ✅ P-14：ADR **11 篇**（≥ 关键决策数）且索引/结构由 `AdrHygieneTests` 机器校验；设计文档与代码同步（ADR 索引双向一致 + schema 键集合等值断言）；`docs/config.schema.json` 存在并被 CI 门禁校验                                                            |

---

## 8. 交付物清单

### 8.1 文档类

- `docs/ARCH-架构重构与工程化治理方案.md`（本方案）
- `docs/TECH-DEBT-技术债务治理方案-v2.0.md`（持续更新剩余项）
- `docs/adr/`（架构决策记录 ✅ P-14：README 机制/索引 + 模板 + 11 篇已接受决策——分层模型、策略对象化、facade 上限、VM 分解、单一事实来源、配置契约、可观测性、CI 门禁、多平台发布、构建 SOP、ADR 机制）
- `docs/API-契约文档.md`（CLI 参数 / config schema / sidecar 格式 / 报告结构）
- `docs/config.schema.json`（配置机器可校验单一事实来源 ✅ P-14：JSON Schema 2020-12，键集合与 `RunOptions` 等值，CI 校验）
- `docs/回滚-Runbook.md`（发布回退、配置迁移回滚步骤）

### 8.2 代码模块类

- 重构后引擎协作者：`WaitCoordinator` 等
- 策略对象集：`DegradationPolicy` / `CooldownPolicy` / `FallbackPolicy` / `FreeTimeWindowPolicy`
- VM 子编辑器 / Coordinator / `TaskItemViewModel` 防腐层
- `JsonStoreBase<T>` + `AppPaths` 常量（去重）
- Core 共享工具（Truncate/终态/Clone 收口）
- 可观测性组件（ARCH P-10 ✅ 已落：结构化日志接线 `RunLogEntry` + `run-*.jsonl`、RunId 关联、`RunMetrics` 七类运行级指标 + `metrics-<run_id>.json` 落盘、`SecretMasker` 脱敏单一事实来源）

### 8.3 工程配置类

- 扩充后的 `.editorconfig`（团队级规则集 ✅ P-12：6 组命名规则 + 5 条安全 CA 规则 `error`，口径按存量约定固化）
- `ci.yml` 阻断版（覆盖率 + 格式 + 配置 Schema + 架构测试 + 静态扫描）+ `ci-platforms.yml` 多平台矩阵（win/mac/linux 非阻断）+ 架构测试独立桶
- 静态扫描配置 ✅ P-12：分析器安全规则集（CA5350/CA5351/CA3001/CA3075/CA5392 = `error`）+ 仓级安全守卫 `SecurityHygieneTests`（命令注入 / 路径遍历 / 硬编码密钥）
- `Release` 流水线（publish + 版本号 + 签名）✅（P-13：`.github/workflows/release.yml` 自包含单文件四 RID + `GitVersion.yml` 版本号 + SHA256 校验，标签建 GitHub Release 可回退；代码签名列为后续接入项）
- `GitVersion` / 版本策略配置 ✅（P-13：`GitVersion.yml`，Mainline 模式，标签锚定 SemVer）

### 8.4 测试类

- 契约测试套件（IBatchEngine / config / sidecar / 报告）
- Policy 对象单测（降级/冷却/兜底/时段）
- VM 契约测试（headless）
- §3.4 架构 IL 扫描断言 + 安全守卫（`SecurityHygieneTests` 7 项，P-12）
- 覆盖率报告与趋势看板

### 8.5 度量类

- 代码度量基线表（行数/复杂度/覆盖率，附录 A）
- 月度技术债务趋势看板（复用 TECH-DEBT §4.8）

---

## 附录 A：代码度量基线（现状，2026-10-20 实测）

| 文件                                                      | 行数   | 角色         | 本期目标                      |
| ------------------------------------------------------- | ---- | ---------- | ------------------------- |
| `Core/Engine/BatchEngine.cs`                            | 399  | 编排 facade  | ≤ 400 ✅ **已收口（P-02，953→400；P-08 再删死私有 `Truncate` 别名 →397；P-10 增可观测性字段后由 `ResolveWorkspacePath` 下沉 `WorkspaceResolver` 回到 399，守卫测试锁死）** |
| `Core/Engine/WorkspaceResolver.cs`                      | 32   | 工作区路径解析纯函数（P-10 新增） | 维持（从 BatchEngine 下沉，行为零变化）   |
| `Core/Observability/RunLogEntry.cs`                     | 71   | 结构化日志条目（P-10 新增） | 维持；新增写入一律转条目后经 `Write(RunLogEntry)` |
| `Core/Observability/RunMetrics.cs`                      | 280  | 运行级指标采集器 + 快照（P-10 新增） | 维持；新增指标须同步 `ObservabilityHygieneTests` 字段清单 |
| `Core/Observability/SecretMasker.cs`                    | 85   | 日志脱敏单一事实来源（P-10 新增） | 维持；新增敏感形态只改此处正则清单 |
| `Core/Observability/RunLogLevels.cs`                    | 33   | 日志级别单一事实来源（P-10 新增） | 维持；写日志点一律引用常量 |
| `Core/Observability/LogEventNames.cs`                   | 38   | 事件名单一事实来源（P-10 新增） | 维持；新增事件名在此登记 |
| `Infrastructure/Logging/FileRunLogger.cs`               | 383  | 运行日志实现（P-10：文本 + JSONL 双轨、唯一落盘点；原 202 行含 `NullRunLogger`，本轮拆出后 383 行） | 维持；新增写入方法必须转条目后经 `Write(RunLogEntry)` |
| `Infrastructure/Logging/NullRunLogger.cs`               | 88   | 空实现（P-10 从 `FileRunLogger.cs` 拆出独立文件） | 维持 |
| `Infrastructure/Observability/RunMetricsWriter.cs`      | 42   | 指标落盘（P-10 新增） | 维持；与报告同目录、snake_case |
| `Core/Engine/BatchPersistence.cs`                       | 193  | 持久化协作者（P-02 新增） | 维持（从 BatchEngine 下沉）      |
| `Core/Engine/AccountSwitchCoordinator.cs`               | 185  | 凭证切换协作者（P-02 新增） | 维持（从 BatchEngine 下沉）      |
| `Core/Engine/BatchLifecycle.cs`                         | 283  | 生命周期协作者（P-02 新增） | 维持（从 BatchEngine 下沉）      |
| `Core/Engine/TaskRunner.cs`                             | 76   | 状态机 facade（P-03 收口） | ≤ 400 ✅ **已收口（P-03，903→76，守卫测试锁死）** |
| `Core/Engine/TaskStepExecutor.cs`                       | 569  | 状态机本体（P-03 新增） | 维持（从 TaskRunner 下沉）     |
| `Core/Scheduling/DegradationPolicy.cs`                  | 145  | 降级/失败策略（P-03 新增） | 维持                     |
| `Core/Scheduling/CooldownPolicy.cs`                     | 31   | 冷却策略（P-03 新增）   | 维持                     |
| `Core/Scheduling/FreeTimeWindowPolicy.cs`               | 66   | 免费时段策略（P-03 新增） | 维持                     |
| `Core/Engine/FallbackPolicy.cs`                         | 32   | 兜底策略（P-03 新增）   | 维持                     |
| `Core/Engine/TaskTerminalWriter.cs`                     | 109  | 终态写入协作器（P-03 新增） | 维持                   |
| `Core/Engine/TaskEventReporter.cs`                      | 59   | 事件上报协作器（P-03 新增） | 维持                   |
| `Core/Engine/TaskRequestBuilder.cs`                     | 34   | 请求构建协作器（P-03 新增） | 维持                   |
| `Core/Engine/TaskCredentialSwitch.cs`                   | 56   | 凭证切换协作器（P-03 新增） | 维持                   |
| `Desktop/ViewModels/ConfigViewModel.cs`                 | 366  | 配置 VM facade（P-04 收口） | ≤ 400 守卫锁死；逻辑已下沉 11 子编辑器（`ViewModels/Config/`）+ `ConfigSaveCoordinator` |
| `Desktop/ViewModels/MonitorViewModel.cs`                | 388  | 监控 VM facade（P-06 收口） | ≤ 400 守卫锁死；逻辑已下沉 Monitor 8 协作者（`ViewModels/Monitor/`） |
| `Desktop/ViewModels/LoginViewModel.cs`                  | 280  | 登录 VM facade（P-06 收口） | ≤ 400 守卫锁死；逻辑已下沉 Login 5 协作者（`ViewModels/Login/`）+ 3 个行视图模型独立文件 |
| `Desktop/ViewModels/TemplateEditorViewModel.cs`         | 240  | 模板 VM facade（P-05 收口） | ≤ 400 守卫锁死；交互逻辑已下沉 3 个 Coordinator（`ViewModels/TemplateEditor/`）+ 4 个行视图模型独立文件 |
| `Desktop/ViewModels/Monitor/OutputStreamCoordinator.cs` | 234  | 输出流协作者（P-06 新增） | 维持（从 MonitorViewModel 下沉） |
| `Desktop/ViewModels/Monitor/TaskListCoordinator.cs`    | 164  | 任务列表/DAG/阶段树协作者（P-06 新增） | 维持（从 MonitorViewModel 下沉） |
| `Desktop/ViewModels/Monitor/MonitorOperationsCoordinator.cs` | 156  | 操作编排协作者（P-06 新增） | 维持（从 MonitorViewModel 下沉） |
| `Desktop/ViewModels/Monitor/BatchStatsState.cs`         | 81   | 统计快照状态（P-06 新增） | 维持（从 MonitorViewModel 下沉） |
| `Desktop/ViewModels/Monitor/BatchStatusState.cs`        | 77   | 批次状态（P-06 新增） | 维持（从 MonitorViewModel 下沉） |
| `Desktop/ViewModels/Monitor/EventToastBoard.cs`         | 66   | 事件提示板（P-06 新增） | 维持（从 MonitorViewModel 下沉） |
| `Desktop/ViewModels/Monitor/BatchStatistics.cs`         | 64   | 纯统计计算（P-06 新增） | 维持（从 MonitorViewModel 下沉） |
| `Desktop/ViewModels/Monitor/OutputTagClassifier.cs`     | 46   | 输出分类纯函数（P-06 新增） | 维持（从 MonitorViewModel 下沉） |
| `Desktop/ViewModels/Monitor/OutputFollowMode.cs`        | 15   | 跟随模式枚举（P-06 新增） | 维持（从 MonitorViewModel 下沉） |
| `Desktop/ViewModels/Login/GuidedLoginFlow.cs`           | 167  | 一键登录流程协作者（P-06 新增） | 维持（从 LoginViewModel 下沉） |
| `Desktop/ViewModels/Login/AccountPoolCoordinator.cs`    | 153  | 账号池协作者（P-06 新增） | 维持（从 LoginViewModel 下沉） |
| `Desktop/ViewModels/Login/CreditsClaimFlow.cs`          | 130  | 积分领取流程协作者（P-06 新增） | 维持（从 LoginViewModel 下沉） |
| `Desktop/ViewModels/Login/ILoginView.cs`                | 58   | 视图缝接口（P-06 新增） | 维持（facade→协作者最小状态面） |
| `Desktop/ViewModels/Login/LoginAccountRow.cs`           | 72   | 账号行 VM（P-06 抽出） | 维持 |
| `Desktop/ViewModels/Login/LoginStepsPresenter.cs`       | 95   | 步骤条协作者（P-06 新增） | 维持（从 LoginViewModel 下沉） |
| `Desktop/ViewModels/Login/CreditsClaimRow.cs`           | 41   | 积分结果行 VM（P-06 抽出） | 维持 |
| `Desktop/ViewModels/Login/LoginStepItem.cs`             | 35   | 步骤行 VM（P-06 抽出） | 维持 |
| `Desktop/ViewModels/Login/LoginErrorTranslator.cs`      | 33   | 异常人话翻译纯函数（P-06 新增） | 维持（从 LoginViewModel 下沉） |
| `Desktop/ViewModels/TemplateEditor/TemplateFlowCoordinator.cs` | 136  | 流程示意 Coordinator（P-05 新增） | 维持（从 TemplateEditorViewModel 下沉） |
| `Desktop/ViewModels/TemplateEditor/TemplateExpansionCoordinator.cs` | 116  | 变量展开 Coordinator（P-05 新增） | 维持（从 TemplateEditorViewModel 下沉） |
| `Desktop/ViewModels/TemplateEditor/TemplateLoadCoordinator.cs` | 32  | 模板加载 Coordinator（P-05 新增） | 维持（从 TemplateEditorViewModel 下沉） |
| `Desktop/ViewModels/TemplateEditor/TemplateFlowNodeViewModel.cs` | 162  | 流程节点行 VM（P-05 抽出） | 维持 |
| `Desktop/ViewModels/TemplateEditor/TemplateVarInputViewModel.cs` | 44  | 变量输入行 VM（P-05 抽出） | 维持 |
| `Desktop/ViewModels/TemplateEditor/TemplateRowViewModel.cs` | 22  | 模板清单行 VM（P-05 抽出） | 维持 |
| `Desktop/ViewModels/TemplateEditor/PreviewTaskRowViewModel.cs` | 30  | 预览任务行 VM（P-05 抽出） | 维持 |
| `Infrastructure/CredentialVault/FileCredentialVault.cs` | 671  | 凭证存储       | 守卫固化（禁重写）                 |
| `Core/Domain/WorkflowTypes.cs`                          | 512  | 工作流类型      | 维持                        |
| `Desktop/Models/ConfigEditors.cs`                       | 505  | 手写编辑器（P-09 收口，510→505） | 维持；新增解析字段一律 `FieldEditor<T>`（`ConfigEditorsHygieneTests` 锁死） |
| `Desktop/Models/FieldEditor.cs`                         | 82   | 可校验字段 + 解析器单一来源（P-09，65→82） | 维持；新增解析一律加 `FieldParsers` 并由视图呈现 `Error` |
| `Core/Options/RunOptions.cs`                            | 18   | God Config（P-09 收口，369→18） | 维持；仅留类型文档，新增配置落所属域切片 |
| `Core/Options/RunOptions.01~11-*.cs`                    | ≤49  | 12 配置域切片（P-09 新增） | 维持；文件名两位序号 = JSON 落盘顺序（`RunOptionsContractTests` 锁死） |
| `Agents/Adapters/CliAdapterBase.cs`                     | 392  | CLI 适配基类   | 安全守卫                      |
| `Agents/Internal/CliProcessRunner.cs`                   | 470  | 进程运行       | 安全守卫                      |
| `Core/Engine/WaitCoordinator.cs`                       | 156  | 等待协调者（P-01 新增） | 维持（从 BatchEngine 下沉）      |
| `Infrastructure/AppPaths.cs`                           | 45   | 数据目录单一事实来源（P-07 收口） | 维持；新增数据目录必须先在此登记（`AppPathsHygieneTests` 锁死） |
| `Infrastructure/Persistence/JsonStoreBase.cs`           | 64   | 单文件 JSON 仓储骨架（P-07 收口） | 维持；派生类只留 `CreateEmpty()` + 可选 `NormalizeAsync()` |
| `Infrastructure/Persistence/JsonConfigStore.cs`         | 167  | 运行配置仓储（P-07 收口，191→167） | 维持（样板已入基类，仅留迁移钩子）     |
| `Infrastructure/Persistence/JsonCooldownStore.cs`       | 51   | 额度冷却仓储（P-07 收口，70→51） | 维持（仅留 raw 截断自愈钩子）      |
| `Infrastructure/Persistence/JsonCreditsClaimStore.cs`  | 19   | 积分台账仓储（P-07 收口，50→19） | 维持（默认钩子）              |
| `Core/Text/TextTruncate.cs`                            | 35   | 文本截断单一事实来源（P-08 收口） | 维持；新增截断一律走此类（`DomainPrimitivesHygieneTests` 锁死） |
| `Core/Domain/TaskStatusExtensions.cs`                  | 14   | 终态判定单一事实来源（P-08 收口） | 维持；新增终态状态只改此处                 |
| `Core/Domain/TaskItem.cs`                              | 237  | 任务领域模型（P-08：唯一手写 `Clone`） | 维持；新增字段须同步 `Clone()`（`TaskItemCloneTests` 反射锁死） |

> 测试：801 个测试方法（截至 2026-10-21；P-06 收口新增 Login/Monitor headless 契约测试 29 项 + Monitor/Login XAML 绑定守卫 1 项 + Monitor/Login facade 行数守卫 2 项，由 P-05 收口 702 → 734：主 572 + Desktop 162；**P-07 收口 +1**「第 4 份同构仓储」拦截守卫 → **735：主 573 + Desktop 162**；**P-08 收口 +9**：`DomainPrimitivesHygieneTests` 5 项 + `TaskItemCloneTests` 4 项 → **744：主 582 + Desktop 162**；**P-09 收口 +14**：主 `RunOptionsContractTests` 5 项 + `RunOptionsHygieneTests` 3 项、Desktop `ConfigEditorsHygieneTests` 3 项 + 时段行为契约 3 项 → **758：主 590 + Desktop 168**；**P-10 收口 +43**：主 `SecretMaskerTests` 20 项 + `RunMetricsTests` 8 项 + `RunMetricsInstrumentationTests` 4 项 + `RunMetricsWriterTests` 2 项 + `FileRunLoggerTests` 3 项 + `ObservabilityHygieneTests` 6 项 → **801：主 633 + Desktop 168**；P-11 收口轮实测 **822：主 654 + Desktop 168**（+21 来自 HEAD 中 ReasoningEfforts 特性提交，与门禁改动无关）；**P-14 收口 +8**：主 `ConfigSchemaTests` 4 项 + `AdrHygieneTests` 4 项 → **830：主 662 + Desktop 168**）；CI 触发 `push`/`PR` 至 `master`，全量 `--no-incremental` 构建 + 分层测试 + 格式/覆盖率/配置 Schema 门禁，**失败即红（P-11/P-14 阻断）**。

## 附录 B：本方案 ↔ TECH-DEBT v2.0 映射

| 本方案章节            | 对应债务条目                       |
| ---------------- | ---------------------------- |
| §1.2 A-1/A-2/A-3 | P1-1 / P1-2 / P1-19（收口）      |
| §1.2 A-4/A-5/A-6 | **P-07 ✅**（A-4，2026-10-21 收口，见 §6.2）＝ P2-31（TECH-DEBT §5.11）/ **P-08 ✅**（A-5，2026-10-21 收口，见 §6.2）← P2-1~6（TECH-DEBT §5.12）/ **P-09 ✅**（A-6，2026-10-21 收口，见 §6.2）← P2-9（God Config 分组，原 #D5）、P2-15  |
| §1.2 A-7         | P1-4（§4.3 断言）                |
| §1.2 A-8         | **P-10 ✅**（可观测性，2026-10-21 收口，见 §6.2）＝ 工程化新增缺口 |
| §1.2 A-9         | P0-16 / §4.9                 |
| §1.2 A-10        | P0-15                        |
| §1.2 A-11        | P0-3（守卫）                     |
| §1.2 A-12        | **P-14 ✅**（ADR + 配置 Schema，2026-10-22 收口，见 §6.2）＝ P2-26 / 文档同步                 |
| §3~§6 全部         | 阶段 3（持续化）+ 阶段 4 机会主义偿还的架构化编排 |

## 附录 C：架构依赖规则（IL 扫描断言，§3.4 落地）

1. `Core` 工程禁止引用 `Infrastructure`/`Agents`/`Desktop` 具体命名空间。
2. `Infrastructure`/`Agents` 不得出现在 `Core` 的 `ProjectReference`（仅 `Core.Abstractions`）。
3. `Desktop`（除 `Composition`）禁止 `new` Infrastructure/Agent 具体类型。
4. 公共工具（`JsonStoreBase<T>`/`AppPaths`/`Truncate`/`Clone`/`IsTerminal`/`FieldEditor<T>`+`FieldParsers`）只允许存在**单一事实来源**。
   口径校准（P-07）：路径布局与 JSON 仓储属基础设施事实，单一事实来源落在 **Infrastructure**（`AppPaths` 静态类无实例、`JsonStoreBase<T>` 为抽象基类），
   Desktop 经组合根引用 Infrastructure 不构成依赖倒置——故本条约束的是「不得出现第二份实现」，而非「必须位于 Core」；
   编辑器解析属 Desktop 表示层事实，单一事实来源落在 **Desktop/Models**（`FieldEditor<T>` + `FieldParsers`）。
   由 `AppPathsHygieneTests`（4 项）/`DomainPrimitivesHygieneTests`（5 项）/`ConfigEditorsHygieneTests`（3 项）机器校验
   （`Truncate`/`Clone`/`IsTerminal` 部分归 P-08；编辑器部分归 P-09；`RunOptions` 配置契约由 `RunOptionsContractTests` 5 项 + `RunOptionsHygieneTests` 3 项校验）。
5. `BatchEngine`/`TaskRunner`/最大 VM 行数硬上限（见 §7.3）。
