# BatchAgentRunner 技术债务治理方案 v2.0

> 盘点日期：2026-10-17　｜　取代 v1.0（同日早些时候，未提交）  
> 盘点范围：`src/**`（117 文件 / 18,119 行）、`tests/**`（32 文件 / 5,845 行）、全部工程配置、git 历史（12 提交）  
> 事实基线：net10.0 / Avalonia 12.1.4 / CommunityToolkit.Mvvm 8.4.2，**无 CI、无门禁、无 CPM**
>
> **当前进度（2026-10-19 晚）**：阶段 −1（B 组 5 项）、阶段 0（A 组 10 项 + P0-17）、阶段 1（建网 6 项）**均已完成**；P0 清单 17 项全部清偿，全量构建 0 警告 / 0 错误（`TreatWarningsAsErrors=true`）。**阶段 2 动骨已完成（2026-10-18 ~ 2026-10-19）**：P1-3（提 `IBatchEngine` + 窄化构造）、P1-9（`IProcessRunner`）、P1-10（`IFileSystem`）、P1-16（取消静态注册表）、P1-1（拆 6 协作者）、P1-2（状态机外置）均已提交；**P1-4/P1-5 已收口（2026-10-19）**——`Desktop/Composition/EngineFactory` 成为 Desktop 中唯一 `new` Infrastructure/Agents 具体类型的位置（原 `CredentialVaultHost.CreateDefault`、`EngineHost` 的 `ShutdownService`、`TemplateEditorViewModel` 的 `FileWorkflowTemplateStore` 三处残留装配全部收回组合根），§4.3「Desktop 不得直接 `new` Infrastructure 具体类型」IL 扫描断言落地（Desktop.Tests 3/3 通过）。**P1-1 已收口（2026-10-19 晚，提交 `39bdb14`）**：补齐拆分产物 ① `ITaskDispatcher` 白箱单测 19 项——该协作者随 REQ-020 功能提交落地时未同步协作者测试模式，是六个协作者中唯一的白箱覆盖缺口，补齐后 §3.4 全部产物均有直接契约测试；同轮 `chore(debt)` 清理 `BatchEngine` 拆分后残留的 3 个空节标题注释（`596c165`）。P1-19（Desktop 补测试 + 契约收敛）已于 2026-10-19 收口（Avalonia headless 测试基座 + `UiThread` 线程收敛契约 2 项 + churn 热点 `ConfigView`/`Login`/`Monitor`/`SetupWizard` VM 契约测试 13 项，Desktop.Tests 39→54）。P1-3 为本轮重点，配套 §4.3 断言与 `BatchEnginePhase2Tests` 契约测试已落地，详见 §5.5。**阶段 2 收尾补漏（2026-10-20）**：P1-6（异步初始化门消除同步阻塞 + IL 断言）、P1-7（事件订阅泄漏退订）、P1-11（Vault 根目录 ACL 加固 + `WriteManifest` 原子写）、P1-12（CLI 权限固化治理：作用域收窄 + 原子写 + 异步化）四项均为阶段 2 声明「完成」后追加的收口项，详见 §5.5 与 §4.8。**阶段 3 持续化补完（2026-10-20）**：P1-13（`CliProcessRunner.KillTree` 重写——原生 `Kill(entireProcessTree:true)` 取代外部 `taskkill` + 同步 `WaitForExit(3000)` 阻塞）为「重写」准入四项中最后一笔收口；P1-14（`LocalGitIntegration` 子进程执行加固——超时回收 + stdin 重定向 + 启动失败收敛）同步收口；**P1-15 高危盲区清零**——`CodeBuddyCreditsApiClient` / `BackupManager` / `ShutdownService` 三项阶段 1 顺延的高危零覆盖盲区补齐白箱测试（`CodeBuddyCreditsApiClientTests` 11 + `BackupManagerTests` 6 + `ShutdownServiceTests` 2），详见 §5.6。测试基线（2026-10-20 P1-15 收口轮实跑）：主测试项目 **475 用例**（快 448 / 慢 27）+ Desktop.Tests **58 用例** = **533 通过 / 0 失败**（主测试较 P1-14 收口轮 450 增加 25 例：P1-15 三个高危盲区白箱测试；本轮复跑未再现测试宿主退出崩溃，历史记录见 §5.5 边界）。**阶段 4 持续化（2026-10-20 ~ 2026-10-21）**：P2-26（误导性注释清算 + §4.7.1 计划性延期台账 + `CommentHygieneTests`，见 §5.7）、P2-27（评估维持「主动不偿还」，见 §5.8）、P2-29（`_harness/` 产物堆清理，见 §5.9）、P2-30（逃逸阀台账四要素补齐 + 未登记的生产代码豁免归位 + `EscapeValveHygieneTests`，见 §5.10）四项已收口；测试基线刷新为 **550 通过 / 0 失败**（2026-10-21 P2-30 收口轮实跑：主 492 + Desktop 58，较 P2-26 收口轮 545 +5）。**P2-31 已收口（2026-10-21）**：三份 `Json*Store` 的加载/原子写样板抽为 `JsonStoreBase<T>`，9 处 `"BatchAgentRunner"` 路径魔法串收口为 `AppPaths` 常量类，并新增 `AppPathsHygieneTests` 守卫；测试基线 **565 通过 / 0 失败**（主 507 + Desktop 58，+15）。**ARCH 架构重构阶段收口轮（2026-10-21，执行记录见 ARCH §6.2）**：P-01（`WaitCoordinator` 抽取）、P-02（`BatchEngine` facade 953→400 行 + 依赖方向 IL 断言固化 + facade 行数守卫）、P-03（`TaskRunner` 策略对象化，903→76 行薄 facade + 4 个 Policy 单测）、P-04（`ConfigViewModel` 1065→366 行，11 子编辑器 + 共享校验）、P-05（`TemplateEditorViewModel` 674→240 行，3 Coordinator + 4 行 VM）、P-06（`MonitorViewModel` 983→388 行 + `LoginViewModel` 751→280 行，Monitor 8 协作者 + Login 5 协作者 + 3 行 VM 独立文件 + headless 契约测试 29 项 + XAML 绑定守卫）依次收口；测试基线 **734 通过 / 0 失败**（主 572 + Desktop 162，2026-10-21 P-06 收口轮实跑）。**P-07 已收口（2026-10-21）**：`JsonStoreBase<T>` + `AppPaths` 去重（A-4）复核真值并加固守卫——`AppPathsHygieneTests` 3→4 项（新增「Infrastructure 内 `Json*Store` 具体类型必须继承 `JsonStoreBase<T>`」类型扫描，防第 4 份同构仓储回潮，负测试实测可捕获）；代码本体同 P2-31 轮未变；测试基线 **735 通过 / 0 失败**（主 573 + Desktop 162，ARCH §6.2 P-07 执行记录）。**P-08 已收口（2026-10-21）**：领域原语去重（A-5，承接 P2-1~P2-6）——残留内联截断 3 处并入 `TextTruncate`、修掉 `TaskItem.Clone()` 对 `StageResults` 的浅拷贝缺陷（`StageResult` 记录化 + 逐元素 `Clone`，原「快照与原型共享阶段结果元素」会污染原型），新增 `DomainPrimitivesHygieneTests` 5 项 + `TaskItemCloneTests` 4 项守卫；同轮清偿 P2-25 遗留的 `BatchEngine` 死私有 `Truncate` 别名（`BatchEngine` facade 400→**397 行**，见 §5.14 步骤 ⑤）；测试基线 **744 通过 / 0 失败**（主 582 + Desktop 162，见 §5.14 / ARCH §6.2 P-08 执行记录）。**P-09 已收口（2026-10-21）**：配置契约去重（A-6，承接 P2-9 / P2-15）——`RunOptions` 由 363 行单一大类按 12 配置域横向切片为 `RunOptions.01~11-*.cs`（本体 369→18 行，切片各 ≤49 行，**JSON 形状与落盘字节逐字不变**，原 §4.7.1 计划性延期 **#D5 结项**），JSON 往返克隆由守卫禁复现；`ConfigEditors` 解析字段全量收敛 `FieldEditor<T>` + `FieldParsers` 单一解析来源（时段起止一并泛型化 + 删掉 `StartText`/`EndText` 裸字符串桥接 + 修掉「非法输入静默抹掉用户已配置免费时段」缺陷），视图侧 7 个缺反馈的输入框补齐校验反馈、错误文本样式收为单一来源；新增 `RunOptionsContractTests` 5 项 + `RunOptionsHygieneTests` 3 项 + `ConfigEditorsHygieneTests` 3 项守卫（负测试实测均可捕获）；测试基线 **758 通过 / 0 失败**（主 590 + Desktop 168，见 §5.15 / ARCH §6.2 P-09 执行记录）。 **P-10 已收口（2026-10-21）**：可观测性（A-8，工程化新增缺口）——结构化日志接线：`IRunLogger` 增 `Write(RunLogEntry)` 作为唯一落盘点，`FileRunLogger` 十余个 `Write*` 全部转条目后经此渲染（文本 `run-*.log` 正文逐字不变 + 新增 `run-*.jsonl` 结构化流）；`RunId` 贯穿事件 / 日志 / 指标（`BatchEvent.RunId` 由 `BatchEngine.Raise` 统一填充）；`RunMetrics` 七类运行级指标埋点（吞吐 / 平均耗时 / 降级次数 / 兜底触发 / 冷却·时段跳过 / 失败率 / 误切换率）+ `metrics-<run_id>.json` 落盘；`SecretMasker` 脱敏在渲染入口统一生效（原「凭证不落日志」零基础设施）。同轮收口 20 处裸级别字面量（改为 `RunLogLevels` 常量）、`BatchEngine.ResolveWorkspacePath` 下沉 `WorkspaceResolver`（facade 维持 399 行 ≤400）、`NullRunLogger` 拆为独立文件。新增 `ObservabilityHygieneTests` 6 项守卫 + `SecretMaskerTests` 20 项 / `RunMetricsTests` 8 项 / `RunMetricsInstrumentationTests` 4 项 / `RunMetricsWriterTests` 2 项 / `FileRunLoggerTests` +3 项（负测试实测三项可捕获）；测试基线 **801 通过 / 0 失败**（主 633 + Desktop 168，见 ARCH §6.2 P-10 执行记录）。**ARCH P-11（2026-10-22 ✅ 收口，见 §5.16）**：CI 转阻断——`ci.yml` 四道门禁（全量 `--no-incremental` 构建 / `dotnet format --verify-no-changes`（CHARSET 假阳性按 §4.7 #9 口径由 `scripts/ci/Assert-Format.ps1` 过滤）/ 分层单测 / 覆盖率门禁 `scripts/ci/Assert-Coverage.ps1`）失败即红；行覆盖率锁定基线 Core 44% / Infra 39% / Agents 23% / Desktop 50%（向 ARCH §7.3 的 85/70/50 爬升）；required status check（`build-and-test`）待仓库管理员在分支保护勾选；测试基线更新为 **822 通过 / 0 失败**（主 654 + Desktop 168）。**ARCH P-14（2026-10-22 ✅ 收口，见 ARCH §6.2）**：ADR 机制落地（`docs/adr/` README 机制/索引 + 模板 + 11 篇已接受决策，`AdrHygieneTests` 4 项守卫编号连续/必备章节/索引双向一致）+ 配置 JSON Schema 单一事实来源（`docs/config.schema.json` 由 `RunOptions` 派生，`ConfigSchemaTests` 4 项锁死键集合与代码逐字相等并校验示例/畸形配置，`scripts/ci/Assert-ConfigSchema.ps1` 进 CI 阻断门禁）；测试基线更新为 **830 通过 / 0 失败**（主 662 + Desktop 168；+8 = `ConfigSchemaTests` 4 + `AdrHygieneTests` 4）。**ARCH P-12（2026-10-22 ✅ 收口，见 §5.17）**：规范集 + 静态安全扫描——`.editorconfig` 命名规则集（接口 I 前缀 / 类型与对外可见成员 PascalCase / 参数与局部变量 camelCase / 私有实例字段 `_camelCase` / 静态字段 PascalCase）+ 五条安全 CA 规则 `error` 化（CA5350/CA5351/CA3001/CA3075/CA5392）；恢复 `CS1591`（四个 `src` 工程 `GenerateDocumentationFile`，公共 API 全量补齐并逐条改写为有信息量的说明，`NoWarn CS1591` 删除、逃逸阀台账 #3 退役）；新增 `SecurityHygieneTests`（7 个测试方法 / 11 个用例）锁死命令注入唯一拼装点 / Vault 账号 ID 逐个方法净化 / 源码无硬编码密钥，并在 `CommentHygieneTests` 追加「文档占位（成员名即 summary）」守卫防「补文档等于塞空话」；同轮把 6 条 facade 行数闸门改为「代码行（排除 `///`）」口径；测试基线 **842 通过 / 0 失败**（主 674 + Desktop 168，+12）。

---

## 批准（Approval）

| 项          | 内容                                                                                   |
| ---------- | ------------------------------------------------------------------------------------ |
| **审批状态**   | ✅ 已批准（Approved）                                                                      |
| **批准人**    | 项目负责人（当前 WorkBuddy 会话用户）                                                             |
| **批准日期**   | 2026-10-18                                                                           |
| **批准范围**   | 全文：阶段 −1 / 0 / 1 执行记录、阶段 2 / 3 路线图、§3 偿还策略、§4 防新增六道墙与逃逸阀台账、附录 A/B                    |
| **效力说明**   | 自批准日起，本方案为 BatchAgentRunner 技术债务治理的**权威基线**；任何后续变更须按 §4.6 债务预算与 §4.7 逃逸阀规则登记，不得口头覆盖。 |
| **当前进度基线** | 阶段 −1 / 0 / 1 已完成，P0 清单 17 项全部清偿；阶段 2 动骨全部完成（P1-3/4/5/9/10/16、P1-1 拆 6 协作者、P1-2、P1-19 均已完成，详见 §2 / §5.5）。P1-4/P1-5 于 2026-10-19 收口：组合根 `EngineFactory` 为 Desktop 唯一 `new` 点，§4.3 对应 IL 断言落地。阶段 3 持续化补完（P1-13/14/15/21）、阶段 4 P2 第一批（P2-26/P2-27/P2-29/P2-30/P2-31）已完成（§5.6 ~ §5.11）。**P-04（2026-10-21 ✅ 收口）**：`ConfigViewModel` 由 1065 行分解为 11 个子编辑器 VM（账号卡/任务/调度/关机/安全/降级/兜底/通知/Git/工作流/通用）+ 共享校验器（`ConfigFieldRules`/`ConfigStatus`/`ModelCatalogFiller`）+ `ConfigSaveCoordinator`，facade 收口至 **366 行**（≤400 守卫测试锁死，见 ARCH §6.2）；全量测试 **701 通过 / 0 失败**（主 569 + Desktop 132，较 P2-31 收口轮 565 → 701：Desktop 58→132 含 P-04 headless 契约测试 + facade 行数守卫；ARCH P-01~P-04 全部 ✅ 收口）。 |

---

## 0. 本版相对 v1.0 的变更

v1.0 的 P0 缺陷清单经本版实测复核**全部仍然成立**（重点复核了 P0-1、P0-2，见附录 B）。本版不是推翻，而是三处结构性修正：

| # | 变更                                             | 理由                                                                 |
| - | ---------------------------------------------- | ------------------------------------------------------------------ |
| 1 | **显式定义 P0/P1/P2 判据**（v1.0 缺）                   | 无判据则分级随人，台账无法交接                                                    |
| 2 | **门禁类债务 P2 → P0**（v1.0 的 P2-16/17/18 + 新增 4 项） | v1.0 §0 已断言"增量才是主要风险"，却把防新增排到阶段 1，自相矛盾。这 7 项**合计成本 < 1 人日**，应与缺陷同级 |
| 3 | **补 12 项 v1.0 未覆盖的实测债务**                       | v1.0 只扫了源码，未扫 git 历史、构建真相、仓库卫生                                     |

---

## 1. 债务清单

### 1.0 分级判据（本版新增，后续分级一律以此为准）

| 级别     | 判据（满足任一）                                                                  |
| ------ | ------------------------------------------------------------------------- |
| **P0** | ① **已在产生真实代价**：错误结果 / 数据丢失 / 安全 / 用户侧崩溃；② **使债务持续放量**，且修复成本 < 1 人日（治理真空类） |
| **P1** | 不修则**下一轮改动必然失控**（结构性问题、可测性缺失、变更放大器）；修复成本 1 人日 ~ 1 人周                      |
| **P2** | 不影响正确性，但持续抬高**单位改动成本**；随相关任务机会主义带走                                        |

判据 ② 是本版与 v1.0 的核心分歧：**"修复成本 < 1 人日"且"不修会持续产生新债务"的，一律 P0**，不看它是否造成过 bug。

---


### 1.1 P0 — 立即偿还（共 17 项）

#### A 组：正确性 / 安全（10 项，沿用 v1.0，本次实测复核仍成立）

**状态：阶段 0 已全部完成（2026-10-17）。** 台账改用不变量描述（见 §4.8 约定），位置列不再依赖行号。

| ID        | 位置                                                                                          | 债务                                                                                 | 影响                                                    | 成本 | 偿还方式                                                |
| --------- | ------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------- | ----------------------------------------------------- | -- | --------------------------------------------------- |
| **P0-1**  | `Core/Engine/BatchEngine.cs`（TryFallbackAsync 内 `cost` 须被 `MaxCostPerTask` 截断）              | `cost = Math.Min(cost, Math.Max(MaxCostPerTask, cost))` **恒等于 cost**，是 no-op       | 单任务成本上限完全失效 → 超额扣费，且表面"已封顶"极具迷惑性                      | 极小 | 重构（1 行 + 1 不变量单测）✅ 阶段 0 已完成（2026-10-17）             |
| **P0-2**  | `Agents/Internal/CliProcessRunner.cs`（异步读取回调 EOF + `DrainOutputAsync` 门）                    | `WaitForExitAsync` 后直接读 buffer，未排空 `OutputDataReceived` 回调                         | 输出尾部丢失 → 漏判配额重置（误降级/兜底）、stream-json `result` 截断导致误判失败 | 中  | 重写（局部：回调 EOF 信号 + TCS 门）✅ 阶段 0 已完成（2026-10-17）      |
| **P0-3**  | `Infrastructure/Git/LocalGitIntegration.cs`（参数走 `ArgumentList` + `GitRefName` 白名单）          | 命令行字符串拼接，仅用 `"` 包裹未转义 `"`                                                          | 配置驱动的**任意参数注入**，可注入 `-c core.sshCommand=` 实现代码执行      | 中  | 重构（`ArgumentList` + 输入白名单）✅ 阶段 0 已完成（2026-10-17）    |
| **P0-4**  | `Core/Engine/BatchEngine.cs`（`_writeGate` 读改写同锁 + `_persistGate` 单写者）                       | `CompleteTask/FailTask/MarkInterrupted` 并发调 `PersistAsync(CancellationToken.None)` | `Concurrency>1` 时并发写同一结果文件 → 状态回退、文件损坏                | 中  | 重构（单写者队列 + 读改写同锁）✅ 阶段 0 已完成（2026-10-17）             |
| **P0-5**  | `Core/Engine/BatchEngine.cs`（`_fallbackLock` 保护 check-then-act）                             | `_fallbackBatchCost +=`、`_fallbackModelsUsed.Add` 无锁                               | 并发下成本累计重复/越限，`List.Add` 内部损坏                          | 小  | 重构（共享锁保护 check-then-act）✅ 阶段 0 已完成（2026-10-17）      |
| **P0-6**  | `Desktop/ViewModels/*`（经由 `EngineHost`/`UiThread` 统一收敛）                                     | `ConfigureAwait(false)` 后直接写 `ObservableCollection`                                | 后台线程改 UI 集合 → Avalonia 跨线程异常，用户侧随机崩溃                  | 小  | 重构（EngineHost 统一 Post，见 P0-7）✅ 阶段 0 已完成（2026-10-17） |
| **P0-7**  | `Desktop/Services/EngineHost.cs`（事件广播单一收敛点 `UiThread.Post`）                                 | 引擎事件在后台线程触发，注释明示"订阅方自行切线程"                                                         | 线程切换责任外包给 7 个订阅方，必然有人踩坑（即 P0-6）                       | 小  | 重构（单一收敛点）✅ 阶段 0 已完成（2026-10-17）                     |
| **P0-8**  | `Infrastructure/TaskFiles/MarkdownInPlaceWriter.cs`（`WriteBackAsync` 复用 `AtomicFileWriter`） | 读—改—写非原子，同库已有 `AtomicFileWriter` 却不用                                               | 写入中断 = **用户任务文件截断**                                   | 小  | 重构（复用 `AtomicFileWriter`）✅ 阶段 0 已完成（2026-10-17）     |
| **P0-9**  | `Infrastructure/CredentialVault/GuidedLoginOrchestrator.cs`（ClearLiveCredentials 先备份）       | 引导登录先 `File.Delete(livePath)` 无备份，后等待超时抛异常                                         | 失败后**用户凭证已消失且不可恢复**                                   | 中  | 重构（先备份再删 + 失败回滚）✅ 阶段 0 已完成（2026-10-17）              |
| **P0-10** | `Desktop/Services/EngineHost.cs` + `Core/Engine/AutoShutdownCoordinator.cs`（可撤销倒计时）         | 自动关机直接 `TryShutdownAsync()`                                                        | 违反设计文档 §6.3「需 60s 可撤销对话框」硬约束                          | 中  | 补齐功能（倒计时 + 可撤销）✅ 阶段 0 已完成（2026-10-17）               |

#### B 组：治理真空（7 项，本版新增 / 由 P2 提级）

特征：**不修，则 A 组修完下一季度会原样长回来**。合计成本 < 1 人日。

**状态：阶段 −1 已全部完成（2026-10-17）。**

| ID        | 位置                                                     | 债务（实测证据）                                                                                                                                                                                                                                                | 影响                                                                               | 成本 | 偿还方式                                                                                       |
| --------- | ------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- | -- | ------------------------------------------------------------------------------------------ |
| **P0-11** | git 历史                                                 | **12 个提交中 10 个提交信息为空**；首提交 `9be9485` 一次性 176 文件 / +22,000 行；`b86c0fe` 单次 63 文件 / +8,487                                                                                                                                                                 | 6 周后**无法追溯任何一次变更的意图**，债务无法归因、churn 分析失效；与自家 `CODE-REVIEW §5.2`「单 PR 聚焦一个关注点」直接冲突 | 极小 | 隔离（commit-msg 钩 + 模板）— ✅ 阶段 −1 已完成                                                         |
| **P0-12** | `Directory.Build.props`                                | 增量构建报 **0 警告**，`--no-incremental` 全量重建 Core 实测 **36 个 CA2007**（全方案 50+）。CI 若看增量结果会**永远全绿**                                                                                                                                                              | "构建干净"是假象，`TreatWarningsAsErrors` 一旦开启会一次性爆雷                                     | 小  | 重构（先清存量警告，再改 CI 用全量基线）✅ 阶段 1 已完成（2026-10-18，全量 0 警告 + `TreatWarningsAsErrors=true`，见 §5.4） |
| **P0-13** | `.gitignore` 末尾 4 行                                    | **按日期逐个文件名 ignore**：`/.workbuddy/memory/2026-10-16.md`、`/.workbuddy/memory/2026-10-17.md`… 而非 `.workbuddy/`                                                                                                                                             | 债务**每天都在自动新增**；`/TaskList` 是在 `tasks.xlsx` 已提交**之后**才补的，历史仍在                     | 极小 | 重构（目录级规则 + `git rm --cached`）— ✅ 阶段 −1 已完成                                                 |
| **P0-14** | 仓库（实测 `git ls-files`）                                  | 10 项产物/隐私已入库：`.playwright-cli/page-*.yml`×2、`.workbuddy/memory/*.md`×2、`.workbuddy/tmp/check_px.py`、`gen_icon2.py`、`.tmp_reflect.ps1`（**含个人绝对路径**）、`models.yaml`（**0 字节**）、`samples/tasks.json.backup-20261015181009`、`TaskList/tasks.xlsx`（**真实运行数据**） | 隐私外泄面 + 仓库膨胀 + 新人 `clone` 后环境被污染                                                 | 小  | 重构（清理 + 净化历史）— ✅ 阶段 −1 已完成                                                                 |
| **P0-15** | `Directory.Build.props:8,11`                           | `TreatWarningsAsErrors=false` + `NoWarn CS1591`；无 `EnforceCodeStyleInBuild`、无 `AnalysisLevel`、无 `AnalysisMode`                                                                                                                                          | 公共 API 文档约束被显式关闭；`.editorconfig` 的 40+ 条规则**构建期完全不执行**                           | 极小 | 重构（配置落地，见 §4.2）— ✅ 阶段 −1 已完成                                                               |
| **P0-16** | 仓库根                                                    | **无任何 CI**（无 `.github/`，无流水线）；`coverlet.collector` 已引用但无收集管线                                                                                                                                                                                            | 债务与回归**无自动发现机制**，全靠人                                                             | 中  | 补齐（build+test+覆盖率，先只报不阻断）— ✅ 阶段 −1 已完成                                                     |
| **P0-17** | `tests/BatchAgentRunner.Desktop.Tests/`（新建，引用 Desktop） | 测试项目只引用 Core / Infrastructure / Agents，**唯独不引用 Desktop（5,411 行 = src 的 30%）**                                                                                                                                                                           | Desktop **结构上不可测试**，P0-6/P0-7 经 P1-19 `UiThreadContractTests` 锁住、P1-7 经 `TaskRowViewModelTests` 锁住（阶段 2 收口）                              | 中  | 重构（补引用 + 首批冒烟测试）— ✅ 阶段 0 已完成（2026-10-17，新建 Desktop.Tests 引用 Desktop，首批冒烟 12 项）             |

> P0-17 说明：Desktop 不是"没写测试"，而是**测试项目根本没有引用它**——即使想写也写不了。这是可测性缺失，不是测试缺失，故列 P0 而非 P1。

---


### 1.2 P1 — 短期偿还（1–2 个迭代：结构性与可测性）

沿用 v1.0 的 P1-1 ~ P1-15，另补 6 项实测新增（P1-16 ~ P1-21）。

**沿用项（摘要，详见 v1.0 §1.2）**

| ID    | 债务                                                                                                                                                                | 偿还方式                                                                                                                                                                                                                                            |
| ----- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1-1  | `BatchEngine.cs` 1960 行 God Object，16 类职责（变更放大器）                                                                                                                  | 重构（拆 6 协作者，见 §3.4）✅ 阶段 2 已完成并收口（2026-10-19：ITaskDispatcher / ITaskRunner / IFallbackBilling / IGitWorkflow / IBatchReportBuilder+IBatchNotifier / IPreflight，六个协作者均有白箱单测——产物 ① `ITaskDispatcher` 的白箱缺口于 2026-10-19 晚补齐 19 项，见 §5.5） |
| P1-2  | `ExecuteTaskAsync` 249 行 / `PreflightAsync` 196 行，`while(true)+switch+goto case`                                                                                  | 重构（状态机外置 + 内拆）✅ 阶段 2 已完成（2026-10-19 外置 e7c9eb9；2026-10-19 内拆 1790708：单任务状态机显式状态机化，ExecuteTaskAsync 由 287 行压至 ≤60 行 / CC≤15，见 §5.5）                                                                                                                                        |
| P1-3  | `BatchEngine` 为 `sealed` 且无 `IBatchEngine` 接口，14 参数构造                                                                                                             | 重构（提接口，P1-1 的前置）✅ 阶段 2 已完成（2026-10-19：提 `IBatchEngine` + 构造由 14 参数窄化为「5 必需 + 1 可选 `BatchEngineDependencies` 聚合」，配套 §4.3 断言与 `BatchEnginePhase2Tests` 契约测试，见 §5.5） |
| P1-4  | UI 层直引 Infrastructure/Agents 并 `new` 全部实现类 → **UI 成为组合根**（架构倒置）                                                                                                   | 重构（Core 暴露 `IEngineFactory` + DI）✅ 阶段 2 已完成并收口（2026-10-19：引入 `IEngineFactory`/`IEngineHost`，组合根下沉 EngineFactory；收口将三处残留装配——`CredentialVaultHost.CreateDefault`、`EngineHost` 的 `ShutdownService`、`TemplateEditorViewModel` 的 `FileWorkflowTemplateStore`——全部收回组合根，§4.3「Desktop 不得直接 new Infrastructure 具体类型」IL 扫描断言落地，见 §5.5）                                                                              |
| P1-5  | 无 DI 容器，手工 `new` 7 个 VM + `EngineHost`（sealed 无接口）                                                                                                                | 重构（与 P1-4 合并）✅ 阶段 2 已完成并收口（2026-10-19，随 P1-4：`EngineHost` 实现 `IEngineHost` 契约，页面 VM 只依赖接口；模板库等端口经宿主暴露（`IWorkflowTemplateStore`），`FakeEngineHost` 测试替身同步扩容，见 §5.5）                                                                                                                                                |
| P1-6  | `MainViewModel.cs:20`、`EngineHost.cs:136` `GetAwaiter().GetResult()` 同步阻塞                                                                                         | 重构（异步初始化门）✅ 阶段 2 已完成（2026-10-20：`MainViewModel` 抽 `InitializeAsync` 异步门 + `EngineHost.RebuildEngine`→`RebuildEngineAsync(CancellationToken)`，4 处 VM 调用点改 `await`，新增 IL 架构断言锁住「不得再引入同步阻塞」，见 §5.5） |
| P1-7  | 事件 lambda 订阅**全程无 `-=`**，行 VM 被长生命周期 `TaskItem` 永久持有                                                                                                              | 重构（弱事件 / Dispose）✅ 阶段 2 已完成（2026-10-20：`TaskRowViewModel` 实现 `IDisposable`，订阅 `TaskItem.PropertyChanged` 改用存储委托并在 `Dispose` 中 `-=`；`MonitorViewModel.SyncTasks` 在 `Tasks.Clear()` 前处置旧行，重建后重新指向同 Id 新行避免悬空；新增 `TaskRowViewModelTests` 3 项锁死「处置后领域对象变更不再驱动本行」不变量，见 §5.5） |
| P1-9  | `Core/Scheduling/GateEvaluator.cs:114-170` 在 Core 层直接 `new Process()` + `cmd.exe /c "{cmd}"`                                                                      | 隔离（`IProcessRunner` 端口定义落 `Core/Abstractions`、实现 `RealProcessRunner` 落 Infrastructure）✅ 阶段 2 已完成（2026-10-19，见 §5.5）                                                                                                                                                  |
| P1-10 | `BatchEngine.cs:196,226,1531` 绕过自有 `ITaskFileStore` 直接 IO                                                                                                         | 重构（IO 全走抽象）✅ 阶段 2 已完成（2026-10-19：引 `IFileSystem` 端口，见 §5.5）                                                                                                                                                      |
| P1-11 | Vault 目录继承默认 ACL；`manifest.json` 明文；`WriteManifest` 是同类中唯一非原子写                                                                                                    | 重构（收 ACL + 统一原子写）✅ 阶段 2 收口（2026-10-20：Vault 根目录 ACL 加固 + `WriteManifest` 改为原子写，见 §5.5）                                                                                                                                                  |
| P1-12 | `CliPermissionSettings.cs:17-46` 静默把用户 CLI 的 `defaultMode` 改为 `bypassPermissions`（**影响范围不止本工具**）                                                                  | 重构（作用域收窄 + 原子写）✅ 阶段 2 收口（2026-10-20：仅当 defaultMode 未显式设置才写入 bypassPermissions、已设值幂等不覆盖用户 CLI 全局配置；落盘改走 AtomicFileWriter 原子写，不再 File.WriteAllText；方法转 async 避免同步阻塞；见 §5.5）                                                                                          |
| P1-13 | `CliProcessRunner.cs:311-322` `KillTree` 依赖外部 `taskkill` + `WaitForExit(3000)` 同步阻塞                                                                               | 重写（.NET 原生 `Kill(entireProcessTree:true)`）✅ 阶段 3 收口（2026-10-20：移除外部 `taskkill` 依赖 + 同步 `WaitForExit(3000)` 阻塞，改原生整树回收；新增 `CliProcessRunnerTests` 3 项锁死整树回收/已退出不抛/单进程终止，见 §5.6） |
| P1-14 | `LocalGitIntegration.cs:80-101` `WaitForExitAsync(ct)` 无超时、无 stdin 重定向                                                                                            | 重构（超时 + stdin 重定向）✅ 阶段 3 收口（2026-10-20：新增 `LocalGitIntegration` 超时构造参数 `DefaultTimeoutSeconds=120` + stdin 重定向（启动即关，子进程等待终端输入直接 EOF 不挂起）+ 启动失败收敛（`Start()` 异常按「尽力而为」契约回失败码）；`WaitForExitAsync` 经 linked CTS 叠加上限、超时即 `Kill(entireProcessTree:true)` 整树回收并按失败返回；新增 `LocalGitIntegrationTests` 10 项锁死超时回收/stdin EOF/正常退出不误报/取消传播/缺失二进制收敛/IL 锁死 stdin+超时/真实 git 回归，见 §5.6）                                                                                                                                                                                                                              |
| P1-15 | 高危模块零覆盖：`CodeBuddyCreditsApiClient`、`XlsxTaskParser/Writer`、`BackupManager`、`LocalGitIntegration`、`ShutdownService`、`DangerousOperationGuard`、`CliCommandBuilder` | 补齐 ✅ **全部收口**（阶段 1：XlsxTaskWriter 2 项 + CliCommandBuilder 8 项 + DangerousOperationGuard 6 项，见 §5.4；阶段 3：`LocalGitIntegration` 已由 P1-14 补齐子进程执行契约测试、`BackupManager`/`ShutdownService`/`CodeBuddyCreditsApiClient` 各补白箱测试，见 §5.6；**高危模块零覆盖清零**） |

**本版新增项**

| ID        | 位置                                                                      | 债务                                                                                                                                                       | 影响                                                                          | 成本 | 偿还方式                                                                                                                                                  |
| --------- | ----------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- | -- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| **P1-16** | `Infrastructure/CredentialVault/CredentialLocationRegistry.cs:40,72,74` | `public static class` + `static readonly Dictionary` + `public static void SetLiveRootOverride(...)`；生产代码 `Desktop/Services/EngineHost.cs:94` 调用它做全局路径注入 | Service Locator 反模式 + **非线程安全 `Dictionary`**；全局状态被 UI 生命周期意外改写（原 P1-8 的实体化） | 中  | 重构（显式传入，取消静态入口）✅ 阶段 2 已完成（2026-10-19：改为显式注入实例服务，见 §5.5）                                                                                                       |
| **P1-17** | 5 个 csproj                                                              | **无 `Directory.Packages.props`（无 CPM）**，包版本手写散落；`Core.csproj` 引用 `Microsoft.Extensions.Logging.Abstractions 10.0.13` 而全仓 `ILogger` 使用 **0 处**（无效依赖）      | 版本漂移；依赖图不可审计                                                                | 小  | 重构（引入 CPM + 删无效依赖）✅ 阶段 1 已完成（2026-10-18：`Directory.Packages.props` + `ManagePackageVersionsCentrally=true`；任何 csproj 再写 Version 即 NU1008 构建错误，结构性防漂移） |
| **P1-18** | `Infrastructure/Persistence/JsonCreditsClaimStore.cs:21`                | 第 18 行 `directory ?? ...` 建目录，第 21 行却 `Path.Combine(directory!, ...)` → 默认构造**必抛 `ArgumentNullException`**；生产唯一调用点 `EngineHost.cs:202` 恰好总传参，故未暴露        | 定时炸弹：任何走默认构造的新调用点立刻崩                                                        | 极小 | 重构（1 行）✅ 阶段 1 已完成（2026-10-18，修复 + 不变量测试 2 项）                                                                                                          |
| **P1-19** | Desktop 层（churn 实测）                                                     | `ConfigView.axaml`、`TemplateEditorViewModel.cs`、`ReportViewModel.cs`、`ConfigViewModel.cs` 等 4 次提交被改（全仓仅 12 提交，即 1/3 提交都动过），且**零测试**                      | 界面契约未稳定却已铺开 5,411 行 → 重构 Desktop 时回归面最大                                     | 大  | 重构（补测试 + 收敛契约）✅ **阶段 2 已完成（2026-10-19）**：Avalonia headless 测试基座 + `UiThread` 线程收敛契约 2 项 + churn 热点 `ConfigView`/`Login`/`Monitor`/`SetupWizard` VM 契约测试 13 项；Desktop 测试 39→54；VM 仅依赖 `IEngineHost` 端口，界面契约收敛，见 §5.5                                                                                    |
| **P1-20** | `Desktop/Converters/ConfigHubConverters.cs:18,31`                       | 两处 `ConvertBack => throw new NotImplementedException()`                                                                                                  | 双向绑定一旦触发即崩溃                                                                 | 极小 | 重构 ✅ 阶段 1 已完成（2026-10-18：返回 `BindingOperations.DoNothing` + 不变量测试）                                                                                    |
| **P1-21** | `Core/Options/RunOptions.cs:280,284,288`                              | 生产 API 地址 `https://www.codebuddy.cn` 及 `/billing/meter/*` 路径写进默认值                                                                                        | 环境切换只能靠覆盖，测试易打到生产                                                           | 小  | 重构（抽配置/常量）✅ **阶段 3 已完成（2026-10-20）**：新建 `CodeBuddyApiConstants`（`ProductionApiBase`/`BalancePath`/`ClaimPath` 单一事实来源），`CreditsClaimConfig` 默认值与 `CreditsClaimMigrator` 迁移目标均改引用该常量，消除多处硬编码魔法串；`CreditsClaimConfigTests` 4 项锁死「默认值==规范生产端点 + 常量形态合规 + 显式覆盖不回退」，见 §5.6 |

---

### 1.3 P2 — 机会主义偿还（随任务带走）

沿用 v1.0 的 P2-1 ~ P2-25，另补 6 项。

**沿用项（摘要）**：P2-1~~P2-6（重复实现：`Truncate` 三份、终态集合三份、失败分派状态机逐字重复、`Clone` 六份手写）、P2-7（`TaskItem : INotifyPropertyChanged` 领域层被 UI 契约侵入）、P2-8（`FakeClock` 进生产程序集）、P2-9（40+ 配置项 God Config，`Clone()` 靠 JSON 往返）、P2-10~~P2-15（UI 一致性：硬编码色值绕过 token、无 `x:DataType` 反射绑定、按中文字面量着色、手写 DTO 编辑器）、P2-20~P2-25（测试分层缺 `Trait`、Flaky 三件套、`*.xlsx` 未标 binary、性能项、死代码）。

**本版新增项**

| ID        | 位置                                                                                                                                                                                           | 债务                                                                                                                                                                                                                                                     |
| --------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **P2-26** | 全仓 147 处中文注释（`二期` / `暂不支持` / `遗留` / `尚未`）                                                                                                                                                    | **计划性延期未进台账**，且已与实现脱节：`Core/Domain/TaskItem.cs:10` 仍写"一期**不实现** workflow/depends_on/max_loops"，但 `Domain/WorkflowTypes.cs`(512 行)、`Scheduling/DagScheduler.cs`、`BatchEngine.cs` 的 DAG 派发**已全部实现**；`BatchEngine.cs` 中 20+ 处注释仍自称"二期扩展"。**误导性注释比无注释更危险** ✅ **已完成（2026-10-20，见 §5.7）**：逐条盘点后分三类处置——① 台账点名的两处脱节注释（`TaskItem` 扩展字段声明、`BatchEngine` 20+ 处"二期扩展"）在前序轮次（P1-1 收口 `596c165` 等）已清，本轮核实确认；② 真正仍脱节的 1 处（`SmtpNotifier.RetrieveSecret` 自称"返回占位并提示"实况已实现环境变量解析）本轮修正；③ ~100 处"二期"标签经逐条核对均为**已交付二期功能的历史标签**（与 REQ 编号并存，非延期声明），如实登记保留、不做无收益 churn；真实计划性延期 4 项全部进 §4.7 计划性延期台账（#D1~#D4），并新增 `CommentHygieneTests` 源码守卫锁死"延期不得以注释自述" |
| **P2-27** | `tests/**` 测试方法名为中文（v2.0 盘点 252/252；2026-10-20 复测 **285/494**，仍占多数）                                                                                                                      | `dotnet test --filter` / CI 报表 / 日志 diff 在非 UTF-8 环境下易碎 → **2026-10-20 评估维持「主动不偿还」（见 §5.8）**：CI 实际按 `Category`(Trait) 过滤、不按方法名；`windows-latest` + .NET 10 默认 UTF-8，三处易碎向量均无实锤，触发条件（CI 编码问题）未达成 |
| **P2-28** | `samples/`（13 文件）                                                                                                                                                                            | 双重角色：既是用户示例，又被测试当契约（`RunOptionsSerializationTests.cs:95` 断言 `samples/config.example.json` 与代码同步）→ 改示例会断测试，改代码要同步示例 **✅ 已完成（2026-10-18，见 §5.4 与阶段 1 小结）**：samples 降级为只读用户示例；测试契约（config.example.json / tasks.json / tasks.md）全部改读 csproj `Link` 指向真实 samples 的**嵌入式资源副本**（内容随 samples 同步、不依赖运行时/克隆路径），`TestSampleResources` 统一读取入口；原 `RunOptionsSerializationTests.cs:95` 不再断言磁盘 samples 与代码同步，仅断言嵌入式模板非空可解析；全部 `RepoRoot()` 磁盘路径探测已清除（顺带随 P2-21 解决） |
| **P2-29** | `_harness/`（214 文件 / 1.2G，2026-10-20 已删除）                                                                                                                                                                                                                   | 已 ignore 的 publish 产物堆（Avalonia DLL + exe），与 `samples/` 职责重叠，无人清理 ✅ **已完成（2026-10-20，见 §5.9）**：整堆清理（214 文件 / 1.2G 全部为可再生产物——`dotnet publish` 自包含输出 + MSBuild obj 中间产物，`Harness.csproj` 源文件早已不存在仅剩 obj 残骸；git 历史从未跟踪、仓内零引用）；`.gitignore` 补 `/_harness/` 显式目录级规则（此前仅靠通用 `publish/`/`bin/`/`obj/` 规则命中——「ignore 生效却无人清理」正是该债务根因），换机后也无法静默重生 |
| **P2-30** | `.editorconfig` 将 `CA1002` 设为 `none`；`NotificationDispatchers.cs` `#pragma warning disable SYSLIB0014`（过时 `SmtpClient`，注释自认"二期换 MailKit"）                                             | 规则豁免无登记、无到期日 ✅ **已完成（2026-10-21，见 §5.10）**：全仓逃逸阀盘点（1 `pragma` + 8 `[SuppressMessage]` + 1 `.editorconfig` `none` + 5 `NoWarn` 规则 + 1 钩子过滤）逐条对账，补齐 §4.7 台账四要素（发现日 / 责任人 / 到期日 / 不变量断言，"长期有效 / 二期换库时"一律折算为具体复核到期日）；**盘点发现 1 项从未登记的生产代码豁免**（Desktop `NoWarn` 的 `AVLN5001` / `AVLN3001` / `CA2007`），其中 `AVLN5001`（10 处过时 API）与 `CA2007`（1 处缺 `ConfigureAwait`）本轮**偿还并删除豁免**，余 `AVLN3001` 登记为 #10；新增 `EscapeValveHygieneTests` 守卫（站点标注 / 台账四要素与到期 / 代码↔台账双向一致 / #10 不变量），负测试实测可捕获 |
| **P2-31** | `JsonCooldownStore` / `JsonCreditsClaimStore` / `JsonConfigStore` 三份逐行同构（构造 + 默认目录 + `LoadAsync` catch(JsonException) + `AtomicFileWriter.SaveAsync`）；`"BatchAgentRunner"` 路径魔法串在 **9 处**硬编码 | 改一处必漏改两处 ✅ **已完成（2026-10-21，见 §5.11）**：① 抽 `JsonStoreBase<T>`（目录解析 + 建目录 + 文件名拼接 + 「文件缺失 / `JsonException` / 反序列化为空」三条回落 + `AtomicFileWriter` 原子保存），派生类只保留 `CreateEmpty()` 与可选的 `NormalizeAsync()` 自愈/迁移钩子；② 9 处魔法串收口为 `AppPaths`（`Root`/`ConfigDirectory`/`TemplatesDirectory`/`VaultDirectory`/`RunsDirectory`/`LogsDirectory`/`MockLiveDirectory`），`AppPaths.cs` 为应用名字符串字面量的唯一出现位置；③ 新增 `AppPathsHygieneTests` 守卫（魔法串清零 + 样板清零 + 派生目录不脱根）与 `JsonStoreBaseTests` 行为不变量 12 项。**行为零变化**（三份仓储对外契约与落盘字节不变），故用户 config.json / cooldowns.json / credits-claims.json 无需迁移 |

**P2 中三项已随阶段 1「建网」提前偿还（2026-10-18，见 §5.4）**：  
P2-20（测试分层：E2E/Usability/Process 打 `Trait`，CI 快跑单元 / 慢跑集成）、  
P2-21（去 Flaky：真实进程 → `IProcessLauncher` 注入缝；samples → 嵌入式资源副本）、  
P2-28（samples 降级为只读示例，测试契约改读嵌入式副本，内容经 csproj `Link` 随 samples 同步）。

**P2-26 随阶段 4「持续化」开轮偿还（2026-10-20，见 §5.7）**：误导注释清算 + 计划性延期台账（§4.7 #D1~#D4）+ `CommentHygieneTests` 守卫。

**P2-29 随阶段 4「持续化」顺手清理（2026-10-20，见 §5.9）**：`_harness/` publish 产物堆整堆删除（214 文件 / 1.2G）+ `.gitignore` 显式目录级规则防复发。

**P2-30 随阶段 4「持续化」偿还（2026-10-21，见 §5.10）**：逃逸阀台账四要素补齐 + 未登记的生产代码豁免归位（偿还 2 项）+ `EscapeValveHygieneTests` 守卫。

**P2-31 随阶段 4「持续化」偿还（2026-10-21，见 §5.11）＝ ARCH P-07 ✅ 收口**：三份 `Json*Store` 抽 `JsonStoreBase<T>` + 9 处路径魔法串收口 `AppPaths` + `AppPathsHygieneTests` 守卫（P-07 收口轮扩为 4 项）。

**P2 持续化偿还第二批（2026-10-21 后 ~ 本次，补记 + 收口）**：

- **P2-1~P2-8 / P2-13 / P2-15（2026-10-21 后提交，本文档滞后补记，见 §5.12）**：重复实现收敛——P2-1~P2-6 手写 `Clone` → `record with`、依赖判定 / 文本截断 / 终态集合各收单一事实来源；P2-7 `INotifyPropertyChanged` 解耦、P2-8 `FakeClock` 迁出生产；P2-13 按枚举着色；P2-15 五编辑器 → `FieldEditor<T>`（P-09 收口轮补齐：时段起止一并泛型化 + 删掉 `StartText`/`EndText` 裸字符串桥接 + 视图侧统一呈现校验反馈，见 §5.14）。
- **P2-23 / P2-24 / P2-25（2026-10-21 后提交，见 §5.12）**：仓库卫生（`*.xlsx` 标 binary）/ 性能（日志批量落盘、O(n²) Tail 消除）/ 死代码删除。
- **P2-10 / P2-11（本次收口，见 §5.13）**：硬编码字面色全部经 `AppBrushes` 单一入口 + `AppStyles.xaml` 双主题 token（`DynamicResource`）；代码侧 11 处 + XAML 侧 41 处清零。
- **P2-12（本次增量，见 §5.13）**：`LoginView` / `SetupWizardView` 根节点加 `x:CompileBindings` + `x:DataType`（**不翻转全局默认**）；全量翻转待所有视图补齐 `x:DataType` 后单独排期。
- **P2-9（✅ 已偿还，2026-10-21，见 §5.15）＝ ARCH P-09 ✅ 收口**：v1.0 记载的 `RunOptions.Clone()`「JSON 往返」已在 v2.0 重构中**消除**（当前 `RunOptions` 无 `Clone` 方法）；剩余「God Config 40+ 配置项分组」（原 §4.7.1 计划性延期 **#D5**）本轮收口——`RunOptions` 由 363 行单一大类按 12 配置域横向切片为 `Core/Options/RunOptions.01~11-*.cs`（本体 369→18 行，切片各 ≤49 行），**类型面 / 属性名 / `[JsonPropertyName]` 键名 / 落盘字节全部不变**（切片文件名两位序号 = JSON 落盘属性顺序）。防回潮由 `RunOptionsContractTests` 5 项（顺序 / 往返 / 前向兼容 / snake_case 命名 / 派生属性只读）+ `RunOptionsHygieneTests` 3 项（须按配置域切片不得回缩 / 切片行数上限 / 禁 `Clone`·`Copy`·`Snapshot` 成员复现）锁死。**#D5 已结项**。
- **P2-22（已解决）**：`models.yaml` 已于 P0-14 `git rm --cached` + gitignore，当前零代码引用，本地 0 字节残骸已随本轮清理。

---

## 2. 治理路线图

原则不变：**先止血 → 再建网 → 再动骨 → 持续化**。本版新增「阶段 −1」，并把 A/B 两组 P0 分开排。

### 阶段 −1：当天完成（< 1 人日，B 组全部）

不占用迭代容量，因为**这些不是重构，是关掉正在漏水的龙头**。

1. **P0-11** 提交规范：commit-msg 钩拒绝空消息 + 提交模板（正文必填"改了什么/为什么"）
2. **P0-13** `.gitignore` 改目录级规则（`.workbuddy/`、`.playwright-cli/`、`*.backup-*`、`TaskList/`），`git rm --cached` 掉已在库产物
3. **P0-14** 清理 10 项误提交产物（`TaskList/tasks.xlsx`、`models.yaml`、`.tmp_reflect.ps1` 等）
4. **P0-15** `Directory.Build.props` 落地 `EnforceCodeStyleInBuild` / `AnalysisLevel`（先 warning 不阻断）
5. **P0-16** 建最小 CI：`build --no-incremental + test`，**只报告不阻断**（关键：必须 `--no-incremental`，否则复现 P0-12 的 0 警告假象）

> 顺序决策：先做 1–3（当天），再做 4–5（2–3 天）。**在 CI 用全量基线之前，任何"警告清零"的说法都不可信。**

> ✅ **阶段 −1 已于 2026-10-17 完成**（P0-11 / P0-13 / P0-14 / P0-15 / P0-16 全部落地，不占用迭代容量）。详见 §5 执行记录。

### 阶段 0：止血（第 1 个迭代，A 组 10 项）

> ✅ **阶段 0 已于 2026-10-17 完成**（P0-1~P0-10 + P0-17 全部落地，验证见 §5.2；全量测试 325 通过 / 0 失败）。

9 项互不耦合、可并行、可独立回滚，是整份清单中性价比最高的部分。

```
P0-1 成本封顶（1 行 + 1 不变量单测）
P0-8 MarkdownInPlaceWriter 改原子写
P0-6/P0-7 UI 线程收敛（EngineHost 统一 Post 后广播，一次性解决 6 处跨线程）
P0-5 fallback 累计加锁
P0-4 持久化单写者化
P0-9 凭证删除前备份 + 失败回滚
P0-10 自动关机可撤销对话框
P0-2 进程输出排空（仅做「回调 EOF + TCS 门」，不做 P1-13 的 KillTree 重写）
P0-3 命令注入：改 ProcessStartInfo.ArgumentList + 输入校验
P0-17 Desktop 补测试引用 + 首批冒烟（与上面并行，为阶段 2 铺路）
```

### 阶段 1：建网（第 1–2 个迭代，与阶段 0 尾部并行）

**在动 P1-1 的骨架之前先造安全网**——没有网就拆 `BatchEngine`，等于盲拆。

1. 测试分层（P2-20）：E2E/Usability 打 `Trait`，CI 快跑单元、慢跑 E2E
2. 去 Flaky（P2-21）：真实进程→`IProcessLauncher` 注入、真实时钟→`IClock`、samples→嵌入式资源（顺带解决 P2-28）
3. 补高危盲区（P1-15）：**优先 `XlsxTaskWriter`（唯一二进制原子写路径）与 `CliCommandBuilder`（注入点）**，再补 `DangerousOperationGuard`
4. 追加**不变量测试**：为 P0-1 等「一眼看不出是错的」缺陷补断言式测试
5. 清存量警告（P0-12）：先清 36+ 个 CA2007 与 AVLN5001，**再**分批开 `TreatWarningsAsErrors`
6. P1-17 引入 CPM + 删无效依赖；P1-18/P1-20 单行修复顺手带走
7. 格式门禁（§4.1）：`pre-commit` 钩 `dotnet format --verify-no-changes` + `.editorconfig` 声明 `end_of_line=lf` + `.gitattributes` 锁 `eol=lf`（§4.1 承诺「阶段 1 起」，2026-10-18 落地）

> ✅ **阶段 1 已于 2026-10-18 完成**（6 项全部落地 + §4.1 格式门禁，验证见 §5.4：全量构建 0 警告 / 0 错误且 `TreatWarningsAsErrors=true`，全量测试 **345 通过 / 0 失败**，`dotnet format` 基线 0 问题）。至此 **P0 清单 17 项全部清偿**。

### 阶段 2：动骨（第 2–4 个迭代）

严格按依赖顺序，**期间冻结新功能**（§4.6 债务预算）：

```
P1-3 提 IBatchEngine 接口 + 窄化构造      ← 必须先做，否则无法增量验证      ✅ 已完成（2026-10-19）
   ↓
P1-4/P1-5 引入 DI + IEngineFactory（解除 UI→Infrastructure 倒置）        ✅ 已完成（2026-10-19 提交 `5d0fb14`/`192eec6`；同日收口：三处残留装配收回组合根 + §4.3 IL 扫描断言落地，见 §5.5）
   ↓
P1-9  IProcessRunner 抽离（把进程 IO 赶出 Core）                          ✅ 已完成（2026-10-19）
P1-10 引擎内 IO 全部走 ITaskFileStore / IFileSystem 端口                      ✅ 已完成（2026-10-19）
P1-16 取消 CredentialLocationRegistry 静态覆写                            ✅ 已完成（2026-10-19）
   ↓
P1-1  BatchEngine 按 6 协作者拆分（strangler fig，见 §3.4）              ✅ 已完成并收口（2026-10-19；同日晚补齐产物① ITaskDispatcher 白箱单测 19 项，六个协作者均有白箱覆盖）
   ↓
P1-2  状态机外置 + 内拆（1790708）                                         ✅ 已完成（2026-10-19 外置 e7c9eb9；2026-10-19 内拆；其后 ARCH P-03 于 2026-10-21 在其上完成策略对象化——`TaskRunner` 的降级/冷却/兜底/免费时段判定下沉为 `DegradationPolicy`/`CooldownPolicy`/`FallbackPolicy`/`FreeTimeWindowPolicy` 四个纯函数式策略对象、状态机本体移入 `TaskStepExecutor`，`TaskRunner` 收口至 76 行薄 facade，见 ARCH §6.2）
   ↓
P1-19 Desktop 补测试 + 契约收敛                                            ✅ 已完成（2026-10-19，见 §5.5）
```

> ✅ **阶段 2 已于 2026-10-18 ~ 2026-10-19 完成**（P1-3/4/5/9/10/16、P1-1 拆 6 协作者、P1-2 全部提交；P1-1 于 2026-10-19 晚收口——产物 ① `ITaskDispatcher` 白箱单测 19 项补齐，六个协作者均有白箱覆盖；P1-19 于 2026-10-19 收口：Avalonia headless 测试基座 + `UiThread` 线程收敛契约 + churn 热点 VM 契约测试，Desktop 测试 39→54）。执行记录与验证口径见 §5.5。P1-3 为本轮治理重点：提 `IBatchEngine` 目标接口 + 构造窄化 + 配套 §4.3 架构断言（`Desktop` 除组合根 `EngineFactory` 外不得直引 `BatchEngine` 具体类型）+ `BatchEnginePhase2Tests` 契约测试。

**护栏**（每一步都要满足）：

- 保持 `BatchEngine` 外壳存在（先转门面，最后再删），确保每步可编译、现有全量测试通过
- 每步结束跑 `git diff --stat`，**单步 > 400 行则回退重切**（说明切得不够细）
- 每步必须有提交信息（阶段 −1 已强制）

### 阶段 3：持续化（第 4 个迭代起，长期）

1. 机会主义偿还 P2 各项（§3.5）
2. ~~阶段 2 拆出的 5 个协作者各自补齐单测，逐步摆脱对 `BatchEngine` 黑箱测试的依赖~~（2026-10-19 晚已完成：六个协作者均有白箱单测——② `TaskRunnerTests`(10)、③ `FallbackBillingTests`(10)、④ `GitWorkflowTests`(10)、⑤ `BatchCollaboratorTests`(12)、⑥ `PreflightTests`(7)、① `TaskDispatcherTests`(19，最后补齐)；后续重点转为按协作者契约深化场景覆盖，而非从零补测试）
3. 台账月度复核，按 §4.7 看趋势而非绝对值

### 阶段 4：P2 机会主义偿还（2026-10-20 起，随任务带走）

阶段 3 的「机会主义偿还」进入实际执行后的第一批，按 §3.6 Boy Scout 边界**单独 `chore(debt):` 提交**：

```
P2-26 误导性注释清算 + 计划性延期台账（§4.7.1）+ CommentHygieneTests 守卫   ✅ 2026-10-20（§5.7）
P2-27 中文测试方法名 评估：维持「主动不偿还」，升级触发器已写入 §5.8          ✅ 2026-10-20（§5.8）
P2-29 _harness/ publish 产物堆清理 + .gitignore 显式规则防复发                ✅ 2026-10-20（§5.9）
P2-30 逃逸阀台账四要素补齐 + 未登记的生产代码豁免归位 + EscapeValveHygieneTests ✅ 2026-10-21（§5.10）
P2-31 三份 Json*Store 抽 JsonStoreBase<T> + 9 处路径魔法串收口 AppPaths + AppPathsHygieneTests ✅ 2026-10-21（§5.11；= ARCH P-07 收口，守卫 3→4 项）
P2-1~P2-8 / P2-13 / P2-15 重复实现收敛（Clone/依赖判定/截断/终态/INotifyPropertyChanged/FakeClock/枚举着色/编辑器） ✅ 2026-10-21 后提交（§5.12）
P2-23 / P2-24 / P2-25 仓库卫生 / 性能 / 死代码                                  ✅ 2026-10-21 后提交（§5.12）
P2-10 / P2-11 硬编码字面色经 AppBrushes + AppStyles token 双主题化               ✅ 本次（§5.13）
P2-12 LoginView/SetupWizardView 增量编译绑定（x:CompileBindings + x:DataType）     ✅ 本次（§5.13，全量翻转待排期）
P2-22 models.yaml 残骸清理（P0-14 已 gitignore + 零引用）                        ✅ 本次（§5.13）
P2-9 God Config 分组 按 12 配置域横向切片（369→18 行，JSON 契约零变化）+ 禁 JSON 往返克隆守卫    ✅ 本次（§5.15；= ARCH P-09 收口，#D5 结项）
P2-15 编辑器泛型化收口：时段起止收敛 FieldEditor<T> + 删裸字符串桥接 + 视图校验反馈统一           ✅ 本次（§5.15；= ARCH P-09 收口）
P2-14 维持主动不偿还
```

> 下一批候选：P2-12 全量编译绑定翻转（待所有视图补齐 `x:DataType`）、D5（`git filter-repo` 历史净化，高风险单独评审）。

---

## 3. 偿还策略：重构 / 重写 / 隔离

### 3.1 决策判据（先回答这 4 问，再选策略）

| 判据                          | 指向                         |
| --------------------------- | -------------------------- |
| ① 组件是否承载**独有且未被文档覆盖的领域知识**？ | 是 → **禁止重写**，只能重构          |
| ② 组件是否有测试覆盖，或能否在改动前低成本补上？   | 否 → **先补测试，否则不得动**         |
| ③ 组件是被多处依赖，还是单点/窄接口调用？      | 单点 → 可重写；多处 → 只能重构或隔离      |
| ④ 缺陷是局部的还是设计级的？             | 局部 → 重构；设计级**且无领域知识** → 重写 |

### 3.2 重写的准入门槛（三条必须全满足）

重写是最贵且最易失败的选择，本项目**默认不选**。准入：

1. 判据 ①②③④ 全部指向重写（尤其是 ①必须为"否"）
2. 替换前后**行为等价可被测试锁定**（先有测试，再重写）
3. 存在**平台原生替代品**，且重写后代码量净减少

当前**仅 4 项通过准入**：

| ID    | 对象                               | 理由                                                                  |
| ----- | -------------------------------- | ------------------------------------------------------------------- |
| P1-13 | `CliProcessRunner.KillTree`      | 依赖外部 `taskkill`、同步阻塞、不可移植、无领域知识；`Kill(entireProcessTree:true)` 原生替代 |
| P0-2  | `CliProcessRunner` 输出读取段         | 「轮询 + WaitForExit 后立即读」模型本身错误，局部替换为「回调 EOF + TCS」                   |
| P2-6  | `WorkflowTypes.cs` 6 个手写 `Clone` | 纯样板，改为 `record with`，消除"改字段必漏改"                                     |
| P2-15 | `ConfigEditors.cs` 5 个编辑器        | 纯样板三段式，改为泛型 `FieldEditor<T>` + 统一校验反馈                               |

### 3.3 明确禁止重写清单（避免团队反复讨论）

| 对象                    | 禁止理由                                                                   |
| --------------------- | ---------------------------------------------------------------------- |
| **`BatchEngine`**     | 持有「降级矩阵 / 冷却 / 兜底计费 / 时段窗口」等**未被文档完整覆盖的业务规则**，重写必然丢语义。只能 strangler fig |
| `RunOptions`          | 40+ 配置项是产品契约，重写 = 破坏用户配置兼容性                                            |
| `FileCredentialVault` | 凭证格式与迁移路径含隐性兼容约束                                                       |
| Desktop 整体            | 5,411 行零测试，重写等于丢弃全部已知 UI 行为。**先补测试再谈结构**                               |

### 3.4 重构主路径：`BatchEngine` 的 strangler fig 拆法

保留外壳 → 职责逐个外迁 → 外壳转门面 → 最后删除。每步可编译、可测试、可回滚。

| 步 | 拆分产出                           | 承接的原职责                             |
| - | ------------------------------ | ---------------------------------- |
| 1 | `ITaskDispatcher`              | 派发/并发控制、DAG 判定                     |
| 2 | `ITaskRunner`                  | 单任务状态机（重试/退避/降级/超时）                |
| 3 | `IFallbackBilling`             | 兜底模型选择与计费（**承载 P0-1/P0-5 的成本不变量**） |
| 4 | `IGitWorkflow`                 | Git 建分支/开 PR、diff 采集、Markdown 回写   |
| 5 | `INotifier` + `IReportBuilder` | 通知、报告/用量/时间线聚合                     |
| 6 | `IPreflight`                   | 启动预检                               |

过渡期约束：**新代码一律不得引用 `BatchEngine` 内部成员**，只允许走 `IBatchEngine`（用 §4.3 的架构断言强制）。

### 3.5 隔离（不消除债务，只在周围筑墙）

适用于「改动风险 >> 收益」的部分。三种形态：

| 形态       | 做法                     | 适用                                                                                                                                      |
| -------- | ---------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| **接口隔离** | 用接口/门面包住实现，禁止新代码直接引用内部 | P1-1 过渡期的 `BatchEngine`                                                                                                                 |
| **防腐层**  | 在脏边界加适配层，阻止契约向外扩散      | P2-7（`TaskItem : INotifyPropertyChanged` → Desktop 侧加 `TaskItemViewModel`，禁止直接绑定 `TaskItem`）；P2-10/P2-11（token 与旧色值间加映射层，禁止新 XAML 写字面色） |
| **输入隔离** | 切断对不稳定外部输入的依赖          | P2-19/P2-28（测试改读**嵌入式资源副本**，samples 目录改为只读示例）                                                                                           |
| **守卫隔离** | 危险入口加静态守卫，先阻止恶化再谈根治    | P1-16 过渡期（`SetLiveRootOverride` 重复覆写直接抛，待 DI 落地后取消）                                                                                     |

### 3.6 机会主义偿还（Boy Scout Rule）边界

**允许**：改某文件时顺手清掉**同文件内**的 P2 项（魔法值、重复 `Truncate`、路径硬编码、过时注释）。

**不允许**：

- 顺手改**其他文件**（PR diff 失控，评审失效）
- 顺手改**正在被 P0/P1 修复的同一区域**（冲突风险）

落地方式：这类清理**单独提交**，前缀 `chore(debt):`，与功能提交严格分离。

### 3.7 主动不偿还（记录但不动）

| 对象                                     | 理由                        |
| -------------------------------------- | ------------------------- |
| `QuotaErrorClassifier` 大小写优化（P2-24 部分） | 性能充足，改动收益低于回归风险；出现实测证据才动  |
| 响应式布局（P2-14）                           | 桌面工具固定窗口，当前无多形态需求         |
| `SessionPolicy` 等疑似预留扩展点的死代码（P2-25 部分） | 需先确认是预留还是废弃               |
| 中文测试方法名（P2-27）                         | 现状一致且可读；仅在 CI 出现编码问题时批量处理 → **2026-10-20 评估维持不偿还**：CI 按 `Category`(Trait) 过滤、不按方法名；`windows-latest` + .NET 10 默认 UTF-8，`--filter`/报表/日志 diff 三处易碎向量均无实锤，触发条件未达成（见 §5.8） |

---

## 4. 防新增机制（本方案的重心）

存量修得再干净，没有这层，下个季度回到原点。

### 4.1 第一道墙：提交门禁（写不进）—— 本版新增

针对实测的「10/12 提交信息为空」：

- `commit-msg` 钩：~~拒绝空消息、拒绝纯标点、要求正文含「改了什么 / 为什么」~~ ⚠️ **已于 2026-10-21 按仓库所有者明确要求永久停用**（`.githooks/commit-msg` 现为 no-op 存根，禁止恢复；提交信息约束不再由机器强制，改由 CODE-REVIEW §5.2 人工把关）
- `pre-commit` 钩：`dotnet format --verify-no-changes` ✅ **阶段 1 已完成（2026-10-18，`.githooks/pre-commit`）**。落地细节与已知 SDK 限制见 §5.4「格式门禁」行与 §4.7 台账 #9（CHARSET 假阳性过滤）。⚠️ **该钩子亦已于 2026-10-21 停用**（本地钩子不随 clone 继承、且所有者要求不强制提交信息类约束）；格式门禁的机器强制已改由 **CI 承载**（ARCH P-11，2026-10-22：`scripts/ci/Assert-Format.ps1`，过滤口径与 #9 一致），不再依赖开发者本地配置。
- 提交模板 + `CODE-REVIEW §5.2` 的单 PR 单一关注点约束

> ⚠️ **门禁回归与恢复记录（2026-10-19）**：`9a0069c`（积分领取 `force` 改动，29 文件 / +801 行）**误删了 `.githooks/pre-commit`**（提交信息只描述积分变更，未提及钩子），格式门禁随即静默失效，并已产生实际漂移（`WorkspaceConcurrencyTests.cs` 导入顺序）。本轮（P1-4/P1-5 收口轮）从 `8e060c5` **原样恢复钩子**并单独 `chore(debt)` 提交，漂移随手 `dotnet format` 清理。教训：① 门禁文件（`.githooks/`）的增删必须走 §4.6/§4.7 登记，不得随功能提交夹带；② 大改动提交前应核对 `.githooks/` 清单；③ 「文档声称已落地」≠「文件还在」——本轮文档校正到代码真值即发现此项。

### 4.2 第二道墙：编译期门禁（编不出）

```xml

<PropertyGroup>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <AnalysisLevel>latest-recommended</AnalysisLevel>
  <AnalysisMode>Recommended</AnalysisMode>
  
  
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup>
```

配套：

- 引入 `Directory.Packages.props`（CPM），包版本集中（P1-17）✅ 阶段 1 已完成（NU1008 构建错误结构性防版本漂移）
- **CI 必须用 `--no-incremental` 全量基线**，否则复现 P0-12 的 0 警告假象
- ~~分批阻断顺序：Core → Agents → Infrastructure → Desktop~~（2026-10-18：存量一次性清零后全量开启，分批前提消失，见 §4.9 注）

### 4.3 第三道墙：架构约束自动化（过不了测试）

用 ArchUnitNET 把分层规则固化为**测试**，而不是文档约定：

| 断言                                                                       | 防什么                | 落地状态（2026-10-19）                                                                               |
| ------------------------------------------------------------------------ | ------------------ | ---------------------------------------------------------------------------------------------- |
| `Core` 不得引用 `Infrastructure`/`Agents`/`Desktop`                          | 防 P1-4/P1-9 类倒置复发  | ✅ `LayeringRulesTests`（Core.Tests，程序集引用反射）                                                          |
| `Core` 命名空间下不得出现 `System.Diagnostics.Process`                            | 防 Core 再起进程        | ✅ `LayeringRulesTests`（Core.Tests，成员 + IL 局部变量反射；P1-9 落地）                                                  |
| `Core`/`Infrastructure` 不得出现 `Avalonia.*`                                | 防 P2-7 类 UI 契约反向侵入 | ⏳ 可按程序集引用低成本实现，留待 ArchUnitNET 批次（§4.9 迭代 2/3）                                                        |
| `Desktop` 不得直接 `new` Infrastructure 具体类型（须经接口）                           | 防 P1-4 复发          | ✅ `LayeringRulesTests`（Desktop.Tests，IL 扫描 `newobj`/`newarr` 并解析 token；2026-10-19 P1-4/P1-5 收口时落地）                  |
| `Desktop` 除组合根 `EngineFactory` 外不得直接引用 `BatchEngine` 具体类型（须走 `IBatchEngine`） | 防 P1-3 过渡期「新代码直引 BatchEngine 内部」复发 | ✅ `LayeringRulesTests`（Desktop.Tests，成员 + IL 局部变量反射；P1-3 收口）                                          |
| `Domain` 下不得出现 `INotifyPropertyChanged`                                  | 防 P2-7 扩散          | ⏳ 留待 ArchUnitNET 批次（§4.9 迭代 2/3）                                                                    |
| 生产程序集不得出现 `FakeClock`/`Mock*` 类型                                         | 防 P2-8 测试替身进生产     | ⏳ 留待 ArchUnitNET 批次（§4.9 迭代 2/3）                                                                    |
| `Catch (Exception)` 占比不得上升（基线 42/79 = 53%，2026-10-18 复测；盘点时 37/69 = 54%） | 防吞异常继续扩散           | ⏳ 需计数基建；当前靠 PR 评审清单人工兜底（§4.5）                                                                          |
| `src` 注释不得自述计划性延期 / 能力未实现（"不实现 / 未实现 / 暂不支持 / 待实现"；"占位并/值/实现"等桩声明；"二期 + 扩展/后续/再换/换成"） | 防 P2-26 误导注释复发：延期只进 §4.7.1 台账，注释只描述当前实现 | ✅ `CommentHygieneTests`（Core.Tests，源码词法扫描注释、跳过字符串字面量；2026-10-20 P2-26 收口时落地；负测试实测可捕获原始误导注释） |
| 每个门禁例外（`#pragma` / `[SuppressMessage]` / `.editorconfig` `severity=none` / `NoWarn` / 钩子过滤）必须就近标注「逃逸阀台账 #N / 到期日 / 责任人」，且台账四要素齐全、到期日未过期、代码↔台账双向一致 | 防 P2-30「豁免无登记、无到期日」复发：登记了却无到期日 = 无人复核，§4.7「到期自动升级 P1」形同虚设 | ✅ `EscapeValveHygieneTests`（Core.Tests，2026-10-21 P2-30 收口时落地；负测试实测可捕获：无标注 `pragma`、无标注 `.editorconfig` 条目、过期到期日、悬空台账 ID 四类） |
| `src` 内除 `AppPaths.cs` 外不得内联应用名路径魔法串；三份 `Json*Store` 必须继承 `JsonStoreBase<T>` 且不得自带「建目录 / `JsonSerializer` / `AtomicFileWriter`」样板；`AppPaths` 全部派生目录必须以应用根为前缀 | 防 P2-31「三份仓储逐行同构 + 路径魔法串 9 处」复发：新数据目录只在 `AppPaths` 登记，加载/落盘样板只在基类 | ✅ `AppPathsHygieneTests`（Core.Tests，2026-10-21 P2-31 收口时落地；负测试实测可捕获：派生仓储内联魔法串、样板回潮两类） |

> 「Desktop 不得直接 new」断言的实现口径（2026-10-19）：用反射自建最小 IL walker（`OpCodes` 反射建查表，逐指令步进），扫描 Desktop 程序集全部方法体（含构造器 / 属性访问器 / 编译器生成的闭包与异步状态机）中的 `newobj` / `newarr`，解析元数据 token 得目标类型；`Desktop.Composition` 命名空间（组合根）为唯一豁免。**按规则本意排除委托实例化**：捕获型 lambda 会被编译器转换为 `newobj`（如 `CredentialVaultHost.GuidedLoginAsync` 把校验回调传给 `RunAsync`），源码并无 `new`，且回调委托类型是协作者签名强制的契约、无法按「须经接口」替换——规则目标是「UI 不得实例化 Infrastructure/Agents 实现类」，不覆盖编译器产物。

### 4.4 第四道墙：复杂度门禁（CI 不过）

**阈值先按现状标定到「不阻断现有代码中的合理部分」，再逐季度收紧**。一上来设理想值会导致团队习惯性 `// NOLINT` 越过，门禁形同虚设。

| 指标     | 建议阈值（含宽容量） | 当前最坏值                   | 拦截目标            |
| ------ | ---------- | ----------------------- | --------------- |
| 单文件行数  | ≤ 800      | <800（原 **1065 行 `ConfigViewModel.cs`** 已随 **P-04 ✅ 收口** 分解为 11 个子编辑器 VM + 共享校验器 + `ConfigSaveCoordinator`，facade 收口至 **366 行**（守卫测试锁死，见 ARCH §6.2）；原 **983 行 `MonitorViewModel.cs`** 与 **751 行 `LoginViewModel.cs`** 已随 **P-06 ✅ 收口**（2026-10-21）分解为 Monitor/Login 两组协作者 + 行视图模型独立文件，facade 分别收口至 **388/280 行**（守卫测试锁死，见 ARCH §6.2）；`BatchEngine.cs` 经 P1-1/P-01/P-02 收口至 400 行；最大 VM 均远低于 ≤800 阈值，上帝类行数风险暂时解除） | 防下一个 God Object |
| 单方法行数  | ≤ 60       | 249（`ExecuteTaskAsync`，P1-2 内拆前；2026-10-19 内拆 1790708 后压至 ≤60，该异常已关闭） | 防超长方法           |
| 圈复杂度   | ≤ 15       | >35（`ExecuteTaskAsync`，P1-2 内拆前；内拆后 ≤15，该异常已关闭）               | 防不可测分支          |
| 构造参数个数 | ≤ 7        | 14（`BatchEngine`）       | 防依赖爆炸           |
| 类公开成员数 | ≤ 30       | 60+（`BatchEngine`）      | 防 God Object    |

### 4.5 第五道墙：PR 评审清单（合不进）

在 `docs/CODE-REVIEW-代码审查标准与流程.md` 追加，PR 模板强制勾选：

- [ ] 本次改动是否让任何文件的行数/复杂度上升？若是，说明理由
- [ ] 是否新增 `catch { }`，或 `catch(Exception)` 后仅记 WARN？（当前 42 处，是排障困难主因）
- [ ] 是否新增可变 `static` 状态？（P1-16 类）
- [ ] 是否新增 `.GetAwaiter().GetResult()` / `.Result` / `.Wait()`？（P1-6 类）— 2026-10-20 起已由 `LayeringRulesTests.Desktop程序集_不得存在同步阻塞异步结果的调用` 自动把关，新增即测试失败
- [ ] 后台线程 `await` 后是否直接改了 UI 集合？（P0-6 类）
- [ ] 是否新增文件读写而未走 `AtomicFileWriter`？（P0-8 类）
- [ ] 是否拼接命令行字符串而非 `ArgumentList`？（P0-3 类）
- [ ] 是否新增硬编码色值/魔法数字/绝对路径，而非引用 token/常量？路径一律走 `AppPaths`，新数据目录须先在 `AppPaths` 登记（P2-31 类）— 2026-10-21 起已由 `AppPathsHygieneTests` 自动把关，`src` 内联应用名路径魔法串即测试失败；新单文件 JSON 仓储须继承 `JsonStoreBase<T>`，不得自带「建目录 / `JsonSerializer` / `AtomicFileWriter`」样板
- [ ] 是否以注释自述计划性延期 / 能力未实现（"二期扩展""暂不支持"等）？延期一律登记 §4.7 台账后在注释引用台账 ID（P2-26 类）— 2026-10-20 起已由 `CommentHygieneTests` 自动把关，新增即测试失败
- [ ] 是否新增门禁例外（`#pragma warning disable` / `[SuppressMessage]` / `.editorconfig` `severity=none` / `NoWarn` / 钩子过滤）？例外一律先登记 §4.7 逃逸阀台账四要素，再在就近注释写明「逃逸阀台账 #N / 到期日 / 责任人」（P2-30 类）— 2026-10-21 起已由 `EscapeValveHygieneTests` 自动把关，未登记 / 无到期日 / 到期未清理即测试失败
- [ ] 新增的高危路径（凭证/进程/Git/安全拦截）是否带测试？
- [ ] 提交信息是否说明「改了什么 / 为什么」？（P0-11 类，钩子已强制）

**吞异常专项**：允许 `catch` 后记日志，但**必须保留原异常对象**且必须区分「可容忍降级」与「应中止」；空 `catch { }` 一律拒绝。

### 4.6 第六道墙：债务预算（排不进）

- 每迭代预留 **20% 容量**用于债务偿还，**不可被功能需求挤占**
- 阶段 2（动骨）期间提升至 **50%**，并冻结新功能
- **台账字段强制四项**：发现日 / 责任人 / 到期日 / 不变量断言（缺一不得登记）
- 每迭代末更新台账，关闭已偿 ID，登记新发现 ID

### 4.7 逃逸阀治理（本版新增）

门禁必须允许例外，否则会被绕过。规则：

- 例外一律显式：`#pragma warning disable` / `[SuppressMessage]` / `// NOLINT` **必须附理由 + 到期日 + 责任人**（2026-10-21 P2-30 起统一为就近注释里的结构化标注「逃逸阀台账 #N / 到期日 YYYY-MM-DD / 责任人 X」，由 `EscapeValveHygieneTests` 机器校验）
- CI 统计**逃逸数**并纳入看板（2026-10-21 P2-30 复核后基线：`#pragma` 1 处、`[SuppressMessage]` 8 处（CA1859×7 + CA1720×1）、`.editorconfig` 规则置 `none` 1 处（CA1002）、`NoWarn` 规则 4 项（CA1707·CA2007·CA1861 测试项目 / AVLN3001 仅 Desktop）、钩子诊断过滤 1 处（pre-commit CHARSET）= **台账 9 条**；P2-30 偿还 2 条（见 §5.10），P-12 偿还 1 条——`NoWarn CS1591` 于 2026-10-22 删除、编号 #3 退役，见 §5.17）
- 到期未清理的逃逸，自动升级为该迭代的 P1（2026-10-21 起：到期日过期即 `EscapeValveHygieneTests` 失败，无需人工巡台账）

**逃逸阀台账（2026-10-22 ARCH P-12 后更新，共 9 项；#3 已偿还退役、编号不复用）**：

> 字段强制四项（§4.6）：**发现日 / 责任人 / 到期日 / 不变量断言**，缺一不得登记。「长期有效」一律折算为**具体复核到期日**——
> 「待评估 / 二期换库时」这类无日期的到期列正是 P2-30 的债务本体（台账登记了却无人复核、也无法判断是否过期）。

| #  | 逃逸                                                          | 位置（不变量描述，不用行号）                                                                                                   | 理由（为什么必须豁免）                                                                                                                                                                                                          | 发现日          | 责任人 | 到期日          | 不变量断言（验收）                                                                                                              |
| -- | ----------------------------------------------------------- | ------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------- | --- | ------------- | --------------------------------------------------------------------------------------------------------------------- |
| 1  | `#pragma warning disable SYSLIB0014`（过时 `SmtpClient`）       | `Infrastructure/Notification/NotificationDispatchers.cs`（`SmtpNotifier.NotifyAsync` 内 `new System.Net.Mail.SmtpClient`） | .NET 仍支持 `SmtpClient`，换 MailKit 属**功能迁移**非缺陷修复；计划性延期见 §4.7.1 #D1                                                                                                                                            | 2026-10-18    | 工程  | 2026-12-31    | `SmtpNotifierCredentialResolutionTests` 2 项锁死凭据引用解析（环境变量名→取值 / 未设置→空串）；换库前后 `NotificationDispatcherTests` 必须保持绿 |
| 2  | `.editorconfig` 将 `CA1002` 置 `none`                         | `.editorconfig` 的 `dotnet_diagnostic.CA1002.severity`（原登记 `:24`，2026-10-21 实测已漂移，改用不变量描述）               | 实测开启即 **96 处违规**，全部落在 JSON 序列化契约类型的 `List<T>` 公开属性（`BatchReport` 18 / `RunOptions` 16 / `WorkflowTypes` 12 …）；改 `Collection<T>` / `ReadOnlyCollection<T>` 破坏用户 config.json 与任务模板的往返兼容 | 2026-10-18    | 工程  | 2027-01-20    | `RunOptionsSerializationTests` 与模板兼容测试锁死 `List<T>` 契约属性 JSON 往返无损；改为抽象集合类型即失败                                       |
| 4  | `NoWarn CA1707`（标识符含下划线）                                  | 两个测试 csproj                                                                                                   | 测试项目专用：「下划线 + 中文名」是本仓测试命名约定（对生产代码不适用，见 §5.8 P2-27 评估维持不偿还）                                                                                                                                     | 2026-10-18    | 工程  | 2027-01-20    | 约定型豁免：由守卫锁死登记完整性与到期日                                                                                            |
| 5  | `NoWarn CA2007`（await 须 ConfigureAwait）                     | 两个测试 csproj（**仅测试**；Desktop 生产侧的同名豁免已于 2026-10-21 P2-30 偿还，见 §5.10）                                         | 与 xUnit 自家 `xUnit1030` 直接冲突——xUnit 明确建议测试方法**省略** `ConfigureAwait`（可 bypass 并行化限制）；故阶段 1 同步删除了测试中全部 `ConfigureAwait(false)`                                                                        | 2026-10-18    | 工程  | 2027-01-20    | 约定型豁免：由守卫锁死登记完整性与到期日；且「Desktop 程序集不得新增无 `ConfigureAwait` 的 await」在移除生产侧豁免后由 `TreatWarningsAsErrors` 直接把关                        |
| 6  | `NoWarn CA1861`（常量数组提 static readonly）                      | 两个测试 csproj                                                                                                   | 测试项目专用：该规则针对生产热路径的重复分配；测试期望值数组一次性使用且内容各异，提字段损害可读性且无性能收益                                                                                                                               | 2026-10-18    | 工程  | 2027-01-20    | 约定型豁免：由守卫锁死登记完整性与到期日                                                                                            |
| 7  | `[SuppressMessage] CA1720`（`ExpertKind.Single` 含类型名）        | `Core/Domain/ExpertRef.cs`（`ExpertKind.Single` 枚举成员）                                                          | 持久化兼容：`ExpertKind` 经 `JsonStringEnumConverter` 以字符串形式随用户模板落盘，重命名破坏既有模板（同 `RunOptions` 契约保护原则）                                                                                                   | 2026-10-18    | 工程  | 2027-01-20    | 模板兼容测试锁死 `ExpertKind` 字符串序列化契约；重命名成员即失败                                                                                 |
| 8  | `[SuppressMessage] CA1859`（接口字段 / 返回改具体类型）                 | `Core/Engine/BatchEngine.cs`（6 处协作者字段）+ `Desktop/Composition/EngineFactory.cs`（1 处组合根返回类型）                    | 架构方向：接口类型是 P1-4/P1-5「UI 只依赖 Core 抽象 + DI 化」的目标形态，也是六协作者的**测试注入缝**，拒绝为微性能向具体类型回退                                                                                                            | 2026-10-19    | 工程  | 2027-01-20    | 六协作者白箱单测（`TaskDispatcherTests` 19 / `TaskRunnerTests` 10 / `FallbackBillingTests` 10 / `GitWorkflowTests` 10 / `BatchCollaboratorTests` 12 / `PreflightTests` 7）依赖接口注入替身；改具体类型即不可替换                 |
| 10 | `NoWarn AVLN3001`（XAML 资源无公开无参构造，运行时加载器不可达）           | `src/BatchAgentRunner.Desktop/BatchAgentRunner.Desktop.csproj`（`ShutdownCountdownWindow`）                       | 该窗口只由 `EngineHost` 以 `ShutdownCountdownWindow(int)` 构造、从不经 XAML URI 加载，AVLN3001 对其是**假阳性**；为消告警补无参构造会平白扩大公开面（2026-10-21 P2-30 盘点发现，此前从未登记）                                          | 2026-10-21    | 工程  | 2026-12-31    | 守卫断言「`src` 内不得出现 `new ShutdownCountdownWindow()`」：一旦新增无参构造，豁免前提消失，须先删豁免再改代码                                     |
| 11 | `[SuppressMessage] CA1001`（可释放字段类型未实现 IDisposable）               | `Core/Engine/BatchLifecycle.cs`（`BatchRunState.RunCts: CancellationTokenSource`）                                  | `BatchRunState` 持有 `RunCts`（非托管资源），但 Dispose 责任在宿主 `BatchEngine`（统一释放 `_runState.RunCts` 与 `_writeGate` 等），类型本身不应实现 IDisposable 以免双重释放；P-02 拆分产物，此前未登记                                        | 2026-10-21    | 工程  | 2027-01-20    | 白箱单测锁死 `BatchEngine.Dispose` 释放 `RunCts`；`BatchLifecycleTests` 验证生命周期内 `RunCts` 由引擎统一处置，类型不实现 IDisposable                      |

> 说明：P2-30 原登记的两项（`.editorconfig` CA1002、`#pragma` SYSLIB0014）已由本表 #2 / #1 **完整登记**（含发现日 / 责任人 / 到期日 / 不变量断言）。
> P2-30 盘点另发现 1 项**从未登记的生产代码豁免**（Desktop `NoWarn` 中的 `AVLN5001` / `AVLN3001` / `CA2007`）——其中
> `AVLN5001`（10 处过时 API：`TextBox.Watermark` → `PlaceholderText`、`Window.SystemDecorations` → `WindowDecorations`）与
> `CA2007`（1 处缺 `ConfigureAwait`）本轮已**偿还并从 NoWarn 移除**，仅 `AVLN3001` 保留并登记为 #10。
> 同轮纠正一处文档失真：§5.4 阶段 1 边界① 曾写「生产代码 0 豁免」，而 Desktop 生产项目实际存在 3 项 `NoWarn`（见 §5.10 与附录 B.3 #5）。
>
> **#3（`NoWarn CS1591`）已偿还退役（2026-10-22，ARCH P-12 / A-10）**：四个 `src` 生产工程开启
> `GenerateDocumentationFile` 并把全部公共 API 补齐 XML 文档，`Directory.Build.props` 的 `NoWarn CS1591` 已删除；
> 无文档的新增公共 API 现被 `TreatWarningsAsErrors` 直接打成构建错误（比「先 warning」更强）。编号保留、不再复用。

#### 4.7.1 计划性延期台账（P2-26，2026-10-20 建立，共 6 项）

P2-26 清算结论：**计划性延期一律进本台账，代码注释只描述当前实现**（自述延期即被 `CommentHygieneTests` 拦截）。字段遵循 §4.6 强制四项（发现日 / 责任人 / 到期日 / 不变量断言）。与上表逃逸阀的分界：逃逸阀是「门禁例外」（pragma/NoWarn），本表是「功能延期」（未做的能力）。

| ID | 延期内容                                                                   | 代码锚点（不变量描述，不用行号）                                                     | 理由 / 前置条件                                                | 发现日 / 责任人 / 到期日          | 不变量断言（验收）                                        |
| -- | ---------------------------------------------------------------------- | -------------------------------------------------------------------- | --------------------------------------------------------- | ------------------------- | ------------------------------------------------- |
| D1 | SMTP 通知凭据引用接入 `ICredentialVault`（现仅环境变量解析）；顺带 `SmtpClient` → MailKit 换库 | `SmtpNotifier.RetrieveSecret` 只经环境变量取值；`#pragma SYSLIB0014`（= 逃逸阀 #1） | 换库属功能迁移非缺陷修复；Vault 集成需凭证域稳定 API               | 2026-10-20 / 工程 / MailKit 迁移迭代或 Vault API 稳定后 | `凭据引用_按环境变量名解析取值` / `凭据引用_环境变量未设置_返回空串`（NotificationDispatcherTests） |
| D2 | 真实 CLI 适配器声明支持会话恢复（方案 b 启用）                                        | `IAgentCliAdapter.CanResume` 默认 `false`；`SessionResumePolicy` 默认关、全条件回退方案 a   | 前置验证 M0 V-8（CLI 是否支持 resume）未实测；不阻塞主线（DESIGN §13 E4 / §14 开放问题 1） | 2026-10-20 / 工程 / M0 实测轮            | `SessionResumePolicyTests` 5 项（默认关 / 无句柄 / CLI 不支持 / 跨 adapter 均回退；仅全条件满足才 Resume） |
| D3 | WorkBuddy CLI 登录态存储位置与形态确认（V-4b/V-4e）                                 | `CredentialLocationRegistry` 中 WorkBuddy 无文件型位置登记；一键登录/积分领取对 WorkBuddy 拒绝执行 | 桌面主登录态存储方式未知，需实机确认；确认前拒绝执行好过盲猜路径           | 2026-10-20 / 工程 / M0 实测轮            | `GuidedLoginFlowTests` / `VaultSwitchTests` 既有「未登记 CLIId 拒绝并提示」用例保持绿 |
| D4 | 专家参数降级：CLI 不支持专家参数时以 `ExpertRef.RolePrompt` 注入角色描述         | `RolePrompt` 字段存在但引擎未消费（预留位）                                   | 前置验证 M0 V-5（CLI 专家参数支持面）未实测；DESIGN §13.3 V-5 在册   | 2026-10-20 / 工程 / M0 实测轮            | 字段序列化契约由既有模板兼容测试锁死；启用时补「不支持 → prompt 注入」用例 |
| ~~D5~~ ✅ **已结项（2026-10-21，见 §5.15）＝ ARCH P-09 收口** | ~~`RunOptions` God Config 分组（40+ 扁平配置项拆分为子选项组）~~ | 以 `partial class` **横向切片**收口：本体 `Options/RunOptions.cs` 369→18 行，12 配置域各占 `Options/RunOptions.NN-*.cs`（≤49 行）；**不嵌套分组**故类型面 / 属性名 / `[JsonPropertyName]` 键名 / 落盘字节全部不变，既有读写点与 `config.json` 契约零迁移 | 原判「宽爆炸半径」的规避路径：嵌套分组会改 `config.json` 形状（用户配置迁移风险），横向切片则零契约变化，等价达成「God Config 不再是一坨」 | 2026-10-21 / 工程 / ~~God Config 专项迭代~~ **已结项** | 切片形态由 `RunOptionsHygieneTests` 3 项锁死（不得回缩为单一大类 / 切片行数上限 / 禁 JSON 往返克隆复现），契约由 `RunOptionsContractTests` 5 项锁死（落盘属性顺序 · 往返逐字节稳定 · 未知字段宽容 · snake_case · 派生属性只读） |
| D6 | XAML 全量编译绑定翻转（`AvaloniaUseCompiledBindingsByDefault=true`）          | `Desktop.csproj` 当前 `false`；本次（§5.13）仅对 `LoginView`/`SetupWizardView` 增量加 `x:CompileBindings` + `x:DataType` | 翻转全局默认会让所有未带 `x:DataType` 的绑定失效，回归风险极高；须先为全部视图补齐 `x:DataType`（含 `DataTemplate` 子类型） | 2026-10-21 / 工程 / 全视图 x:DataType 补齐后 | 每新增/修改视图须带 `x:CompileBindings` + `x:DataType`；既有 `LayeringRulesTests`/视图契约测试全绿 |

> 到期未推进的延期，按 §4.7 规则自动升级为该迭代 P1。D2/D3/D4 共享同一前置（M0 实测轮），建议同轮清零。D6 为 2026-10-21 收口轮新登记；**D5 已于 2026-10-21 结项（ARCH P-09 收口，见 §5.15）**，当前仅余 D1~D4 + D6。

### 4.8 度量与趋势看板

每迭代采集，**看趋势而非绝对值**：

| 指标                     | 当前基线（实测）                                                                                                                                                                   | 目标方向     |
| ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------- |
| 最大文件行数                 | **<800**（2026-10-21 **P-04 ✅ 收口**：原最大文件 `ConfigViewModel.cs` 由 1065 行分解为 11 个子编辑器 VM + 共享校验器 + `ConfigSaveCoordinator`，facade 收口至 **366 行**，由 `LayeringRulesTests.ConfigViewModel_facade文件不得超过400行_防上帝类回潮` 守卫锁死；2026-10-21 **P-06 ✅ 收口**：原 983 行 `MonitorViewModel.cs` / 751 行 `LoginViewModel.cs` 分解为 Monitor 8 协作者 + Login 5 协作者 + 行 VM 独立文件，facade 分别收口至 **388/280 行**，由 `MonitorViewModel_facade文件不得超过400行_防上帝类回潮` / `LoginViewModel_facade文件不得超过400行_防上帝类回潮` 守卫锁死；`BatchEngine.cs` 经 P1-1 拆分降至 1030，P-01 下沉 `WaitCoordinator` 后降至 953，P-02 下沉三个协作者后收口至 400 行（≤400 收口达标，守卫测试锁死，见 ARCH §6.2）；Desktop 破 800 行阈值的高危 God Object 已全部消除）                                                                                          | ↓        |
| 最大方法行数                 | **≤60**（P1-2 内拆 1790708 已完成：原 287 行 `TaskRunner.ExecuteTaskAsync` 显式状态机化，拆为 `TaskExecutionState` 状态对象 + 8 个 ≤60 行 / CC≤15 子方法，§4.4 单方法阈值达标；全局最大方法随 §4.4 季度复测重定基线，该异常已关闭）                                                                                     | ↓        |
| 全量构建警告数                | **0**（2026-10-18 阶段 1 清零：P0-12 完成，`TreatWarningsAsErrors=true` 生效，任何新增警告即构建失败；2026-10-19 P1-4/P1-5 收口后复测仍 0）                                                                             | → 0 并保持  |
| `catch (Exception)` 占比 | **src 44 处**（2026-10-19 复测，较阶段 1 的 42 略升；占比分母随 P1-1 拆分变化，待精确复算）                                                                                  | ↓        |
| 高危模块零覆盖数               | **0**（2026-10-20 P1-15 收口：阶段 1 补齐 XlsxTaskParser/Writer、CliCommandBuilder、DangerousOperationGuard；P1-14 补齐 LocalGitIntegration 子进程执行契约测试；2026-10-20 补齐最后三项 CodeBuddyCreditsApiClient / BackupManager / ShutdownService，见 §5.6） | → 0 并保持  |
| Desktop 测试数            | **168 用例**（2026-10-21 后 P2 机会主义偿还批次累计新增：P2-1~P2-8/P2-13/P2-15/P2-23~P2-25 收口轮 + P2-10/11/12 收口轮；含 `AppBrushesTests` 27 / `OnboardingViewTests` 2 / 各 VM 白箱等；ARCH P-04/P-05/P-06 收口轮新增 headless 契约测试 + XAML 绑定守卫 + facade 行数守卫；**ARCH P-09 收口轮 +6**：`ConfigEditorsHygieneTests` 3 项 + 时段行为契约 3 项；headless 基座 + 视图契约 + 架构断言持续扩展）                                                              | ↑        |
| 空提交信息数 / 迭代            | 10/12 历史；commit-msg 钩已于 2026-10-21 按所有者要求**永久停用**（no-op 存根），机器不再强制，改由 CODE-REVIEW §5.2 人工把关；**2026-10-22 复测：HEAD 起 2 个提交信息为空**（`73ed7f3`/`8f09eaa`，批量收口提交，历史包袱不再回改）                                                                                                                           | 人工把关，不强制                |
| 误提交产物数                 | 10；阶段 −1 已 `git rm --cached` 并 ignore，当前 0                                                                                                                                 | → 0 并保持  |
| 已 ignore 产物堆（P2-29）    | 曾 `_harness/` 1.2G / 214 文件（publish+bin+obj，从未跟踪、无人清理）；2026-10-20 整堆清理 + `/_harness/` 显式 ignore 规则                                                                    | → 0 并保持  |
| P0 未偿数                 | 17 → 阶段 −1 完成 5 项、阶段 0 完成 11 项、阶段 1 完成最后 1 项（P0-12），当前 **0**                                                                                                               | → 0 并保持  |
| 每迭代**新增**债务数           | 未采集（阶段 −1 起采集）                                                                                                                                                             | → 趋 0    |
| churn 热点 Top3          | `BatchEngine.cs`(4)、`RunOptions.cs`(4)、`ConfigView.axaml`(4)（**2026-10-21 ARCH P-09 收口**：后两者已由「配置域切片 / 编辑器泛型化 + 校验反馈统一 + 错误样式单一来源」拆分，热点成因消除，待下次复测重排）                                                                                                               | 热点应随拆分下降 |
| 同步阻塞调用数（P1-6 类）      | **0**（2026-10-20 归零：原 2 处 `GetAwaiter().GetResult()`——`MainViewModel.cs:20`、`EngineHost.cs:136`——经异步初始化门消除；新增 IL 架构断言 `Desktop程序集_不得存在同步阻塞异步结果的调用` 持续锁住，任何新增 `.GetAwaiter().GetResult()` / `Task.Result` / `Task.Wait()` 即构建/测试失败） | → 0 并保持  |
| 测试总数                   | **822 用例通过 / 0 失败**（2026-10-22 ARCH P-11 收口轮实跑：主测试项目 **654**（快跑 613 + 慢跑 41）+ Desktop.Tests **168**；较 ARCH P-10 收口轮 801 +21，增量来自 HEAD 中 ReasoningEfforts 特性提交（`--effort` 参数 + `CliCommandBuilderTests`），与本轮门禁改动无关；历史累计：较 P2-31 收口轮 565 起，P2 机会主义偿还批次 + ARCH P-04/P-05/P-06 收口轮累计新增 169；**ARCH P-07 收口轮 +1**：`AppPathsHygieneTests` 第 4 项「第 4 份同构仓储」拦截守卫；**ARCH P-08 收口轮 +9**：`DomainPrimitivesHygieneTests` 5 项 + `TaskItemCloneTests` 4 项；**ARCH P-09 收口轮 +14**：主 `RunOptionsContractTests` 5 项 + `RunOptionsHygieneTests` 3 项、Desktop `ConfigEditorsHygieneTests` 3 项 + 时段行为契约 3 项；**ARCH P-10 收口轮 +43**：主 `SecretMaskerTests` 20 项 + `RunMetricsTests` 8 项 + `RunMetricsInstrumentationTests` 4 项 + `RunMetricsWriterTests` 2 项 + `FileRunLoggerTests` 3 项 + `ObservabilityHygieneTests` 6 项，全绿）                                                                     | ↑        |
| CI 门禁与行覆盖率（ARCH P-11 / A-9①） | **4 道门禁全阻断**（2026-10-22 收口：`--no-incremental` 构建 / `dotnet format --verify-no-changes`（CHARSET 假阳性按 `Assert-Format.ps1` 过滤，实测 339 条过滤后 0 真实违例）/ 分层单测（快跑 613 + 慢跑 41 + Desktop 168）/ 覆盖率门禁 `Assert-Coverage.ps1`）；行覆盖率锁定基线（= 收口实测下取整）：**Core 44% / Infrastructure 39% / Agents 23% / Desktop 50%**（实测 44.09 / 39.84 / 23.06 / 50.10），向 ARCH §7.3 长期目标（85 / 70 / 50）爬升；`TestResults/` 已 ignore，覆盖率产物不入库 | 阈值只升不降；required status check 待管理员勾选 |
| 误导性自述注释数（P2-26）      | **0**（2026-10-20 收口：v2.0 盘点 147 处「二期/暂不支持/遗留/尚未」逐条核对——台账点名的脱节注释前序轮次已清、`RetrieveSecret` 一处本轮修正、~100 处「二期」为已交付功能历史标签如实保留；真实延期 4 项进 §4.7.1 台账。`CommentHygieneTests` 源码扫描锁死，新增即失败）                                                                     | → 0 并保持  |
| 应用路径魔法串处数（P2-31/P-07） | **0**（2026-10-21 收口：`src` 内 `"BatchAgentRunner"` 字面量仅剩 `AppPaths.cs` 一处声明、`Environment.SpecialFolder.LocalApplicationData` 亦仅此一处，原 9 处全部改为常量引用；三份仓储样板（建目录/`JsonSerializer`/`AtomicFileWriter`）清零、一律继承 `JsonStoreBase<T>`。`AppPathsHygieneTests` **4 项**机器锁死：① 魔法串清零 ② 派生仓储样板清零且继承基类 ③ 目录常量不脱根 ④ **新增「第 4 份同构仓储」类型扫描拦截**（P-07 收口轮新增，负测试实测可捕获）） | → 0 并保持  |
| 领域原语重复处数（P2-1~P2-6 / ARCH P-08） | **0**（2026-10-21 收口：`Truncate` → `Core/Text/TextTruncate`（残留内联 3 处并入：`StreamJsonRenderer`×2 / `TemplateFlowNodeViewModel`×1）、终态集合 → `TaskStatusExtensions.IsTerminal`、深拷贝 → `WorkflowTypeClones`（`record with`）+ `TaskItem` 唯一手写点、依赖失败分派 → `DagScheduler.EvaluateDependency`。`DomainPrimitivesHygieneTests` **5 项**机器锁死：① 除 `TextTruncate.cs` 外不得内联截断 ② 终态成员须委派单一事实来源 ③ 终态成员对全部枚举值与单一事实来源判定一致 ④ 深拷贝入口仅允许 `WorkflowTypeClones` / `TaskItem` ⑤ 依赖分派公开入口唯一且 `TaskDispatcher` 保持委派（负测试实测四项可捕获）；`TaskItemCloneTests` **4 项**反射锁死「新增字段必同步 `Clone()`」；同轮删除 `BatchEngine` 无调用点死私有 `Truncate` 别名后，`src` 内 `Truncate` 声明点只剩「单一事实来源 + 4 个保留调用点的薄后缀适配器」两类） | → 0 并保持  |
| God Config 与编辑器手写重复处数（P2-9/P2-15 / ARCH P-09） | **0**（2026-10-21 收口：`RunOptions` 由 363 行单一大类按 12 配置域横向切片为 `RunOptions.01~11-*.cs`（本体 369→18 行，切片各 ≤49 行），**JSON 形状与落盘字节逐字不变**；`ConfigEditors` 解析字段全量 `FieldEditor<T>`（`FieldParsers` 为唯一解析来源，含本轮收口的时段起止），并删掉 `StartText`/`EndText` 裸字符串桥接、修掉「非法输入静默抹掉已配置时段」缺陷，视图侧 7 个缺反馈输入框补齐校验反馈、错误文本样式收为 `TextBlock.field-error` 单一来源。机器锁死：`RunOptionsHygieneTests` **3 项**（须按配置域切片不得回缩 / 切片行数上限 / 禁 `Clone`·`Copy`·`Snapshot` 成员复现）+ `RunOptionsContractTests` **5 项**（落盘属性顺序 / 往返逐字节稳定 / 未知字段宽容 / snake_case / 派生属性只读）+ `ConfigEditorsHygieneTests` **3 项**（Desktop 内 `TryParse` 只出现在 `FieldParsers` / 声明的字段必须被 `Apply()` 读取 / 凡绑 `X.Text` 必呈 `X.Error`）（负测试实测三项均可捕获）） | → 0 并保持  |
| 可观测性回潮风险点（ARCH P-10 / A-8） | **0**（2026-10-21 收口：脱敏实现仅 `SecretMasker` 一处（原零基础设施，「凭证不落日志」全靠写入点自觉）、日志级别字面量仅 `RunLogLevels` 一处（原 20 处裸字面量散落 7 个文件）、日志落盘点仅 `IRunLogger.Write(RunLogEntry)` 一处（原十余个 `Write*` 各自拼文本直写）、运行级指标仅 `RunMetrics` 一处（原只能从日志文本人肉数）；`RunId` 贯穿事件 / 文本日志 / `run-*.jsonl` / `metrics-<run_id>.json`。`ObservabilityHygieneTests` **6 项**机器锁死：脱敏实现唯一 / 写日志点不得出现裸级别字面量 / 实现须带结构化通道与 RunId / 不得绕过统一落盘点 / 指标字段全集与比率边界 / 事件携带 RunId（负测试实测三项可捕获）） | → 0 并保持 |
| 逃逸阀台账条目数（P2-30）       | **9 条**（2026-10-22 P-12 偿还 `NoWarn CS1591`、#3 退役后；P2-30 收口时为 10 条：全仓盘点去重后登记——`#pragma` 1 + `[SuppressMessage]` 8 + `.editorconfig` `none` 1 + `NoWarn` 规则 5 + 钩子过滤 1；**无到期日条目 0**（原 #1「二期换 MailKit 时」/ #2「待评估」/ #5-#8「长期有效」全部折算为具体复核日）；同轮发现并偿还 2 项**从未登记**的生产代码豁免：`AVLN5001` 10 处过时 API（`Watermark`→`PlaceholderText`、`SystemDecorations`→`WindowDecorations`）+ Desktop `CA2007` 1 处（`ConfigureAwait` 缺失），豁免随之删除） | ↓（新增须登记，到期须清理） |

> **台账登记改用不变量描述，不用行号**。例：P0-1 登记为「`TryFallbackAsync` 中 `cost` 必须被 `MaxCostPerTask` 截断」并配断言测试。理由：既有审查报告记录 `:1011`，实测已漂移到 `:1406`，行号型台账必然失效。

### 4.9 引入顺序（避免门禁一次性压垮团队）

```
当天   : 提交规范 + gitignore 目录级 + 清产物 + 编译期分析 + 最小 CI（阶段 −1 的 1–5，已全部完成 2026-10-17）
迭代1  : CI（--no-incremental，只报不阻断）+ 格式门禁 + 测试分层 + 盲区补齐（已完成 2026-10-18，见 §5.4；格式门禁 = pre-commit 钩 + end_of_line/eol=lf 约定，CHARSET 假阳性按 §4.7 #9 过滤）
迭代2  : EnforceCodeStyleInBuild + ArchUnitNET 断言（Core 优先）
迭代3  : 复杂度阈值按基线标定（先 warning） + 逃逸阀登记
迭代4  : 复杂度阈值转 error + TreatWarningsAsErrors 分批开启（已提前完成 2026-10-18：存量清零后 TreatWarningsAsErrors=true）
迭代5  : **CI 门禁转阻断 + 覆盖率门禁**（✅ 已完成 2026-10-22，ARCH P-11：格式/覆盖率/分层单测/构建四道门禁进 CI 且失败即红；required status check `build-and-test` 待管理员在分支保护勾选；详见 §5.16）
持续   : PR 清单 + 20% 预算 + 台账月度复核
```

> 迭代 4 的 `TreatWarningsAsErrors` 提前至阶段 1 落地的原因：阶段 1 已把存量警告一次性清零（99 个，含 src 49 + 测试 50），「分批开启」的前提（避免一次性爆雷）已消失，直接全量开启等价且更简单。复杂度阈值（§4.4）与 ArchUnitNET（§4.3）仍在迭代 2/3 排期。

---

## 5. 执行记录（Execution Log）

### 5.1 阶段 −1（2026-10-17，B 组全部，< 1 人日）

| ID    | 落地内容                                                                                                                                                                      | 验证                                                                                           |
| ----- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| P0-11 | 新增 `.githooks/commit-msg`（拒绝空消息 / 纯标点 / 缺「改了什么·为什么」/ 正文过短）+ `.gitmessage.txt` 模板；本地 `git config core.hooksPath=.githooks`、`commit.template=.gitmessage.txt`               | 对 6 类样例消息做干跑：空/纯标点/无正文/缺为什么均被拒，合规中文消息通过                                                      |
| P0-13 | `.gitignore` 末尾按日期逐个文件名规则替换为目录级：`.workbuddy/`、`.playwright-cli/`、`/TaskList/`、`*.backup-*`、`/models.yaml`、`/.tmp_reflect.ps1`                                             | `git check-ignore` 对全部 10 项原误提交产物返回 IGNORED                                                  |
| P0-14 | `git rm --cached` 清理 10 项误提交产物（含含个人绝对路径的 `.tmp_reflect.ps1`、`TaskList/tasks.xlsx` 真实运行数据等）；文件保留在磁盘并转为 ignore                                                              | `git ls-files` 中上述 10 项已不在跟踪列表                                                               |
| P0-15 | `Directory.Build.props` 增加 `EnforceCodeStyleInBuild=true`、`AnalysisLevel=latest-recommended`、`AnalysisMode=Recommended`；`TreatWarningsAsErrors` 保持 `false`（先 warning 不阻断） | `dotnet build --no-incremental` Core：**29 警告 / 0 错误**，CA2007/CA1822/CA1001 等已真实暴露（增量构建曾显示 0） |
| P0-16 | 新增 `.github/workflows/ci.yml`：`windows-latest` + .NET 10，`dotnet build --no-incremental` + `dotnet test`，**只报告不阻断**                                                       | 工作流文件已就位；~~门禁转阻断（必需状态检查）留待阶段 1（§4.9）~~ ✅ **已由 ARCH P-11 收口（2026-10-22，见 §5.16）**：四道门禁（构建/格式/分层单测/覆盖率）失败即红；required status check 待管理员勾选 `build-and-test`                                                           |

### 5.2 阶段 0（2026-10-17，A 组 10 项 + P0-17）

A 组 10 项互不耦合，与 P0-17 并行完成。代码改动均随既有工程约定落地（`UiThread` / `AutoShutdownCoordinator` / `AtomicFileWriter`），并由对应不变量测试锁住。验证统一口径：全量 `dotnet test`（`.slnx`）**325 通过 / 0 失败**（Core/Infrastructure/Agents 测试 313 + Desktop.Tests 12）。

| ID     | 落地内容                                                                                                                                                          | 验证                                                                  |
| ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------- |
| P0-1   | `BatchEngine.TryFallbackAsync` 成本改为 `Math.Min(cost, Math.Max(0m, MaxCostPerTask))` 真实截断（原 no-op 恒等于 cost）                                                     | 单测 `兜底实测成本超出单任务上限时按上限截断` 通过；不变量「cost ≤ MaxCostPerTask」由测试锁住         |
| P0-2   | `CliProcessRunner`：异步读取回调收到 `null` 即 EOF 信号；进程退出后 `DrainOutputAsync` 等 stdout/stderr 双流 EOF 到达（5s 上限，超时只记日志不阻塞）                                               | 单测 `RunAsync_大量输出_进程退出后输出尾部不丢失` 通过                                  |
| P0-3   | `LocalGitIntegration` 全部参数走 `ProcessStartInfo.ArgumentList`（不经 shell、不做字符串解析）；`GitRefName` 白名单校验（防 `-` 开头被当选项、`..` 路径穿越）                                      | 单测 `GitRefNameTests`（白名单）+ `BatchEngineGitIntegrationTests` 通过      |
| P0-4   | `BatchEngine` 引入 `_writeGate`（状态写入与落盘序列化互斥）+ `_persistGate`（单写者串行落盘）；`CompleteTask/FailTask/MarkInterrupted/PersistAsync` 全部走门                                | 单测 `并发落盘串行化_终态不回退` 等通过                                              |
| P0-5   | 兜底成本/用量 `_fallbackBatchCost` / `_fallbackModelsUsed` 由 `_fallbackLock` 保护 check-then-act，读（汇总）写（累加）共用同一把锁                                                     | 单测 `并发下兜底成本与用量累计准确` 通过                                              |
| P0-6/7 | `EngineHost` 单一收敛点：`Raise` 经 `UiThread.Post` 广播；引擎事件订阅统一切 UI 线程后再调用订阅方，7 处订阅方不再各自 `Dispatcher.Post`；`UiThread` 已在 UI 线程则同步执行，保持顺序语义                           | 编译期单收敛点保证；线程切换责任从 7 个订阅方收口到 1 处（P0-6 的根因消除）                         |
| P0-8   | `MarkdownInPlaceWriter.WriteBackAsync` 复用库内 `AtomicFileWriter`（临时文件 + 原子替换），不再 `File.WriteAllTextAsync` 直接覆写                                                  | 单测 `回写走原子写_目录内不残留临时文件` 通过                                           |
| P0-9   | `GuidedLoginOrchestrator.ClearLiveCredentials` 先备份 Live 凭证到同级暂存目录再删除；异常路径 `RestoreLiveCredentials` 回滚原凭证；成功后才清暂存副本（清理失败仅告警，不触发回滚覆盖新凭证）                        | 单测 `GuidedLoginFlowTests` 覆盖备份/超时回滚/成功清理路径                          |
| P0-10  | `AutoShutdownCoordinator` 接管关机条件判定与可撤销倒计时；`EngineHost` 仅提供倒计时窗口，取消/异常/窗口弹不出均不关机（宁可不关也不强关）                                                                     | 单测 `AutoShutdownCoordinatorTests` 锁定「无一步到位直接关机」不变量；取消/异常/无窗口三路径均不关机 |
| P0-17  | 新建 `tests/BatchAgentRunner.Desktop.Tests` 引用 `Desktop` 程序集，加首批冒烟测试（纯 UI 无关值转换器 `GreaterThanZeroConverter` / `EqualsTextConverter` 共 12 项），Desktop 由结构性不可测转为可测 | `dotnet test` 该工程 **12 通过 / 0 失败**；已加入 `.slnx`，CI 全量自动覆盖            |

> 阶段 0 边界（交接必读）：① 仅止血，未动 `BatchEngine` 骨架（P1-1~P1-3 仍待阶段 2）；② ~~全量构建仍有 48 个 CA 警告~~（2026-10-18 更新：阶段 1 已清零并开启 `TreatWarningsAsErrors`，见 §5.4 / P0-12）；③ 不变量测试已锁住「一眼看不出是错的」类缺陷（P0-1/2/3/4/5/8/9/10），Desktop 线程收敛（P0-6/7）运行时断言测试已由阶段 2 P1-19 引入 Avalonia headless 测试基座落地（`UiThreadContractTests` 锁死线程收敛，见 §5.5）。

### 5.3 阶段 −1 的已知边界（交接必读）

1. **钩子是本地生效，非 clone 自动继承**。`core.hooksPath=.githooks` 写入的是本机 `.git/config`。其他 clone / CI runner 默认不跑该钩。新人接入需执行：  
   `git config core.hooksPath .githooks`（或仓库根提供 `tools/setup-hooks.sh` 一键脚本——尚未提供，列为后续）。
2. ~~**CI 当前只报告、不阻断**~~（2026-10-18 更新）：分支保护 / 必需状态检查**仍未配置**（GitHub 仓库设置，代码侧无法完成），红 CI 依旧不阻止合并；但 `TreatWarningsAsErrors=true` 已让「新增警告」在构建期失败（本地与 CI 的 build 步骤均生效）。**分支保护设为必需状态检查仍是剩余动作**（需仓库管理员权限）。
3. ~~**P0-12（存量警告清零）不在阶段 −1**~~（2026-10-18 更新）：阶段 1 已清零并开启 `TreatWarningsAsErrors`，见 §5.4。
4. **未净化已入库历史**。`git rm --cached` 只把文件移出索引，历史提交中仍含这些产物（隐私面）。彻底净化需 `git filter-repo`，属高风险操作，本阶段未做，登记为后续机会主义项。

### 5.4 阶段 1（2026-10-18，建网 6 项 + §4.1 格式门禁，P0 清零）

**验证统一口径**：`dotnet build --no-incremental` = **0 警告 / 0 错误**（`TreatWarningsAsErrors=true` 生效）；`dotnet test BatchAgentRunner.slnx` = **345 通过 / 0 失败**（阶段 0 基线 325 + 阶段 1 新增 20）；`dotnet format --verify-no-changes` = **0 问题**（CHARSET 假阳性按 §4.7 #9 过滤后）。

| ID        | 落地内容                                                                                                                                                                                                                                                                                                    | 验证                                                                                                                                                           |
| --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| P2-20     | 测试分层：E2E/Usability/Process 三类打 `[Trait("Category", …)]`；CI 拆「快跑（单元，默认 bucket + Desktop.Tests）」与「慢跑（E2E/Usability/Process）」两步，均 `--no-build`                                                                                                                                                             | 快跑过滤 `Category!=E2E&Category!=Usability&Category!=Process` = 308 通过；慢跑 = 24 通过；合计 332 = 主测试项目全量（2026-10-18 复测修订：原记 306/330，与同文档 345 = 332 + 13 自相矛盾，按实跑计数更正） |
| P2-21     | 去 Flaky：① `IProcessLauncher`/`RealProcessLauncher` 抽出 `CliProcessRunner` 的进程启动缝，测试可注入假启动器验证「启动失败」分支（无需真实进程）；② samples 改读**嵌入式资源副本**（csproj `Link` 指向真实 samples，内容随 samples 同步，不依赖运行时/克隆路径）；③ 删除全部测试中的 `RepoRoot()` 磁盘路径探测                                                                               | 新增 `RunAsync_启动器返回null_返回无法启动进程结果且不进入主循环` 通过；E2E/Usability/序列化测试改读嵌入式资源后全通过                                                                                  |
| P2-28     | samples 降级为「只读用户示例」：测试契约（config.example.json / tasks.json / tasks.md）全部改读嵌入式副本；`TestSampleResources` 统一读取入口                                                                                                                                                                                             | `RunOptionsSerializationTests.仓库内注释模板可被解析`（改断言资源非空）、E2E TC-E2E-008 往返均通过                                                                                     |
| P1-15     | 高危盲区补齐 16 项：`XlsxTaskWriterTests`（二进制原子写回：缺列自动追加 / 无临时残留 / 原地更新）、`CliCommandBuilderTests`（注入面：占位符替换 / 引号转义 / 工作空间包裹 / 专家注入）、`DangerousOperationGuardTests`（安全闸门：白名单优先 / 黑名单回传模式 / 空文本不抛）                                                                                                                | 新增 16 项全部通过；高危模块零覆盖数 7 → 4（2026-10-18 勘误：原记 7→3，但同格列余 4 项，7−3=4，按算术更正）                                                                                       |
| P0-12     | 存量警告清零 + 警告转错误：99 个警告逐类修复（CA2007 ConfigureAwait、CA1822 可静态化、CA1001 补 IDisposable 链、CA1861 静态数组、CA1816 SuppressFinalize、CA1806/CA2201/CA1847/CA1850/CA1860/CA2016/CA1711/CA1716/CA1051 等），8 项逃逸登记于 §4.7 台账；`TreatWarningsAsErrors=true` 全量开启                                                             | `dotnet build --no-incremental` **0 警告 / 0 错误**；故意加 Version 探针验证 NU1008 后即恢复                                                                                 |
| P1-17     | 引入 CPM：新建 `Directory.Packages.props`（`ManagePackageVersionsCentrally=true`，全部包版本集中）；5 个 csproj 的 `PackageReference` 去掉内联 Version；删除 Core 无效依赖 `Microsoft.Extensions.Logging.Abstractions`（全仓 `ILogger` 使用 0 处）                                                                                          | 探针实验：任一 csproj 再写 `Version=` 即 NU1008 **构建错误**，版本漂移结构性防住                                                                                                     |
| P1-18     | `JsonCreditsClaimStore` 默认构造修复（先解析目录到局部变量再建目录/取路径），拆除「默认构造必抛 ArgumentNullException」定时炸弹                                                                                                                                                                                                                 | 不变量测试 2 项通过（默认构造只读加载不抛 + 临时目录保存/加载往返无损；默认路径绝不写用户数据）                                                                                                          |
| P1-20     | `ConfigHubConverters` 两处 `ConvertBack` 由 `throw new NotImplementedException()` 改为返回 `BindingOperations.DoNothing`（表示「不支持反向转换」而非崩溃）                                                                                                                                                                      | 不变量测试 1 项通过（Desktop.Tests）；双向绑定触发不再崩                                                                                                                         |
| §4.1 格式门禁 | `pre-commit` 钩落地（`.githooks/pre-commit`）：`dotnet format --verify-no-changes`（CHARSET 假阳性按 §4.7 #9 过滤）+ 暂存 `.cs` 显式 `iconv` UTF-8 校验；`.editorconfig` 声明 `end_of_line=lf`、`.gitattributes` 锁 `* text=auto eol=lf`（修复 autocrlf 机器上 CRLF/LF 互踩）；存量格式基线同步清零（23 文件：IMPORTS/WHITESPACE/FINALNEWLINE/ENDOFLINE） | 钩子三项验证：干净状态放行；注入尾随空格文件被拒（WHITESPACE）；注入 GBK 字节文件被拒（UTF-8 守卫）。`dotnet format --verify-no-changes` 过滤 CHARSET 后 0 问题                                           |

> 阶段 1 边界（交接必读）：① CA2007/CA1861/CA1707 在**测试项目**经 NoWarn 豁免（xUnit1030 与 CA2007 直接冲突等原因，已登记 §4.7 #4/#5/#6）；~~生产代码 0 豁免~~ **2026-10-21 P2-30 事实更正**：该表述与代码真值不符——`src/BatchAgentRunner.Desktop/BatchAgentRunner.Desktop.csproj` 自 `3c20288`（P1-17）起即带 `NoWarn AVLN5001;AVLN3001;CA2007` 三项**生产代码**豁免且从未进 §4.7 台账（教训同 §4.1 ③：「文档声称」≠「代码真值」）。P2-30 本轮偿还 `AVLN5001` 与 `CA2007` 两项、删除其豁免，余 `AVLN3001` 登记为 §4.7 #10（见 §5.10）。② `IProcessLauncher` 目前只被 `CliProcessRunner` 消费，GateEvaluator 内的 `cmd.exe` 直拉（P1-9）仍待阶段 2 抽 `IProcessRunner`。③ 分支保护 / 必需状态检查仍未配置（GitHub 设置），CI 红不阻断合并——这是 P0-16「只报告不阻断」的最后残留，需仓库管理员在 GitHub 页面完成。④ `TreatWarningsAsErrors` 为全量开启而非 §4.2 的分批（存量清零后分批前提已消失，见 §4.9 注）。⑤ `pre-commit` 钩与 commit-msg 钩同为**本机生效**（`core.hooksPath` 写在本机 `.git/config`，clone 不自动继承），新人接入需执行 `git config core.hooksPath .githooks`；CI runner 不跑钩子，靠 `TreatWarningsAsErrors` + CI build 步骤兜底。⑥ CHARSET 过滤依赖 SDK 10.0.401 的回归行为，升级 SDK 后需复测（§4.7 #9）。

---

### 5.5 阶段 2（2026-10-18 ~ 2026-10-19，动骨）

> **本轮重点：P1-3（提 `IBatchEngine` 接口 + 窄化构造）**。P1-3 是 strangler fig 的前置：先有稳定接口面，后续每个拆分步骤才能以「接口契约不变」增量验证。P1-3 的改造（接口提取 + 构造窄化 + 契约测试）已在 prior session 提交（`1ff7c4e`），本轮（2026-10-19）补齐其配套 **§4.3 架构断言**并使文档与代码一致。**同轮次完成 P1-4/P1-5 收口**（见下表「P1-4/P1-5 收口」行）：`5d0fb14` 引入 `IEngineFactory`/`IEngineHost` 时组合根并未真正唯一——`CredentialVaultHost.CreateDefault` 等三处仍在 Desktop 层自行 `new` Infrastructure 具体类型，且 §4.3 表格中「Desktop 不得直接 new」断言一直未落地；本轮将装配全部收回组合根并补上该断言，P1-4/P1-5 才真正完成。

**验证统一口径（2026-10-19 收口轮实跑）**：`dotnet build --no-incremental` = **0 警告 / 0 错误**（全仓，含新增断言）；`dotnet test tests/BatchAgentRunner.Desktop.Tests` = **39 通过 / 0 失败**（含架构断言 3/3）；主测试项目分层实跑 = **402 通过 / 0 失败**（快 375 + 慢 27）。**已知环境问题**：主测试项目快速 bucket 跑完（375 全过）后测试宿主进程在退出时崩溃（与全量跑同源，疑似进程类测试残留），结论以「已执行用例全绿」为准；慢速 bucket 可完整跑完。**P1-9/P1-10/P1-16 复核轮（2026-10-19 晚）复跑**：全量构建 **0 警告 / 0 错误**；主测试分层实跑 **409 通过 / 0 失败**（快 382 + 慢 27）+ Desktop.Tests **39 通过 / 0 失败** = **448 / 0**（较收口轮 +7：并发工作方 TaskRunner 防御性终止相关测试调整，已随 P1-1 收口提交 `2299f22`）；本轮快速 bucket 跑完未再现宿主退出崩溃。**P1-1 收口轮（2026-10-19 晚）复跑**：全量构建 **0 警告 / 0 错误**；`dotnet format --verify-no-changes`（CHARSET 假阳性按 §4.7 #9 过滤）**0 问题**；主测试分层实跑 **428 通过 / 0 失败**（快 401 + 慢 27）+ Desktop.Tests **39 通过 / 0 失败** = **467 / 0**（较 448 +19，全部来自 `TaskDispatcherTests`；本轮未再现宿主退出崩溃）。

| ID        | 落地内容                                                                                                                                                                                                                                                                                                    | 验证 / 提交                                                                                                                                                    |
| --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1-3      | 提 `IBatchEngine` 目标接口（`Core/Abstractions/IBatchEngine.cs`，接口面与 `BatchEngine` 公共面严格一致，`IDisposable`）；`BatchEngine` 改为 `sealed class BatchEngine : IBatchEngine`；构造由 14 参数窄化为「5 必需（`RunOptions`/`IAgentCliAdapter`/`ITaskFileStore`/`ICooldownStore`/`IRunLogger`）+ 1 可选 `BatchEngineDependencies` 聚合」，聚合成员 `null` = 用默认实现，纯签名重构、行为不变 | 提交 `1ff7c4e`；`BatchEnginePhase2Tests`（14 方法）锁定公开行为；本轮新增 §4.3 断言（`Desktop` 除组合根 `EngineFactory` 外不得直引 `BatchEngine` 具体类型）通过 |
| P1-4/P1-5 | 引入 `IEngineFactory`/`IEngineHost`，组合根下沉到 `Desktop/Composition/EngineFactory`（唯一允许 `new` Infrastructure/Agents 具体类型与 `new BatchEngine` 的位置）；`EngineHost` 只持 `IBatchEngine`                                                                                                                                        | 提交 `5d0fb14`（`192eec6` 配套 §4.3 断言：`EngineHost` 不得持 Infrastructure/Agents 具体类型）                                                               |
| P1-4/P1-5 收口 | **组合根唯一化**：`5d0fb14` 后仍有三处「第二组合根」残留——① `CredentialVaultHost.CreateDefault`（5 个 Infrastructure/Agents `new`）→ 装配上移 `EngineFactory.CreateVaultHost`，宿主改纯构造注入，`Orchestrator` 收敛为 private（不再经 `IEngineHost` 泄漏 Infrastructure 具体类型）；② `EngineHost` 的 `new(new ShutdownService())` → 改 `EngineFactory.CreateShutdownCoordinator()`；③ `TemplateEditorViewModel` 的 `new FileWorkflowTemplateStore()` → 改经 `IEngineHost.TemplateStore`（`IWorkflowTemplateStore` 端口，`FakeEngineHost` 补零 IO 桩）。**§4.3 断言落地**：新增「Desktop 除组合根外不得直接 new Infrastructure/Agents 具体类型」IL 扫描断言（反射 `OpCodes` 自建 walker，覆盖构造器/访问器/闭包/异步状态机；捕获型 lambda 的委托实例化按规则本意排除） | 2026-10-19；Desktop.Tests 39/39（新断言 3/3）、主测试 402/402、全量构建 0 警告 / 0 错误；`dotnet format` 本轮文件 0 问题 |
| P1-9      | 进程 IO 赶出 Core：`IProcessRunner` 端口落 `Core/Abstractions/IProcessRunner.cs`（端口与默认实现同驻 Core，同 `IClock`/`IFileSystem` 模式），唯一允许 `new Process()` 的 `RealProcessRunner` 落 Infrastructure，Core 内留 `NullProcessRunner` 惰性默认（未显式注入时命令门禁直接通过）；`GateEvaluator` 改构造注入不再直拉 `cmd.exe`，引擎侧经 `BatchEngineDependencies.ProcessRunner` 接线 | 提交 `e1d82c8`；§4.3 断言「Core 不得出现 `System.Diagnostics.Process`/`ProcessStartInfo`」（成员 + IL 局部变量反射）随阶段 1 落地并覆盖本轮；`GateEvaluatorTests` 10 项（FakeProcessRunner：退出码/期望退出码/输出正则/失败模板/审查/人工门禁）全绿 |
| P1-10     | 引擎内 IO 全走抽象：引 `IFileSystem` 端口（`Core/Abstractions/IFileSystem.cs` + `SystemFileSystem` 默认实现，同 `IClock` 模式），经 `BatchEngineDependencies.FileSystem` 注入；`BatchEngine` 原三处绕过 `ITaskFileStore` 的直接 IO 全部改走端口（任务文件读写仍走 `ITaskFileStore`），`BatchEngine` 零直接 IO | 提交 `13aed90`；`BatchEngineTests` 不变量 2 项（任务文件存在性 / 工作空间存在性必须经 `IFileSystem` 端口，`FakeFileSystem` 桩验证不碰真实磁盘） |
| P1-16     | 取消 `CredentialLocationRegistry` 静态覆写：`public static class` + 静态 `Dictionary` → `sealed class : ICredentialLocationRegistry` 实例服务，`SetLiveRootOverride` 覆写状态改实例 `ConcurrentDictionary`（线程安全、无跨实例全局副作用）；规格表 `Specs` 为不可变静态数据（全部实例共享）。组合根 `EngineFactory` 持唯一实例并一次性覆写 Mock Live 根，Vault / 引导登录 / CLI 适配器 / 积分领取全链路显式注入同一实例 | 提交 `6ca29c0`；src 内零静态调用残留（grep 实证，仅剩文档注释引用）；凭证域 6 个测试文件（VaultSwitch / VaultSnapshot / MockVaultE2E / GuidedLoginFlow / CreditsClaim / CliPermissionSettings）改注入实例后全绿 |
| P1-1      | 按 **6** 协作者拆分（原路线图记 5，实际多抽出一个 `IPreflight`）：`ITaskDispatcher`（派发主循环/受控并发/DAG）、`ITaskRunner`（单任务状态机+终态写入+兜底计费+凭证/组合切换）、`IFallbackBilling`（兜底成本/用量）、`IGitWorkflow`（建分支/开 PR/Diff/Markdown 回写）、`IBatchReportBuilder`+`IBatchNotifier`（报告/通知）、`IPreflight`（启动前预检） | 提交 `0e5aba5`/`b43a65a`/`afc6943`/`1f0caa6`/`3d76083`（协作者白箱测试 `b372d60`/`0b7c194`）；`BatchEngine` 由 1960 行降至 1030 行                             |
| P1-1 收口 | **补齐最后一个白箱缺口**：产物 ① `ITaskDispatcher` 随 REQ-020 功能提交 `341112a` 落地（未走债务提交的「协作者白箱测试」模式），是六个协作者中唯一只有 `BatchEngine` 黑箱间接覆盖的（319 行调度逻辑：DAG 三策略、悬空/未到终态保守阻断、同工作空间串行、并行度上界、统一切换触发时机、停止/取消后排空）。新增 `TaskDispatcherTests` 19 项（可编程假上下文：门控 RunTask + 按任务覆写结果 + 执行钩子）。同轮 `chore(debt)` 清理 `BatchEngine` 拆分后残留的 3 个空节标题注释（「二期：通知推送」「兜底（REQ-004b）」「任务状态写入」，职责已迁协作者，P2-26 同类误导性注释） | 提交 `39bdb14`（测试 19/19 通过，全为快车道 Invariant）+ `596c165`（注释清理 −6 行）；主测试 428（快 401 + 慢 27）+ Desktop 39 = 467 / 0；全量构建 0 警告 / 0 错误；`dotnet format` 0 问题。白箱测试期间另暴露两类 `0x80131506` 类 CLR 致命崩溃（仅长会话/测试触发、生产隐蔽），随 `2299f22` 兜底：`TaskRunner` 等待恢复返回成功但时钟未推进至恢复点且无可用组合时判 `Blocked` 防无限空转；`PromptClassifier` 按 pattern 缓存 `RegexOptions.Compiled` 实例防动态程序集累积撑爆加载器堆（详见 `CODE-REVIEW-性能瓶颈与Bug审查报告.md` B3/B4） |
| P1-2      | 状态机外置 + 内拆：单任务状态机（重试/退避/降级/超时+终态写入）抽至 `ITaskRunner`/`TaskRunner`，`BatchEngine` 仅保留批处理级协调（落盘单写者、waiting 门、状态广播）；`ExecuteTaskAsync` 由 287 行 `while(true)+switch+goto` 显式状态机化为 `TaskExecutionState` 状态对象 + 8 个 ≤60 行 / CC≤15 子方法（原 `goto case` 改为方法调用） | 提交 `e7c9eb9`（外置）+ `1790708`（内拆：5 个新白箱用例，467→472 全绿）                                                                                              |
| P1-19     | **补测试 + 契约收敛（Avalonia headless 基座落地）**：① 程序集经 `[assembly: AvaloniaTestFramework]`（`HeadlessFramework.cs`）+ `DesktopHeadlessTestBase` 基类，使 `[AvaloniaFact]` 测试运行在 UI 线程（视图构造、`UiThread` 真实 marshal 可测）；② 线程收敛运行时契约 `UiThreadContractTests` 2 项（headless 下验证 `UiThread.InvokeAsync`/`Post` 确实把动作 marshal 到 UI 线程；无 Application 同步降级分支由既有非 headless VM 测试锁死）；③ churn 热点契约测试：`ConfigViewTests` 1 项（headless 构造 + 视图↔VM 接线 `PickTaskFile` 注入）、`LoginViewModelTests` 4 项（账号行渲染/ForceClaim 回写/两段式删除确认/Claim 空闲态）、`MonitorViewModelTests` 4 项（状态筛选/跟随 vs 全部输出）、`SetupWizardViewModelTests` 4 项（步骤流转钳制/AgentKind 同步/保存回写）。原 churn 热点（`ConfigView`/`TemplateEditor`/`Report`/`Config` VM）现全部有白箱/契约测试；VM 仅依赖 `IEngineHost` 端口（P1-4/P1-5 组合根唯一 `new`），界面契约收敛到稳定端口面，重构回归面被锁死。测试基座迁移：Desktop.Tests 由 xunit v2 升 xunit v3（`Avalonia.Headless.XUnit` 仅面向 v3），主测试项目仍 v2；`Directory.Packages.props` 增 `xunit.v3` + `Avalonia.Headless.XUnit`，csproj 同步 | 提交 `7059be1`；验证口径：全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test tests/BatchAgentRunner.Desktop.Tests` = **55 通过 / 0 失败**（P1-19 自身 54：较铺路 39 +15——UiThread 线程收敛契约 2 + Login/Monitor/SetupWizard/ConfigView 契约测试 13；P1-6 回归断言 +1，见 §4.8）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题 |
| P1-6     | **异步初始化门（2026-10-20）**：消除两处 sync-over-async 同步阻塞。① `MainViewModel` 构造器去掉 `Host.InitializeAsync().GetAwaiter().GetResult()`（原 `MainViewModel.cs:20`），改为注入 `InitializeAsync(CancellationToken)` 门（`App` 在窗口可见前 `await` 完成后再 `UiThread.Post` 创建并展示主窗口；宿主初始化失败走兜底——记录后仍然装配页面，窗口进入可用态，与 CLR 崩溃兜底 B3/B4 同类，不使进程崩溃）；子页面 VM 的装配沉入 `BuildPages()`，窗口可见时 VM 已就绪、绑定一次性成立。② `EngineHost.RebuildEngine()`（原 `EngineHost.cs:136`）同步阻塞改 `RebuildEngineAsync(CancellationToken)`（`ConfigureAwait(false)` + `UiThread.Post` 广播 `TasksChanged`）；4 处 VM 调用点（`ConfigViewModel` / `LoginViewModel`×2 / `SetupWizardViewModel`）由 `_host.RebuildEngine()` 改为 `await _host.RebuildEngineAsync().ConfigureAwait(true)`。③ `MainViewModel` 对宿主 `Status` 的 `PropertyChanged` 转发统一经 `UiThread.Post` 收敛到 UI 线程（与 P0-6/P0-7 一致）。④ 新增架构断言 `Desktop程序集_不得存在同步阻塞异步结果的调用`：IL 扫描全部方法体 call/callvirt，凡在「非编译器生成类型」中命中 `*Awaiter.GetResult()` / `Task.Result` / `Task.Wait()` 即判 P1-6 类同步阻塞（合法 `await` 落在异步状态机、类型名含 `<`，已排除），CI 双重兜住（§4.5 PR 清单项）。无行为变化，仅消除死锁/线程池饥饿风险 | 提交 `f43914e`；全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **488 通过 / 0 失败**（主 433 + Desktop 55，Desktop 较 P1-19 收口轮 54 +1：P1-6 回归断言；既有 VM 测试随 `RebuildEngineAsync` 改名无行为变化全绿）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题；`FakeEngineHost.RebuildEngine` 同步探针同步改名 `RebuildEngineAsync` 并保留调用计数断言 |
| P1-7     | **事件订阅泄漏（2026-10-20）**：行 VM 订阅长生命周期 `TaskItem` 的 `PropertyChanged` 全程无 `-=`，`MonitorViewModel.SyncTasks` 在每次 `TasksChanged`（引擎事件驱动，≤2 秒）执行 `Tasks.Clear()` 重建全部行，被移除的旧行 VM 因仍被 `TaskItem` 持有而永久无法回收（泄漏随运行时间累积）。补上缺失的退订：① `TaskRowViewModel` 由 `ObservableObject` 扩为 `ObservableObject, IDisposable`，订阅改为存储具名委托 `_itemChangedHandler`，`Dispose()` 中 `Item.PropertyChanged -= _itemChangedHandler`（幂等）；② `MonitorViewModel.SyncTasks` 在 `Tasks.Clear()` 前对每个旧行调用 `Dispose()`，重建后若 `_selectedTask` 已被替换则重新指向同 Id 新行，避免悬空指向已处置实例；`SyncTaskRow` 的 Add 路径新建行随下次 `SyncTasks` 统一回收。无刷新行为变化（行刷新仍由 `SyncTaskRow` 驱动 + 自身订阅双保险） | 全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **491 通过 / 0 失败**（主 433 + Desktop 58，Desktop 较 P1-6 收口轮 55 +3：新增 `TaskRowViewModelTests` 3 项——`Dispose` 后领域对象变更不再驱动本行 / 幂等 / 反复重建无订阅累积）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题；`TaskRowViewModel` 现为唯一订阅 `TaskItem` 事件的行 VM，`AttentionTaskRowViewModel` / `PreviewTaskRowViewModel` 不订阅事件（已核对，不在 P1-7 范围） |
| P1-11    | **Vault 安全加固（2026-10-20）**：三处债务一并收口——① **收 ACL**：`FileCredentialVault` 新增 `SecureVaultRoot`（Windows-only），在每次变更操作持 `_gate` 期间经 `EnsureRootSecured`（幂等，仅首次）对 Vault 根目录 `SetAccessRuleProtection(true, false)` 移除继承的默认 ACL，仅显式授予**当前用户 + SYSTEM** 完全控制（带 `ContainerInherit | ObjectInherit`，子目录与快照/清单文件自动继承）；加固失败仅记审计不阻断业务（DPAPI 内容级保护之外的防御纵深）。② **manifest 明文**：不改其格式（DESIGN §4.2 设计即明文元数据），目录级受限 ACL 把明文 `manifest.json` 的暴露面收口到本机当前用户，与加密快照同享目录权限边界。③ **统一原子写**：`WriteManifest` 由直写 `File.WriteAllText` 改为与 `WriteProtectedSnapshotFile` / `SetLiveAccountAsync` 同款的「临时文件 + `File.Move` 原子替换」，消除本类唯一的非原子写（崩溃/强杀不再产生半截 manifest）。非 Windows 平台跳过 ACL（凭证功能本身仅 Windows，见 `DpapiCredentialProtector`），行为零变化 | 全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **493 通过 / 0 失败**（主 435 + Desktop 58，主测试较 P1-7 收口轮 433 +2：新增 `VaultAclTests` 2 项——`Backup_后Vault根目录_移除继承ACL且当前用户拥有完全控制`（Windows 断言 `AreAccessRulesProtected==true` 且当前用户显式 FullControl）/ `WriteManifest_原子写_账号目录内不残留临时文件`）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题 |
| P1-12    | **CLI 权限固化治理（2026-10-20）**：`CliPermissionSettings` 由「无条件静默把用户 CLI 的 `defaultMode` 覆写为 bypassPermissions」改为「作用域收窄 + 原子写」：① **作用域收窄**——仅当 `permissions.defaultMode` 缺失/为空时才写入 bypassPermissions；用户若已显式设值（含 bypass 或任何其它模式）一律保留其选择，不再越权覆盖 `~/.codebuddy/settings.json`（该文件属 CodeBuddy CLI 自身、作用域不止本工具）；已为期望值时幂等不写，消除无谓覆盖与并发竞态（顺带消除 CODE-REVIEW P5「每次任务都重写 settings.json」的无谓磁盘写与并发读到一半风险）。② **原子写**——落盘改走 `AtomicFileWriter`（临时文件 + `File.Move` 原子替换），不再 `File.WriteAllText` 直接覆盖，强杀/断电不再损坏用户 CLI 配置。③ 方法由同步 `void` 转 `async Task EnsureBypassPermissionsAsync`（传递 `CancellationToken`），消除同步阻塞（遵循 P1-6 先例）；两处调用点（`CliAdapterBase.ExecuteAsync` / `CliLoginLauncher.LaunchAsync`）改为 `await`，异常不阻断语义不变 | 全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **495 通过 / 0 失败**（主 437 + Desktop 58，主测试较 P1-11 收口轮 435 +2：新增 `CliPermissionSettingsTests` 2 项——`用户已显式设defaultMode时_不静默覆盖保留其选择` / `已为bypass时_幂等不重复写_保留其它字段`；并补 `原子写_目录内不残留临时文件` 断言）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题 |

> 阶段 2 边界（交接必读）：① 路线图原计划 P1-1「拆 5 协作者」，实际抽了 **6** 个（多一个 `IPreflight`），`BatchEngine` 外壳仍保留为 `IBatchEngine` 门面，对外契约不变，可整体回退。② 构造窄化是纯签名重构：可选依赖聚合 `null` 语义与原逐个 `?? new …` 回退严格一致，行为零变化。③ 全量测试的快速 bucket 可跑完且全绿，慢速 bucket 完整通过（27/27）；测试宿主退出时崩溃为历史问题（2026-10-19 晚两轮复跑均未再现），实际用例数以 CI 复跑为准（§4.8 现按 467 实跑登记）。④ P1-19 已于 2026-10-19 收口：Desktop 测试 39→54，Avalonia headless 基座 + `UiThread` 线程收敛运行时契约（P0-6/7 最终验证）已落地，原 churn 热点契约收敛到 `IEngineHost` 端口面；阶段 2 至此全部完成。⑤ P1-2「外置」+「内拆」均已完成（外置 e7c9eb9；内拆 1790708）：`TaskRunner.ExecuteTaskAsync` 由 287 行 `while(true)+switch+goto` 显式状态机化为 `TaskExecutionState` 状态对象 + 8 个 ≤60 行 / 圈复杂度 ≤15 子方法（原 `goto case QuotaExhausted` 改为 `HandleQuotaExhaustedAsync` 方法调用），§4.4 单方法阈值达标，§4.8「最大方法行数」异常已关闭；全局最大方法随 §4.4 季度复测重定基线。⑥ P1-9/P1-10 边界（2026-10-19 复核轮）：`Core/Scheduling/TemplateExpander.cs` 仍有直接 `File`/`Directory` IO（CSV 读取 + glob 目录枚举），但其仅被 Desktop 模板编辑器预览调用、不在引擎运行路径，且属 P1-10 台账范围（`BatchEngine` 原三处）外——如实登记、不扩范围；若后续模板预览也要可测，随 P2 机会主义偿还一并收口。⑦ P1-1 收口轮（2026-10-19 晚）教训：产物 ① `ITaskDispatcher` 是随**功能提交** `341112a`（REQ-020）落地的，「债务提交必须带协作者白箱测试」的模式对「功能提交顺带产出的拆分产物」不成立——拆分产物无论由哪类提交落地，都须过「六协作者白箱覆盖清单」核对；本轮补测时另发现两处测试写法陷阱（同步请求延后切换会立即暂停派发、Wait 判定不记录 blockingDep），均以确定性门控替身锁死，供后续协作者测试参考。⑧ P1-11（2026-10-20 收口，阶段 2 收尾补漏）：Vault 根目录 ACL 加固（移除继承 ACL、仅当前用户 + SYSTEM 可访问）+ `WriteManifest` 改为原子写，是「FileCredentialVault 安全收口」的最后一项；`manifest.json` 仍保持 DESIGN §4.2 的明文元数据格式，其明文暴露面由目录级受限 ACL 收口到本机当前用户。行为与 P1-6/P1-7 同为阶段 2 声明「完成」后追加的收尾项，不属新增功能。
⑨ P1-12（2026-10-20 收口，阶段 2 收尾补漏）：`CliPermissionSettings` 作用域收窄（不静默覆盖用户 CLI 全局配置中的既有 `defaultMode`）+ 原子写（改走 `AtomicFileWriter`）+ 同步方法转 `async Task EnsureBypassPermissionsAsync`，与 P1-6/P1-7/P1-11 同类，属阶段 2 声明「完成」后追加的收尾项，不属新增功能；`EnsureBypassPermissions` 改名 `EnsureBypassPermissionsAsync`，`CliAdapterBase.ExecuteAsync` / `CliLoginLauncher.LaunchAsync` 两处调用点同步改 `await`。既有的「确保旁路权限以避审批框卡死」业务目标不变——仅从「强制覆写」收紧为「缺省才补、已设则保留」的 ensure 语义，且落盘不再有损坏风险。

---

### 5.6 阶段 3（持续化补完，2026-10-20 之后）

阶段 3 为长期机会主义偿还（§3 阶段 3 路线图）。P1-13 是「重写」准入四项（P0-2 局部、P1-13、P2-6、P2-15）中**最后一件未偿**的：它不阻塞线上正确性（原 `taskkill` 方案在 Windows 上本就能整树回收），但属跨平台不可移植 + 调用线程同步阻塞类债务，按 §3.2「重写准入」③（存在平台原生替代、代码净减少）收口。

**验证统一口径（2026-10-20 P1-21 收口轮实跑）**：`dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **537 通过 / 0 失败**（主测试 479 + Desktop.Tests 58，主测试较 P1-15 收口轮 533 +4：新增 `CreditsClaimConfigTests` 4 项）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题；仓内既有 `SetupWizardViewModel.cs:245` 同款原生 `Kill(entireProcessTree:true)`，P1-13 已统一收敛 `CliProcessRunner`。

| ID     | 落地内容                                                                                                                                                                                                                                                                     | 验证 / 提交                                                                                                                                                      |
| ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1-13  | **进程树回收重写（2026-10-20）**：`CliProcessRunner.KillTree` 由「外部 `taskkill /T /F` 进程 + 同步 `WaitForExit(3000)` 阻塞」重写为 .NET 原生 `Process.Kill(entireProcessTree: true)` 整树回收（保留 `HasExited` 判空 + try/catch 兜底，行为零变化）。消除：① 对外部 `taskkill` 的跨平台不可移植依赖（原方案仅 Windows 生效，非 Windows 平台退化为 `Kill` 单进程、不再整树）；② 最长 3 秒的调用线程同步阻塞（`Kill` 仅发送终止信号、不等待退出）；③ 无领域知识的中间进程。`KillTree` 保持 `public static void`，9 处调用点（取消 / 超时 / 危险拦截 / 异常分支）均无需改动 | 全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **498 通过 / 0 失败**（主 440 + Desktop 58，主测试较 P1-12 +3：新增 `CliProcessRunnerTests` 3 项锁死整树回收等价——beacon 子进程在 KillTree 后停摆 / 已退出进程调用不抛 / 单进程被立即终止）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题 |
| P1-14  | **子进程执行加固（2026-10-20）**：`LocalGitIntegration` 原 `WaitForExitAsync(ct)` 只响应取消令牌、无 stdin 重定向——挂死的 git/gh（网络卡死、等待终端凭证输入）会无限期挂起任务线程，且 gh CLI 缺失时 `Start()` 抛 `Win32Exception` 直接击穿「尽力而为」契约。三项加固：① **超时回收**——新增构造超时参数（`DefaultTimeoutSeconds=120`，非正回落默认），`WaitForExitAsync` 经 linked CTS 叠加 `CancelAfter(timeout)` 上限，超时（非调用方取消）即 `Kill(entireProcessTree: true)` 整树回收并按失败返回（`<fileName> 执行超时（>Ns），已整树回收`），不无限期挂起；调用方取消仍向上传播 `OperationCanceledException`。② **stdin 重定向**——`RedirectStandardInput=true` 并在 `Start()` 后立刻 `StandardInput.Close()`，等待终端输入的子进程（git 凭证提示 / gh 交互）直接拿到 EOF 继续执行、不挂起。③ **启动失败收敛**——`Start()` 抛 `Win32Exception`/`InvalidOperationException`（可执行文件不在 PATH）按「失败返回 null，不阻断任务」契约收敛为 `(-1, …, "无法启动 …")` 失败码，异常不再抛给调用方。行为零变化（happy path 退出码 / 真实 git 建分支 / 推送失败返回 null / gh 缺失退化为「已推送」提示均不变） | 全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **508 通过 / 0 失败**（主 450 + Desktop 58，主测试较 P1-13 收口轮 440 +10：新增 `LocalGitIntegrationTests` 10 项锁死——挂死进程超时整树回收 / stdin EOF 不挂起 / 正常退出不误报超时 / 调用方取消传播 / 缺失二进制收敛失败码 / IL 锁死 `RedirectStandardInput=true`+`CancelAfter` / 真实 git 建分支成功 / 无效仓库与非法分支名返回 null / 推送失败返回 null / gh 缺失退化为已推送提示）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题 |
| P1-15  | **高危盲区清零（2026-10-20）**：`CodeBuddyCreditsApiClient` / `BackupManager` / `ShutdownService` 三项阶段 1 顺延的高危零覆盖盲区补齐白箱测试（阶段 1 已补 `XlsxTaskWriter`+`CliCommandBuilder`+`DangerousOperationGuard` 16 项；`LocalGitIntegration` 已由 P1-14 补齐子进程执行契约测试）。`CodeBuddyCreditsApiClientTests` 经 `HttpClient` 桩注入响应，覆盖余额 JSON 解析求和（数字/字符串容量/`Packages` 缺失/非 JSON 返回 null）、「今日已签到」判定（code=10001 / 消息关键词 / 其它码）、`GetBalanceAsync` 成功解析、`ClaimDailyAsync` 首次成功 / 已签到返回 `AlreadyClaimed` / 服务端错误抛 `HttpRequestException`，并锁死 Bearer 鉴权头 + 浏览器 UA；`BackupManagerTests` 覆盖源缺失短路返回 null / 生成带时间戳副本且原文件保留 / `Prune` 保留最近 N 份删更早 / `retention≤0` 与目录缺失安全退避；`ShutdownServiceTests` 锁死平台判定（`IsSupported` 与 `OperatingSystem.IsWindows()||IsMacOS()` 一致），受支持平台不构造可触发关机的用例、非支持平台 `TryShutdownAsync` 返回 false 且不触达关机命令 | 全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **533 通过 / 0 失败**（主 475 + Desktop 58，较 P1-14 收口轮 508 +25：新增 `CodeBuddyCreditsApiClientTests` 11 项 + `BackupManagerTests` 6 项 + `ShutdownServiceTests` 2 项）；**高危模块零覆盖数 4 → 0**（原余 `CodeBuddyCreditsApiClient`/`BackupManager`/`LocalGitIntegration`/`ShutdownService`，`LocalGitIntegration` 已由 P1-14 收口）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题 |
| P1-21  | **生产端点去魔法串（2026-10-20）**：新建 `Core/Options/CodeBuddyApiConstants`（`ProductionApiBase` = `https://www.codebuddy.cn`、`BalancePath` = `/billing/meter/get-user-resource-summary`、`ClaimPath` = `/billing/meter/daily-checkin`），作为生产端点的**单一事实来源**；`CreditsClaimConfig` 的 `ApiBase`/`BalancePath`/`ClaimPath` 默认值改引用该常量（不再内联魔法串），`CreditsClaimMigrator` 的「迁移目标」常量同步去重引用——消除同一组 URL 散落三处（RunOptions 默认值 + 迁移器 + 客户端注释）的漂移风险。环境切换仍靠显式覆盖 `ApiBase` 等字段，但默认值的「生产」属性已被命名常量显式标注、可审计，且任何重新内联魔法串的回退会被测试捕获。行为零变化（JSON 序列化输出值不变：仍 `https://www.codebuddy.cn` + 两条 `/billing/meter/*` 路径），故用户 config.json 与 samples 无需改动 | 全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **537 通过 / 0 失败**（主 479 + Desktop 58，主测试较 P1-15 收口轮 533 +4：新增 `CreditsClaimConfigTests` 4 项——`默认配置_端点默认值等于规范生产常量` / `默认配置序列化后_api_base仍为生产基址` / `规范常量_形态合规` / `显式覆盖_不回退到生产默认值`）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题 |

> 阶段 3 边界（交接必读）：① P1-13 重写严格等价——`Kill(entireProcessTree: true)` 与原 `taskkill /T /F` 在 Windows 上均整树回收，行为由 `KillTree_父进程拉起常驻子进程_整树回收后子进程不再存活` 锁死；若被回退为「仅杀父进程」，beacon 子进程继续存活、该用例失败。② 非 Windows 平台：`Kill(entireProcessTree: true)` 要求目标进程为进程组首（本仓调用点均由 `IProcessLauncher` 启动、满足前提），与原方案非 Windows 退化路径语义一致，不扩范围。③ 重写后 `CliProcessRunner` 不再 `Process.Start("taskkill")`，仓内进程树回收统一为原生 `Kill(entireProcessTree: true)`（`CliProcessRunner.KillTree` + `SetupWizardViewModel.cs:245` 两处）。④ 余「重写」项（P2-6 六个手写 `Clone` → `record with`、P2-15 五个编辑器 → 泛型 `FieldEditor<T>`）仍属阶段 3 持续化范围（P1-15 高危盲区清零已完成，见上表）。

---

### 5.7 阶段 4：持续化开轮（2026-10-20，P2 机会主义偿还第一批）

> **本轮重点：P2-26（误导性注释清算）**。这是 P2 批次中第一项「治理元债务」——不清它，后续每轮偿还都会在注释里留下新的失真。清算后确立规则：**计划性延期只进 §4.7.1 台账，注释只描述当前实现**，并由测试守卫强制执行。

**验证统一口径（2026-10-20 P2-26 收口轮实跑）**：`dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **545 通过 / 0 失败**（主测试项目 487 + Desktop.Tests 58；较 P1-21 收口轮 537 +8：`SessionResumePolicyTests` 5 + `SmtpNotifierCredentialResolutionTests` 2 + `CommentHygieneTests` 1）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）**0 问题**。

| ID        | 落地内容                                                                                                                                                                                                                                                                                                                                                                  | 验证 / 提交                                                                                                                                                                     |
| --------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P2-26     | **误导性注释清算（2026-10-20）**：全仓盘点「二期 / 暂不支持 / 遗留 / 尚未 / 不实现 / 未实现 / 待实现」后分三类处置——① **台账点名的两处脱节注释前序轮次已清**（`TaskItem.cs` 文档注释改述「扩展字段由工作流执行与 DAG 派发使用」；`BatchEngine.cs` 20+ 处「二期扩展」随 P1-1 拆分收口轮 `596c165` 清理），本轮逐条核实确认；② **真正仍脱节的 1 处本轮修正**——`SmtpNotifier.RetrieveSecret` 注释自称「当前返回占位并提示」，实况已实现环境变量解析（`Environment.GetEnvironmentVariable(ref) ?? ""`），改述为如实描述 + 指向台账，方法可见性 `private` → `internal`（既有 `InternalsVisibleTo`）以便行为测试直接锁死；③ **~100 处「二期」标签逐条核对均为已交付二期功能的历史标签**（与 REQ-016/017/018、DESIGN E0–E5 编号并存，描述的是交付波次而非延期声明），如实登记保留、不做无收益 churn。**计划性延期 4 项全部进台账**（§4.7.1 #D1~#D4：SMTP Vault 集成/MailKit、会话恢复 M0 V-8、WorkBuddy 登录态 V-4b/V-4e、专家参数降级 V-5），代码侧 `IAgentCliAdapter.CanResume` / `SessionResumePolicy` / SYSLIB0014 pragma 的自述延期统一改为引用台账 ID | 全量 `dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **545 通过 / 0 失败**（主 487 + Desktop 58）；`dotnet format`（CHARSET 按 §4.7 #9 过滤）0 问题 |
| P2-26 守卫 | **`CommentHygieneTests`（新增，Architecture）**：源码词法扫描 `src/**/*.cs` 全部注释（行注释 / 块注释；跳过常规 + 逐字 + 插值字符串字面量），命中四类模式即失败——①「不实现 / 未实现 / 暂不支持 / 尚未实现 / 待实现」；② 桩声明（「占位并 / 占位值 / 占位实现 / 占位逻辑 / 占位状态」「返回 / 仅是 / 纯为 / 纯属 + 占位」；「占位符」模板变量与「防环：先占位」算法用语不命中）；③「二期 + 扩展 / 后续 / 再换 / 换成」；失败信息指向 §4.7.1 台账处置路径。把 P2-26 从一次性清算变成**结构性防复发**（§4.3 第三道墙新增一行、§4.5 PR 清单新增一项）。**负测试已验证**：临时还原 `RetrieveSecret` 原始误导注释（「当前返回占位并提示」）实测被捕获并报出文件:行 | 收口轮实跑 1/1 通过；对修正前的 `RetrieveSecret` 注释（「占位并提示」自述）实测可捕获                                                                                                     |
| P2-26 测试 | **行为守卫 7 项（新增）**：`SessionResumePolicyTests` 5 项锁死方案 b「默认关 + 全条件回退方案 a」语义（默认关 / 无句柄 / CLI 不支持 / 跨 adapter → `NoSession`；仅配置开 + 句柄在 + 同 adapter + CLI 声明支持 → `Resume`）；`SmtpNotifierCredentialResolutionTests` 2 项锁死凭据引用「环境变量名 → 取值 / 未设置 → 空串」（GUID 唯一变量名避免并行污染、用后即还）——实现若退化为「占位并提示」即失败，与台账 #D1/#D2 的验收列一一对应                                        | 均为主测试项目快车道 `Invariant`；收口轮 7/7 通过                                                                                                                                       |

> 阶段 4 边界（交接必读）：① 「二期」标签本身不是债务——已交付功能的历史标签与 REQ 编号并存、信息不失真，批量重写属 churn；债务只针对「声称未做但已做」与「声称已做但未做」两类失真，本轮已清零。② `CommentHygieneTests` 的词法扫描覆盖本仓全部源码形态（无 raw string / 无插值孔内注释），若未来引入 C# 11 raw string 需同步扩展扫描器。③ 守卫的失败词表是下限不是上限：真正想自述延期时，正确动作是登记 §4.7.1 台账后在注释引用台账 ID（如 `RetrieveSecret` 现行写法），而非绕过词表。④ D2/D3/D4 三项延期共享 M0 实测轮前置，建议同轮清零；到期未推进按 §4.7 规则升级为该迭代 P1。

### 5.8 阶段 4：P2-27 评估（2026-10-20，维持「主动不偿还」）

> **结论：P2-27 维持 §3.7 / 附录 A 的「主动不偿还」判定，不批量重命名中文测试方法名。** 本轮按方案约定的触发条件（「仅在 CI 出现编码问题时批量处理」）逐项核验，确认触发条件**未达成**。

**核验过程与证据**：

1. **`dotnet test --filter` 易碎**：实测 CI 工作流（`.github/workflows/ci.yml`）的过滤串为 `Category!=E2E&Category!=Usability&Category!=Process` 与 `Category=E2E|Category=Usability|Category=Process`——**全部按 `Category`(Trait) 过滤，从不按测试方法名**。中文方法名不进入 `--filter` 字符串，该向量在 CI 中不可达；开发者本地亦以 `Category` 过滤为主，按中文方法名过滤属极低频手操作。
2. **CI 报表易碎**：CI 运行器为 `windows-latest` + .NET 10（`dotnet-version: '10.0.x'`），进程与控制台默认代码页 UTF-8（CodePage 65001），`--logger "console;verbosity=normal"` 输出中文方法名无乱码——该向量无实锤。
3. **日志 diff 易碎**：测试日志中的中文同样落在 UTF-8 默认运行环境，与本地/CI 同态，无跨编码 diff 失真——该向量无实锤。

**现状度量（v2.0 盘点值已过时，本轮刷新）**：`tests/**` 下 `[Fact]`/`[Theory]` 方法共 **494** 个，其中中文命名 **285** 个（约 58%，仍占多数），v2.0 盘点记录的「252/252」已不准确（期间 P1-13~P1-21、P2-26 等轮次新增/补测使总量与中文占比均变化）。

**处置**：不引入任何代码或测试改动；维持「主动不偿还」。**升级触发器**（写入本方案即生效，无需再评审）：任一下列情形出现即触发 P2-27 批量重命名（ASCII 化 / 罗马音 / 英文摘要，单独 `chore(debt):` 提交，受 §3.6 单行提交纪律约束）——

- CI 运行器改为非 UTF-8 默认环境且报表出现中文乱码；
- `dotnet test --filter` 被迫按方法名过滤且本地非 UTF-8 控制台产生编码失败；
- 日志 diff 工具因编码不一致导致中文测试方法名比对失真、影响回归定位。

**验证口径（2026-10-20 P2-27 评估轮，纯文档变更）**：无代码/测试改动，`dotnet build --no-incremental` 与 `dotnet test` 基线维持 P2-26 收口轮的 **545 通过 / 0 失败**；`dotnet format` 维持 **0 问题**。

### 5.9 阶段 4：P2-29 清理（2026-10-20，顺手清理）

> **本轮重点：P2-29（`_harness/` publish 产物堆）**。附录 A「顺手清理」组的一项——不在关键路径、不影响正确性，但 1.2G / 214 文件的已 ignore 产物堆与 `samples/` 职责重叠且无人清理，属持续抬高单位改动成本的仓库卫生债务。清理前完成三项核实（全过才动手）：

1. **全部可再生产物**：214 文件 = `publish/`（`dotnet publish` 自包含输出：Avalonia/SkiaSharp/ClosedXML 等 110 dll + 48 so + 6 dylib + 4 exe + 22 pdb + `runtimes/`）+ `bin/Debug/net10.0` + `obj/`（MSBuild 中间产物；3 个 .cs 均为生成文件——`AssemblyAttributes.cs` / `Harness.AssemblyInfo.cs` / `Harness.GlobalUsings.g.cs`）。连 `Harness.csproj` 源文件本身都已不存在，仅剩 obj 残骸。
2. **无引用**：`git log --all -- _harness` 为空（从未跟踪）；slnx / csproj / CI / 脚本 / 文档对该路径零引用（唯一提及处为本台账自身）。
3. **可完整复现**：任何时候 `dotnet publish src/BatchAgentRunner.Desktop -o _harness/publish` 可重新生成全部内容。

| ID        | 落地内容                                                                                                                                                                                                                                                                                                                                                          | 验证 / 提交                                                                                                                                                                     |
| --------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P2-29 清理 | **整堆删除 `_harness/`**（214 文件 / 1.2G）：publish + bin + obj 全清 | `ls _harness` 不存在；`git status --ignored` 无 harness 条目 |
| P2-29 防复发 | **`.gitignore` 末尾补 `/_harness/` 显式目录级规则**（附 P2-29 注释）：此前仅靠通用 `publish/`（`.gitignore:180`）与 `[Bb]in/` / `[Oo]bj/` 规则命中——「ignore 生效但无人清理」正是该债务的根因；显式规则把意图写进版本库，换机 / 换人后静默重生前先撞上这条注释（沿用 P0-13 目录级规则教训） | `git check-ignore -v _harness/publish/x.dll` → `.gitignore:375`；探针目录 `_harness_probe/` 不受影响（规则精确匹配 `_harness/`） |
| P2-29 度量刷新 | **代码规模口径刷新**：B.2 快照「182 文件 / 24,417 行（含 `_harness/obj`）」中 `_harness/obj` 部分（3 个生成 .cs）随本轮消失；当前源码口径（排除 obj/bin）**223 文件 / 35,042 行**（Core 8,507 / Desktop 6,949 / Infrastructure 5,114 / Agents 1,941 / 主测试 11,064 / Desktop.Tests 1,467；较 v2.0 盘点的增长来自 P1-1~P1-21、P2-26 各轮新增协作者与测试） | `find` + `wc -l` 实跑 |

**验证口径（2026-10-20 P2-29 清理轮实跑）**：`dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **545 通过 / 0 失败**（主 487 + Desktop 58，与 P2-26 收口轮基线一致——`_harness/` 不在解决方案内，清理零行为影响）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）**0 问题**。

> 阶段 4 边界（交接必读）：① 本轮为纯仓库卫生操作，无任何 src/tests 改动——545 基线与 P2-26 收口轮逐项一致即是零行为影响的证据。② `_harness/` 与 `samples/` 的「职责重叠」指两者都曾是「给外面看的运行样本」：samples 是 tracked 的只读用户示例（P2-28 已收口），`_harness/publish` 是 ignored 的本机发布堆——清理后该重叠消失，发布输出不应再进仓库根目录。③ 若未来确需常驻 publish 输出（如 E2E 夹具），应先落 `IProcessLauncher` 式注入缝或嵌入式资源（P2-21/P2-28 模式），不得复辟「ignore 了之」。

### 5.10 阶段 4：P2-30 逃逸阀治理（2026-10-21）

> **本轮重点：P2-30（规则豁免无登记、无到期日）**——P2 批次中第二项「治理元债务」。§4.7 的规则一直写着「例外必须附理由 + 到期日 + 责任人」，
> 但台账自己就没做到：#1 到期日是「二期换 MailKit 时」、#2 是「待评估，未登记到期日」、#4~#8 是「长期有效」——**没有日期就无法判断是否过期，
> 「到期未清理自动升级为当期 P1」这条规则等于没写**。本轮按「盘点 → 偿还 → 登记 → 守卫」四步收口。

**验证统一口径（2026-10-21 P2-30 收口轮实跑）**：`dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **550 通过 / 0 失败**（主测试项目 492 + Desktop.Tests 58，较 P2-29 轮 545 +5：`EscapeValveHygieneTests` 5 项）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）**0 问题**。

| 步骤         | 落地内容                                                                                                                                                                                                                                                                                                                                                                                                                      | 验证 / 提交                                                                                                                                                                    |
| ------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| ① 全量盘点 | 按 §4.7 定义逐类扫描全仓逃逸阀：`#pragma warning disable` 1 处（SYSLIB0014）、`[SuppressMessage]` 8 处（CA1859×7 + CA1720×1）、`.editorconfig` `severity = none` 1 处（CA1002）、`NoWarn` 规则 5 项（CS1591 全仓 / CA1707·CA2007·CA1861 测试项目 / AVLN5001·AVLN3001·CA2007 Desktop）、钩子诊断过滤 1 处（pre-commit CHARSET）——**与 9 条台账对账后，发现 Desktop 的 3 项 `NoWarn` 从未登记**，且 §5.4 边界①「生产代码 0 豁免」与代码真值不符 | grep 实证（src / tests / `.editorconfig` / `*.csproj` / `Directory.Build.props` / `.githooks`）；`git log -S AVLN5001` 定位到 `3c20288`（P1-17）引入时未走 §4.7 登记 |
| ② 偿还可偿项 | **AVLN5001（10 处）偿还**：`TextBox.Watermark` → `PlaceholderText`（ConfigView 5 / DiffView 1 / TemplateEditorView 3）、`Window.SystemDecorations` → `WindowDecorations`（ShutdownCountdownWindow 1）——均为 Avalonia 12 过时 API 改名，仓内其余 10 处本就写 `PlaceholderText`，本次是补齐历史漏改。**Desktop `CA2007`（1 处）偿还**：`ConfigView.PickTaskFileAsync` 补 `.ConfigureAwait(false)`（与仓内 VM「ConfigureAwait(false) + UiThread.Post」模式一致）。两项豁免从 `Desktop.csproj` 的 `NoWarn` 移除 | `dotnet build --no-incremental` 0 警告 / 0 错误（移除豁免后无任何 AVLN5001 / CA2007 残留）；`ConfigViewTests` 与既有 VM 测试全绿（文件选择回调契约不变）        |
| ③ 完整登记   | §4.7 台账扩为 **8 列 × 10 行**，补齐 §4.6 强制四项：`发现日` / `责任人` / `到期日` / `不变量断言`（原表只有理由 + 到期日/责任人）。「长期有效」「二期换 MailKit 时」「待评估」全部折算为**具体复核到期日**（2026-12-31 / 2027-01-20），并按规则给出可验收的不变量断言（可测的指向既有测试，不可测的声明由守卫锁死登记完整性）。新增 #10（Desktop `AVLN3001`），并把代码侧标注统一为「逃逸阀台账 #N / 到期日 YYYY-MM-DD / 责任人 X」 | 台账 10 行四要素齐全、无到期日条目 0；代码侧标注与台账双向一致（由守卫第 3 项断言锁死）                                                                                                |
| ④ 守卫落地   | 新增 `EscapeValveHygieneTests`（`tests/BatchAgentRunner.Tests/Architecture`，5 项）：① 站点标注（`src` 内 `pragma` / `SuppressMessage`、`.editorconfig` `none`、csproj/props `NoWarn` 就近注释必须含台账 ID + 到期日 + 责任人）；② 台账四要素齐全且**到期日未过期**（过期即失败 = 自动升级当期 P1）；③ 代码↔台账双向一致（悬空 ID / 台账无引用均失败）；④ #10 的不变量（`src` 内不得出现 `new ShutdownCountdownWindow()`）       | 收口轮 5/5 通过；**负测试实测四类可捕获**：无标注 `pragma`（报文件:行）、`.editorconfig` 条目删标注、台账到期日改为 2020-01-01（报「已过期 → 升级 P1」）、标注删导致双向不一致 |

> 阶段 4 边界（交接必读）：① **保留的 #10（AVLN3001）是假阳性豁免**：`ShutdownCountdownWindow` 只由 `EngineHost` 带参构造、从不经 XAML URI 加载，为消告警补无参构造反而扩大公开面——故豁免保留、并由守卫第 4 项锁死「不得新增无参构造」，前提消失时须先删豁免再改代码。② **CA1002 豁免是承重结构**：实测开启即 96 处违规，全部是 JSON 契约类型的 `List<T>` 公开属性（`BatchReport` 18 / `RunOptions` 16 / `WorkflowTypes` 12 …），改抽象集合类型破坏用户 config 与模板兼容——偿还它的正确时机是「契约版本化」，不是顺手改。③ 到期日是**复核日**而非「必须修完日」：到期只要求当轮给出「续期并说明理由」或「清理」，两者都要在台账留痕；守卫只判过期与否，不代做决策。④ 守卫解析 `docs/TECH-DEBT-技术债务治理方案-v2.0.md` 的 §4.7 表格（列序耦合），改动表结构须同步更新 `EscapeValveHygieneTests.ReadLedger`。

### 5.11 阶段 4：P2-31 路径常量与 JSON 仓储收口（2026-10-21）＝ ARCH P-07

> **本项对应 ARCH 架构重构阶段 `P-07`（A-4 去重，见 ARCH §6.2 P-07 执行记录）**：代码本体于本轮偿还，
> 2026-10-21 **P-07 收口轮**复核真值（三份仓储仍继承基类、样板仍清零、魔法串仍仅 `AppPaths.cs` 一处）并加固守卫
> （`AppPathsHygieneTests` 3→4 项），阶段随之收口。
>
> **本轮重点：P2-31（三份 `Json*Store` 逐行同构 + `"BatchAgentRunner"` 路径魔法串 9 处）**。
> 与 P2-26 / P2-30 不同，这一项是**纯重复代码**：三份仓储的构造、默认目录、`LoadAsync` 的两条 catch、
> `AtomicFileWriter` 调用逐行同构，路径前缀再散落 9 处——任改一处要同步改三处。
> 本轮按「抽骨架 → 收口常量 → 锁守卫」三步完成，**对外行为与落盘字节零变化**。

**验证统一口径（2026-10-21 P2-31 收口轮实跑）**：`dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **565 通过 / 0 失败**（主测试项目 507 + Desktop.Tests 58，较 P2-30 收口轮 550 +15：`AppPathsHygieneTests` 3 + `JsonStoreBaseTests` 12）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）**0 问题**。

| 步骤         | 落地内容                                                                                                                                                                                                                                             | 验证 / 提交                                                                                                                                                                    |
| ------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| ① 抽骨架     | 新增 `Infrastructure/Persistence/JsonStoreBase<T>`：目录解析（`null` → `AppPaths.Root`）+ `Directory.CreateDirectory` + `FilePath` 拼接 + `LoadAsync` 三条回落（文件缺失 / `JsonException` / 反序列化为空 → `CreateEmpty()`）+ `SaveAsync` 走 `AtomicFileWriter`。派生类只留 `CreateEmpty()` 抽象工厂与可选 `NormalizeAsync()` 自愈钩子 | 三份仓储改造后全量测试全绿（既有 `JsonCooldownStoreTests` 2 项 + `JsonCreditsClaimStoreTests` 2 项逐项通过，行为零变化）                                                                       |
| ② 收口常量   | 新增 `Infrastructure/AppPaths`：`Root`（`%LOCALAPPDATA%\BatchAgentRunner`）、`ConfigDirectory`、`TemplatesDirectory`、`VaultDirectory`、`RunsDirectory`、`LogsDirectory`、`MockLiveDirectory`。替换 9 处魔法串——三份仓储（经基类 `Root`）、`FileWorkflowTemplateStore`、`FileCredentialVault`、`FileRunLogger`、`Desktop/Program`、`Desktop/Composition/EngineFactory`（2 处） | `grep -rn '"BatchAgentRunner"' src` 现仅剩 `AppPaths.cs` 一处声明；`dotnet build --no-incremental` 0 警告 / 0 错误                                                                      |
| ③ 守卫落地   | 新增 `AppPathsHygieneTests`（`tests/BatchAgentRunner.Tests/Architecture`，3 项）：① `src` 内除 `AppPaths.cs` 外不得内联应用名路径魔法串；② 三份仓储必须继承 `JsonStoreBase<T>` 且源码不得自带「`Directory.CreateDirectory` / `JsonSerializer.` / `AtomicFileWriter`」样板；③ `AppPaths` 全部 `string` 静态属性必须以 `Root` 为前缀 | 收口轮 3/3 通过；**负测试实测可捕获**：在 `JsonConfigStore.cs` 临时内联 `"BatchAgentRunner"` → 报「src\...\JsonConfigStore.cs:11 发现内联的应用名路径魔法串」；临时把 `AtomicFileWriter` 样板搬回派生仓储 → 报「仍自带样板：AtomicFileWriter」 |
| ④ 行为不变量  | 新增 `JsonStoreBaseTests`（`tests/BatchAgentRunner.Tests/Infrastructure`，12 项）锁住骨架提取后的对外可观测行为：三份仓储 ×（文件缺失 / 非法 JSON / `null` 字面量）回落语义、显式目录与默认目录的路径解析、积分台账与运行配置的保存—加载往返无损（含迁移钩子执行） | 收口轮 12/12 通过；既有三份仓储测试未改动仍全绿                                                                                                                                         |

**改造后的三类形态**（派生类只剩差异部分，样板一律在基类）：

| 仓储                     | `CreateEmpty()`        | `NormalizeAsync()`                                        | 行数变化         |
| ---------------------- | ---------------------- | --------------------------------------------------------- | ------------ |
| `JsonCooldownStore`    | `new List<…>()`        | **自愈**：超长 `RawValue` 截断 + 确有变化时原子重写（原 2026-10-18 现场逻辑原样保留）   | 70 → 51      |
| `JsonCreditsClaimStore` | `new CreditsClaimLedger()` | 无（默认钩子）                                                  | 50 → 19      |
| `JsonConfigStore`      | `CreateSample()`       | **迁移**：`CreditsClaimMigrator.Migrate` 在反序列化成功后就地执行 | 191 → 167    |

> 行数按提交 `adace61` 前后实测更正（原记 71→57 / 51→24 / 191→165 与 git 真值不符，P-07 收口轮按 `git show adace61^:<file> | wc -l` 复核后修正）。
> **P-07 收口轮（2026-10-21）加固**：`AppPathsHygieneTests` 由 3 项扩为 4 项——新增
> `Infrastructure内Json仓储具体类型_均须继承JsonStoreBase_防第四份同构仓储`（扫描 Infrastructure 程序集内所有 `Json*Store` 具体类型，
> 不继承 `JsonStoreBase<T>` 即失败），补齐原第 ② 项「按文件名清单核对、新增第 4 份仓储静默漏网」的盲区；
> 负测试实测：临时注入 `JsonNegativeProbeStore`（不继承基类）→ 守卫报「JsonNegativeProbeStore（基类：Object）」，探针已删除不入库。

> 阶段 4 边界（交接必读）：① **`AppPaths` 落在 Infrastructure 而非 Core**：路径布局是「文件存放在哪」的基础设施事实，Core 不消费它；Desktop 已直接引用 Infrastructure（`EngineFactory` 是组合根，本就 `new` 具体类型），故不违反 §4.3 分层断言（该断言只约束 `new` 具体类型，`AppPaths` 无实例）。② **`ConfigDirectory` 语义保持不变**：历史原因 `config.json` 与另两个 JSON 直接落在应用根而非子目录，收口时**刻意不改目录结构**——改了会让既有用户的配置/冷却/台账「凭空消失」，属数据丢失类风险，收益却不对称。③ **仅收口「路径」魔法串**：`NotificationDispatchers` 里的 `[BatchAgentRunner]` 是通知标题的品牌前缀，不是路径，不在本项范围内，保持原样未动。④ `JsonStoreBase<T>` 的 `NormalizeAsync` 默认同步返回原值；如需在加载后回写（冷却自愈），派生类须自行 `await SaveAsync`，基类不隐式回写——避免「只读加载」意外产生磁盘写入。

> **文档同步补记（2026-10-21 收口复核轮）**：P2-31 代码与台账提交（`adace61` / `43a10ea`）时漏同步 `docs/CODE-REVIEW-代码审查标准与流程.md`，本轮补齐三处——§3.5 可维护性与架构清单补「路径常量与 JSON 仓储样板（P2-31）」条、§4 硬规则表补 H16、§9 反模式表补对应行，使 §4.5「PR 评审清单落在 CODE-REVIEW」的口径在该文档侧闭环（对照：P2-30 轮同步了 §3.5 条目 + H15）。代码与测试真值未变（565 通过 / 0 失败），本轮为纯文档补同步。

### 5.12 阶段 4：P2-1~P2-8 / P2-13 / P2-15 / P2-23~P2-25 回填执行记录（2026-10-21 后提交，本文档滞后补记）

> 下述 P2 项已在 **2026-10-21 后**提交（提交号见下），但当时未同步本文档；本轮据 git 历史与实际代码真值补记，避免台账与代码漂移。

| 批次 | ID | 落地内容（摘要） | 提交 |
| ---- | -- | --------------- | ---- |
| 重复实现收敛 | P2-1 / P2-2 | 工作流执行器复用引擎等待与工作空间解析——消除双实现语义分叉 | `518103a` |
| 重复实现收敛 | P2-3 / P2-4 / P2-5 | Core 重复实现收敛——依赖判定 / 文本截断 / 终态集合各收单一事实来源 | `9a1c782` |
| 重复实现收敛 | P2-6 | 工作流域 8 个手写 `Clone` 改 `record with` 表达式——改字段必漏改的样板清零 | `f0dc708` |
| Desktop 契约隔离 | P2-7 / P2-8 / P2-13 | 行 VM 透传 / `FakeClock` 迁出生产 / 状态点着色改枚举 | `0bf025e` |
| 配置编辑器 | P2-15 | 五手写 DTO 编辑器收敛 `FieldEditor<T>`——解析失败从静默置 null 改为显式校验反馈 | `91fb700` |
| 仓库卫生 / 性能 / 死代码 | P2-23 / P2-24 / P2-25 | `*.xlsx` 标 binary / 日志批量落盘 / O(n²) Tail 消除 / 四项死代码删除 | `56fcddc` |

**验证统一口径（2026-10-21 后累计实跑）**：全量 `--no-incremental` 构建 **0 警告 / 0 错误**；测试总数由 P2-31 轮 565 增至 **650**（主 538 + Desktop 112，较 P2-31 +85）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题。

> **后续收口轮（2026-10-21，见 §5.14）**：本批 P2-1~P2-6 的收敛结果由 ARCH **P-08 收口轮**加固——残留内联截断清零、`TaskItem.Clone()` 阶段结果浅拷贝缺陷修复、新增 `DomainPrimitivesHygieneTests`（5 项）+ `TaskItemCloneTests`（4 项）防回潮闸门。即：本表是「债务本体偿还」记录，§5.14 是「单一事实来源机器化」记录。

### 5.13 阶段 4：P2-10 / P2-11 / P2-12 增量 / P2-22 收口（本次）

> **本轮重点：P2-10 / P2-11（硬编码字面色双主题化）+ P2-12（编译绑定增量）+ P2-22（残骸清理）**。
> 与 P2-26/P2-30 不同，这是「代码侧 + XAML 侧」的色值债务：原 status/level/diff/nav 调色板散落 `TaskRowViewModel` / `Phase2RowModels` / `DiffViewModel` / `MonitorViewModel` / `ConfigHubConverters`（代码侧 11 处 `Color.Parse`）与 `LoginView.axaml`（30 处）/ `SetupWizardView.axaml`（11 处），与 `AppStyles.xaml` 的 token 表并存冲突——深色主题下这些硬编码色完全失效（P2-10），且两份调色板数值互不一致（P2-11）。

**验证统一口径（本次实跑）**：`dotnet build --no-incremental` = **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx` = **650 通过 / 0 失败**（含新增 `AppBrushesTests` 27 / `OnboardingViewTests` 2）；`dotnet format`（CHARSET 假阳性按 §4.7 #9 过滤）0 问题。

| 步骤 | 落地内容 | 验证 / 提交 |
| ---- | ------- | ---------- |
| ① 抽入口（P2-11） | 新增 `Desktop/Helpers/AppBrushes.cs`：原 5 处 `Color.Parse("#…")` 调色板统一经本类按 token 名取色——运行时宿主可用取主题 token（双主题自动切换），无宿主（单测/无 Application）回落浅色 token 值（与 `AppStyles.xaml` Light 逐字一致）。`StatusRunningBrush` 等 token 已于 `Styles/AppStyles.xaml` 双字典就位；新增 `OnPrimaryBrush`（主色背景反白字）。代码侧 11 处硬编码色（`DiffViewModel` 6 / `MonitorViewModel` 6 / `ConfigHubConverters` 2 → 经 `ForDiffKind`/`ForBatchStatus`/`NavActive`/`NavActiveText` 等收敛）；既有 `TaskRowViewModel`/`Phase2RowModels` 的 `StatusBrush` 在飞改造（前序会话）一并收口 | `AppBrushesTests`（27 项）锁死：各方法产出色值 = 预期浅色 token / 键不存在确定回落 / 宿主可用可取；负测试实测可捕获回落 |
| ② XAML 双主题化（P2-10） | `LoginView.axaml`（30 处）+ `SetupWizardView.axaml`（11 处）`#hex` 全部改 `DynamicResource` 既有 token：状态/级别色→语义 Brush（`Primary`/`Success`/`Error`/`Warning`）、软盒背景→`*SoftBrush`、边框→`BorderSubtle`/`BorderStrong`、描述文本→`TextSecondary`、主色背景反白字→`OnPrimaryBrush` | `grep -niE '#[0-9A-Fa-f]{6}'` 在两份视图 0 命中；`OnboardingViewTests`（2 项）锁死视图可构造 + 接线；`src` 内 `Color.Parse` 仅剩 `AppBrushes` fallback |
| ③ 编译绑定增量（P2-12） | `LoginView` / `SetupWizardView` 根节点加 `x:CompileBindings="True"` + `x:DataType`（视图 + 各 `DataTemplate` 子类型 `LoginStepItem`/`LoginAccountRow`/`CreditsClaimRow`）；**不翻转** `Desktop.csproj` 全局默认（避免未带 `x:DataType` 的绑定失效） | 全量 `--no-incremental` 构建 0 警告 / 0 错误（编译绑定把属性拼写错误从「静默失效」升为编译错误）；全量测试 650 通过 |
| ④ 残骸清理（P2-22） | `models.yaml` 已于 P0-14 `git rm --cached` + gitignore，当前零代码引用；删除本地 0 字节残骸 | `git check-ignore -v models.yaml` 命中 `/models.yaml`；代码侧 `grep -rn 'models.yaml'` 0 引用 |

> 阶段 4 边界（交接必读）：① **不在翻转全局编译绑定**：`AvaloniaUseCompiledBindingsByDefault` 维持 `false`，仅对本次两视图增量开启；全量翻转（#D6）须先为所有视图补齐 `x:DataType`（含 `DataTemplate` 子类型）再单独排期，否则会让未带类型的绑定在运行时失效。② **token 键与浅色 fallback 逐字一致**：`AppBrushes` 的 fallback hex 必须随 `AppStyles.xaml` Light token 同步调整（由 `AppBrushesTests` 锁死），否则无宿主环境（单测）会出现色偏差。③ **仅收敛既有色值、不改布局/文案**：P2-10/11 行为零变化，纯双主题修复。

> **文档同步补记（本次收口轮）**：依 §4.5「PR 评审清单/硬规则落在 CODE-REVIEW」，本轮同步 `docs/CODE-REVIEW-代码审查标准与流程.md`（§3.5 可维护性与架构清单补「硬编码色值/魔法数字须经 token/常量（P2-10/11）」「XAML 绑定须带 x:DataType 编译绑定（P2-12 增量）」条、§4 硬规则表补 H17/H18、§9 反模式表补对应行）；同步 `docs/架构重构与工程化治理方案.md`（token 化结论 + P2 进度）；`README.md` 含他人未提交改动，本轮不碰。

### 5.14 阶段 4：ARCH P-08 领域原语收口轮（本次，承接 P2-1~P2-6）

> **本项对应 ARCH 架构重构阶段 `P-08`（A-5 去重，见 ARCH §6.2 P-08 执行记录）**：债务本体（`Truncate`×3、终态集合×3、`Clone`×6、失败分派状态机逐字重复）已于 2026-10-21 由 `518103a` / `9a1c782` / `f0dc708` 偿还（见 §5.12）；本轮为**收口轮**——清零残留副本、修复收口过程中暴露的真实缺陷、把「单一事实来源」升级为机器可校验的闸门。

| 步骤 | 落地内容 | 验证 |
| ---- | ------- | ---- |
| ① 残留内联截断清零 | `Agents/Internal/StreamJsonRenderer.cs` 2 处 `s[..120] + "…"` → `TextTruncate.Truncate(s, 120, "…")`；`Desktop/ViewModels/TemplateEditor/TemplateFlowNodeViewModel.cs` 的 `Shorten` 内联 → `TextTruncate.Truncate(flat, max, "…")` | 行为逐字等价（后缀与上限不变）；`grep -nE '\[\.\.[^]]+\]\s*\+\s*"' src` **0 命中**；由守卫 ① 锁死 |
| ② 修 `TaskItem.Clone()` 浅拷贝缺陷 | 原 `StageResults = new List<StageResult>(StageResults)` 只拷容器、元素共享——改快照的阶段摘要会污染原型；改 `StageResult` 为 `sealed record` + `WorkflowTypeClones.Clone(this StageResult)`（`with { }`）+ `TaskItem.Clone()` 逐元素 `s.Clone()` | `TaskItemCloneTests` 4 项（反射遍历全部可写属性注入非默认样本值 → 值等价 + 引用不共享 + JSON 形状一致 + 可空语义）+ `WorkflowTypeCloneTests` 7 项全绿 |
| ③ 防回潮闸门 | 新增 `tests/BatchAgentRunner.Tests/Architecture/DomainPrimitivesHygieneTests.cs`（**5 项**）：内联截断拦截 / 终态成员须委派 + 全枚举值行为等价 / 深拷贝入口仅 `WorkflowTypeClones` 与 `TaskItem` / 依赖分派公开入口唯一且 `TaskDispatcher` 保持委派；新增 `tests/BatchAgentRunner.Tests/Domain/TaskItemCloneTests.cs`（**4 项**） | **负测试实测**：临时注入 `P08Probe`（内联 `Cut` + 自造 `IsTerminal => true` + 自造 `Clone()` + 公开 `DependencyVerdict Eval()`）→ ①②③⑤ 四项按预期报错；探针已删除、不入库 |
| ④ 文档同步 | `ARCH-架构重构与工程化治理方案.md`（头部更新 / §1.2 A-5 / §3.4 第 4 条 / §4.3 / §6.1 / §6.2 P-08 执行记录 / 附录 A / 附录 B）、`架构重构与工程化治理方案.md`（§1.2 B2 / §4.3）、本文档（头部进度 / §4.8 度量 2 行 / §5.14） | 三份文档口径一致：P-08 ✅ 已收口，测试基线 744 |
| ⑤ 死代码清偿（P2-25 遗留项） | `Core/Engine/BatchEngine.cs` 的**无调用点死私有 `Truncate` 别名**（P-02 facade 收口时未一并清掉）及其后失效的 `using BatchAgentRunner.Core.Text;` 删除，facade 400→**397 行** | 严格构建 0 警告/0 错误（失效 `using` 会触发 IDE0005 打成错误）；`DomainPrimitivesHygieneTests` 5 项仍全绿；删除后 `src` 内 `Truncate` 声明点只剩「单一事实来源 `TextTruncate` + 4 个保留调用点的薄后缀适配器」两类，均不含截断运算 |

**验证统一口径（本次实跑，含 ⑤ 死代码清偿后复跑）**：`dotnet build BatchAgentRunner.slnx --no-incremental`（严格模式，不带 fast 参数）= **0 警告 / 0 错误**；`dotnet test` 全量 = **744 通过 / 0 失败**（主 582 + Desktop 162，较 P-07 收口轮 735 +9）；`dotnet format --verify-no-changes` 除 §4.7 #9 已登记的 CHARSET 假阳性 304 条外 **0 问题**；hygiene 守卫（主 28 + Desktop 4 = 32 项：`CommentHygieneTests` / `EscapeValveHygieneTests` / `AppPathsHygieneTests` / `DomainPrimitivesHygieneTests` / 两工程 `LayeringRulesTests`）全绿。

> 阶段 4 边界（交接必读）：① **保留调用点的薄后缀适配器**：`WorkflowExecutor` / `GateEvaluator` / `ReportWriter` / `CodeBuddyCreditsApiClient` 各有一处 `private static Truncate(...) => TextTruncate…`，仅「固定后缀 + 单一入参」的本地简写，不含截断逻辑，不属重复实现。② **`QuotaErrorClassifier` / `JsonCooldownStore` 的 `text[^MaxRawChars..]` 取尾语义**与 `TextTruncate`「保头 + 后置后缀」相反，形态不同不强行统一。③ **`StageResult` 记录化**仅新增编译器生成的 `Equals`/`GetHashCode`/`ToString`（值语义），JSON 契约与构造方式零变化，全仓无依赖其引用相等的用法。④ **Desktop 未纳入深拷贝反射扫描**：主测试工程不引用 Desktop（组合根隔离），Desktop 侧回潮由 `Desktop.Tests` 的 XAML/VM 契约守卫覆盖。⑤ **`BatchEngine` 死私有 `Truncate` 已于本轮清偿（原单列登记的 P2-25 遗留项）**：无调用点别名 `private static string? Truncate(...) => TextTruncate.Result(...)` 及其后失效的 `using BatchAgentRunner.Core.Text;` 已删除，facade 400→**397 行**；失效 `using` 若不同步删除会被 IDE0005 在严格模式打成错误。**提交切片注意**：该文件在本轮提交时点仍可能承载 P-02 facade 收口（953→400 行）的未提交改动，须先切 P-02 提交再提本轮，否则会把数百行 P-02 重构裹进 P-08 提交、违反 §7「单步 ≤400 行」护栏。

---

### 5.15 阶段 4：ARCH P-09 配置契约收口轮（本次，承接 P2-9 / P2-15）

> **本项对应 ARCH 架构重构阶段 `P-09`（A-6 去重，见 ARCH §6.2 P-09 执行记录）**：债务本体（P2-15 五编辑器 → `FieldEditor<T>`）已于 2026-10-21 后由 `91fb700` 偿还（见 §5.12），P2-9 的 `RunOptions.Clone()` JSON 往返已在 v2.0 重构中消除；本轮为**收口轮**——清零编辑器残留桥接、修掉收口过程中暴露的真实缺陷、把 #D5「God Config 分组」以零契约变化的方式结项、并为三者上防回潮闸门。

| 步骤 | 落地内容 | 验证 |
| ---- | ------- | ---- |
| ① 编辑器泛型化残留清零 | `Desktop/Models/ConfigEditors.cs` 510→**505 行**：时段起止由「手写 `TimeOnly.TryParse` + 裸字符串属性」收敛为 `FieldEditor<TimeOnly?>`（`FieldParsers.TimeOnlyOrNull`），**修掉「非法输入 → `BuildWindow()` 返回 null → `Apply()` 静默抹掉用户已配置免费时段」的真实缺陷**（现对错误态保留原时段）；同时删掉 `StartText`/`EndText` 裸字符串桥接（`FieldEditor<T>` 新增可选 `onTextChanged` 回调驱动 `Preview` 刷新，视图直绑 `{Binding StartTime.Text}`） | 行为契约 3 项（非法保留原时段 + 错误可见 / 合法按输入回写 / 关时段回写为空）；`Desktop/Models/FieldEditor.cs` 65→82 行 |
| ② 视图侧统一校验反馈 | `Desktop/Views/ConfigView.axaml` 767→**795 行**：原本只有共享候选模型池的单价/折扣有 P2-15 反馈块，**时段起止（共享池 + 每账号两处视图共 4 个）/ 每账号候选模型的单价·折扣（2 个）/ 兜底模型单价（1 个）共 7 个输入框**虽已是 `FieldEditor<T>` 却完全不呈现错误。现全部补齐 `ToolTip.Tip="{Binding X.Error}"` + 同模板内 `X.Error`/`X.HasError` 错误文本；错误文本样式收为 `UserControl.Styles` 的 `TextBlock.field-error` 单一来源 | 由守卫 ③ 锁死；`ConfigViewBindingContractTests` 全绿（新增绑定路径均可解析） |
| ③ #D5 God Config 结项 | `Core/Options/RunOptions.cs` 369→**18 行**，按 12 配置域横向切片为 `RunOptions.01~11-*.cs`（各 ≤49 行）。**不嵌套分组** → 类型面 / 属性名 / `[JsonPropertyName]` 键名 / 落盘字节全部不变，既有读写点与 `config.json` 契约零迁移 | `RunOptionsContractTests` 5 项全绿（含「落盘属性顺序与切片前逐项一致」——39 个持久化键） |
| ④ 防回潮闸门 | 新增 `tests/BatchAgentRunner.Tests/Options/RunOptionsContractTests.cs`（**5 项**）+ `tests/BatchAgentRunner.Tests/Architecture/RunOptionsHygieneTests.cs`（**3 项**）+ `tests/BatchAgentRunner.Desktop.Tests/Architecture/ConfigEditorsHygieneTests.cs`（**3 项**） | **负测试实测**：编辑器侧注入 `Desktop/Models/P09Probe.cs`（手写 `int.TryParse` + 声明后从不读取的 `FieldEditor<int?> ProberPort`）→ 守卫 ①③ 报错；删掉「每账号候选模型」视图 4 个反馈绑定 → 守卫 ② 报错（**3/3 可捕获**）。配置侧注入 `Core/Options/RunOptions.12-Probe.cs`（219 行超限切片 + `camelCase` 键名 + 可写属性挂 `[JsonIgnore]` + `public RunOptions Snapshot()`）→ `RunOptionsHygieneTests` ②③ + `RunOptionsContractTests` ①②④⑤ 共 **5 项报错**。全部探针已删除、不入库 |
| ④b 修守卫自身盲区 | `RunOptionsContractTests` 的 `Populator` 对 `int?` 等**可空基元**填成 `null` → 被 `WhenWritingNull` 忽略后属性不进 JSON，「全部持久化属性参与序列化」的往返断言漏检。改 `Nullable.GetUnderlyingType(type) ?? type` 拆到基元再填值 | 修复后重注可空基元探针 → ① 顺序契约 + ② 往返契约**均按预期报错**（当前 `RunOptions` 无可空基元字段，故此前盲区未暴露） |
| ⑤ 文档同步 | `ARCH-架构重构与工程化治理方案.md`（头部更新 / §1.2 A-6 / §3.4 断言清单 + 第 4 条 / §4.3 / §5.7 / §6.1 / §6.2 P-09 执行记录 / 附录 A / 附录 B / 附录 C）、`架构重构与工程化治理方案.md`（§1.2 B2 / §4.3）、本文档（§1.3 P2-9 与 P2-15 / §4.7.1 #D5 结项 / §4.8 看板 2 处 / 附录 A） | 三份文档口径一致：P-09 ✅ 已收口，测试基线 758 |

**验证统一口径（本次实跑）**：`dotnet build BatchAgentRunner.slnx --no-incremental`（严格模式，不带 fast 参数）= **0 警告 / 0 错误**；`dotnet test` 全量 = **758 通过 / 0 失败**（主 590 + Desktop 168，较 P-08 收口轮 744 +14）；`dotnet format --verify-no-changes` 除 §4.7 #9 已登记的 CHARSET 假阳性（本次实测 322 条）外 **0 问题**（本轮顺带修掉 `Options/RunOptions.09-Phase2.cs` 的 IMPORTS 排序）；架构守卫 `Architecture` 命名空间实测 主 **27** 项（`LayeringRulesTests` 9 / `EscapeValveHygieneTests` 5 / `DomainPrimitivesHygieneTests` 5 / `AppPathsHygieneTests` 4 / `RunOptionsHygieneTests` 3 / `CommentHygieneTests` 1）+ Desktop **11** 项（`LayeringRulesTests` 4 / `ConfigEditorsHygieneTests` 3 / `DesktopBindingRulesTests` 2 / 两视图绑定契约 2）全绿，另有配置契约 `RunOptionsContractTests` 5 项全绿；`RunOptions` 落盘字节与切片前**逐字节等价**（顺序契约 + 往返契约双锁）。

> 阶段 4 边界（交接必读）：① **`RunOptions` 不改 `record`、不嵌套分组**——它是用户配置的产品契约（ARCH §2.2 不动清单），嵌套分组会改 `config.json` 形状；横向切片是 #D5 唯一零迁移的收口形态，代价是**切片顺序即 JSON 落盘顺序**（调整/新增切片须同步 `RunOptionsContractTests` 的顺序契约）。② **SMTP 端口非法仍回落 587 属刻意保留**：587 是协议默认端口，回落才能让规则可用；与「保留原值」的差异在于端口有明确默认值而时段没有——但错误态同样对用户显式呈现（不静默）。③ **保留的非 `FieldEditor<T>` 字段**：`NotificationRuleEditor.SmtpToText`（逗号/换行分隔的多值文本，非解析回写字段）、`QuietHours`（原样透传文本，由通知侧解析）均非泛型编辑器适用场景，不强行统一。④ **视图 XAML 守卫只校验「有反馈绑定」**：不校验反馈的位置/文案形式（各视图布局差异大，机器判定形态弊大于利）。⑤ **提交切片注意**：本轮涉及的 `ConfigEditors.cs` / `ConfigView.axaml` / `ConfigEditorsTests.cs` 三个文件在提交时点仍承载 P-04 配置页分解与 P2-15 编辑器泛型化的未提交改动，须**先切出 P-04 + P2-15 提交、再提本轮**，否则会违反 §7「单步 ≤400 行」护栏。

---

### 5.16 阶段 4：ARCH P-11 CI 阻断 + 覆盖率/格式门禁收口轮（本次，承接 P0-16 / A-9①）

> **本项对应 ARCH 架构重构阶段 `P-11`（A-9① 工程化，见 ARCH §6.2 P-11 执行记录）**：P0-16 建的最小 CI 自阶段 −1 起「只报告不阻断」，格式门禁依赖已停用的本地 pre-commit 钩，`coverlet.collector` 自 v1.0 起「已引用但无收集管线」。本轮把三道机器门禁（格式 / 覆盖率 / 分层单测+构建）全部接入 CI 并转为阻断语义，R-3（CI 不阻断致质量下滑）缓解闭环。

| 步骤 | 落地内容 | 验证 |
| ---- | ------- | ---- |
| ① ci.yml 转阻断 | `.github/workflows/ci.yml`：job 仍名 `build-and-test`（分支保护可稳定引用），步骤链 = 全量 `--no-incremental` 构建 → 格式门禁 → 快跑单测 + 覆盖率采集（主 + Desktop）→ 慢跑单测（E2E/Usability/Process，不采覆盖率）→ 覆盖率门禁；任一步骤非零退出即 job 失败，「只报告不阻断」语义消除，文件头注释同步改写 | 本地逐步实跑四道门禁均 PASS（见下方验证口径） |
| ② 格式门禁脚本 | 新增 `scripts/ci/Assert-Format.ps1`：封装 `dotnet format BatchAgentRunner.slnx --verify-no-changes`，按行捕获输出（**不用 `Out-String`——80 列换行会把 `error CHARSET:` 截断成 `error CH`/`error CHA` 导致过滤失效，实测踩坑**），过滤 §4.7 #9 已登记的 CHARSET 假阳性（SDK 10.0.401 对全部合法 UTF-8 无 BOM 源码误报），其余违例（ENDOFLINE/WHITESPACE/IDExxxx）一律 exit 1 | **负测试实测**：向 `ReasoningEfforts.cs` 注入行尾空格 → 门禁按预期 FAIL 并指出 `error WHITESPACE`（探针已删、不入库）；干净树 339 条 CHARSET 过滤后 0 真实违例 |
| ③ 修工作区 EOL 污染 | `src/BatchAgentRunner.Core/Options/ReasoningEfforts.cs` 工作区副本被外部工具改成 CRLF（index/blob 本就是 LF）→ 真实 ENDOFLINE 违例；剥回 LF 后与 HEAD 字节一致（`git hash-object` 相等） | `dotnet format` ENDOFLINE 违例清零；该文件无内容改动、不入提交 |
| ④ 覆盖率门禁脚本 | 新增 `scripts/ci/Assert-Coverage.ps1`：解析两个测试工程产出的 `tests/*/TestResults/*/coverage.cobertura.xml`，按 class `filename`（`src/` 前缀取第二段）归并生产程序集，逐程序集断言行覆盖率 ≥ 阈值；测试工程与第三方程序集不在阈值表内、不进分母；阈值 = 收口实测基线下取整（Core 44 / Infra 39 / Agents 23 / Desktop 50），§7.3 长期目标（85/70/50）为爬升方向不硬门禁 | 四程序集实测 44.09 / 39.84 / 23.06 / 50.10 全部 PASS |
| ⑤ 采集配置 | `tests/coverlet.runsettings`：`Format=cobertura` + `SkipAutoProps=true`；**移除 Exclude 段**——coverlet.collector v6 对 `[Asm*]*` 通配式误伤导致整份 0 采集（实测踩坑），改由 ④ 的白名单放行；采集范围锁定快跑 bucket，保证门禁可重复 | 两个测试工程各产出 1 份 cobertura；快跑 613 + Desktop 168 全绿 |
| ⑥ 配套 | `.gitignore` 新增 `TestResults/`（覆盖率产物只活在 CI/本地，不入库） | `git check-ignore` 对 cobertura 产物返回 IGNORED |
| ⑦ required status check（交接项） | 分支保护属 GitHub 仓库管理设置（共享状态，本仓无 `gh` CLI）——**由仓库管理员执行**：Settings → Branches → `master` 规则 → Require status checks → 勾选 **`build-and-test`**（建议同时 Require branches to be up to date）。等价 CLI（管理员）：`gh api repos/<owner>/<repo>/branches/master/protection --method PUT -f "required_status_checks[strict]=true" -f "required_status_checks[contexts][]=build-and-test" -f "enforce_admins=false" -f "required_pull_request_reviews=null" -f "restrictions=null"`。操作步骤已写入 ARCH §6.2 P-11 执行记录与 README | 待管理员勾选；勾选后红 CI 即阻止 PR 合并 |

**验证统一口径（本次实跑）**：`dotnet build --no-incremental`（严格模式，不带 fast 参数）= **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx --no-build` 全量 = **822 通过 / 0 失败**（主 654 = 快跑 613 + 慢跑 41，Desktop 168；较 P-10 收口轮 801 +21，增量来自 HEAD 中 ReasoningEfforts 特性提交，与本轮门禁改动无关）；本地实跑 `Assert-Format.ps1` = PASS（339 CHARSET 假阳性过滤后 0 真实违例）、`Assert-Coverage.ps1` = PASS（Core 44.09% / Infra 39.84% / Agents 23.06% / Desktop 50.10%，阈值 44/39/23/50）。

> 阶段 4 边界（交接必读）：① **覆盖率阈值只升不降**——下调即放弃门禁；确需临时豁免按「先补测试、再动阈值」处理并在 ARCH 登记。② **CI SDK 版本浮动（`10.0.x`）与 CHARSET 过滤耦合**：未来 SDK 若修复误报，过滤只是空转（无害）；若出现新的整文件级假阳性，只应加进 `Assert-Format.ps1` 头部的过滤清单并注明原因，**不得**为过门禁去关 `.editorconfig` 真实规则。③ **慢跑 bucket 不采覆盖率**是刻意设计：真实进程类测试在 CI 上耗时波动大，采入门禁会时快时慢、失去可重复性。④ **门禁脚本路径即契约**：`scripts/ci/*.ps1` 被 ci.yml 以 `pwsh -File` 引用，重命名/移动须同步改 workflow。⑤ **本地 `git status` 可能显示 `ReasoningEfforts.cs` 为 M 但 `git diff` 为空**——stat 缓存残影（mtime 相等触发 racy 判定），内容与 HEAD 字节一致，`git update-index --refresh` 或任一带内容比对的 git 命令即消隐。

---

### 5.17 阶段 4：ARCH P-12 规范集 + 静态安全扫描收口轮（2026-10-22，承接 A-10 / A-11）

> **本项对应 ARCH 架构重构阶段 `P-12`（A-10 规范集 / A-11 静态安全扫描，见 ARCH §6.2 P-12 执行记录）**：
> 治理前的两处空白——① `.editorconfig` 只有格式类规则，命名约定与关键安全 CA 规则未成集，`CS1591` 被
> `NoWarn` 全仓关闭（公共 API 无文档约束，逃逸阀台账 #3）；② P0-3 修掉的命令注入、Vault 路径遍历、
> 凭证硬编码三类风险只靠「一次性修复」，没有守卫，重构后可能悄悄退化。本轮把两者都变成机器门禁。

| 步骤 | 落地内容 | 验证 |
| ---- | ------- | ---- |
| ① 规则集扩充 | `.editorconfig` 新增命名规则：接口 `I` 前缀、类型 PascalCase、**对外可见**成员（方法/属性/事件）PascalCase、参数与局部变量 camelCase、私有实例字段 `_camelCase`、静态字段（含 `const`/`static readonly`）PascalCase；口径按**存量约定**固化（非理想范式强加），并把 6 处 `const int FacadeLineCap`、1 处 `const int TailWindowChars` 改为 camelCase、2 个可变静态缓存 `_cache`/`_cacheTime` 改为 `Cache`/`CacheTime` 以消除同类型内两种静态字段命名并存 | `dotnet format` IDE1006 违例清零 |
| ② 安全 CA 规则 error 化 | `CA5350`（弱加密）/ `CA5351`（已破解算法）/ `CA3001`（SQL 注入）/ `CA3075`（不安全 DTD/XXE）/ `CA5392`（P/Invoke 未限定 DLL 搜索路径）设为 `error`——CA5392 唯一命中 `Program.MessageBoxW` 的 `user32.dll` P/Invoke（真实 DLL 劫持面），补 `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` 收紧（该 DLL 只在 System32，无行为变化）；其余四条存量 0 违例，属零成本闸门：新增即构建失败 | 全量严格构建 0 警告 0 错误 |
| ③ 恢复 CS1591 | `Directory.Build.props` 删除 `NoWarn CS1591`；四个 `src` 生产工程开启 `GenerateDocumentationFile`；公共 API（类型 / 成员 / 枚举值 / 参数）全量补齐 XML 文档，并逐条改写为有信息量的说明（不保留「成员名。」式占位）；同轮修掉 15 处把 `<param>` 放进 `<summary>` 的结构性错误、5 处 `cref` 指向私有/不存在成员的 CS1574 | 严格构建 CS1591/CS1570/CS1573/CS1574 全 0；逃逸阀台账 #3 退役（§4.7） |
| ④ facade 行数闸门口径校正 | `LayeringRulesTests` 的 6 条「上帝类 ≤400 行」断言由「物理行数」改为「**代码行数**（排除 `///` 文档注释）」：文档行数由 API 面大小决定、与上帝类回潮无关，计入等于用「文档写得全」惩罚架构闸门；空行与普通注释仍计入 | 六条闸门全绿（ConfigViewModel 452→366 代码行、BatchEngine 427→396、MonitorViewModel 452→388） |
| ⑤ 静态安全扫描守卫 | 新增 `SecurityHygieneTests`（7 项）把三类仓级不变量变成红线：**命令注入**——`CliCommandBuilder` 的 `{prompt}`/`{workspace}` 必须保持 `Quote(NormalizeToSingleLine(…))` 形态、引号实现全仓唯一、`CliAdapterBase` 一律经 `CliCommandBuilder.Build` 且不得裸替换占位符；**路径遍历**——`FileCredentialVault` 六个账号 ID 入口方法**逐个方法体**校验 `NormalizeAccountId` 净化 + 恶意 ID（`..`/`/`/`\`/`*`）拒绝语义（Theory 5 例）+ 合法 ID 原样通过；**凭证硬编码**——`src` 全量扫描疑似密钥字面量（排除存环境变量名/引用的标识符） | 7 项全绿；把断言改为按方法体校验后，实测能定位单点削弱 |
| ⑥ 可见性接线 | `ci-platforms.yml` 的 `architecture-tests` 独立桶追加主测试工程 `FullyQualifiedName~Architecture` 一步——安全守卫与分层/单一事实来源守卫同桶隔离跑，失败在独立 job 可见 | workflow 语法与既有步骤一致（`--no-build`，先跑全量 build） |

**验证统一口径（本次实跑）**：全量 `dotnet build BatchAgentRunner.slnx --no-incremental`（严格模式，不带 fast 参数）= **0 警告 / 0 错误**；`dotnet test BatchAgentRunner.slnx --no-build` 全量 = **842 通过 / 0 失败**（主 674 = 快跑 633 + 慢跑 41，Desktop 168；较 P-14 收口轮 830 **+12** = `SecurityHygieneTests` 11 个用例（6 Fact + 1 Theory×5）+ `CommentHygieneTests` 新增「文档占位」守卫 1 项）；`Assert-Format.ps1` = PASS（342 条 CHARSET 假阳性过滤后 0 真实违例）；`Assert-Coverage.ps1` = PASS（Core 44.09% / Infra 39.84% / Agents 23.06% / Desktop 50.10%，与 P-11 锁定基线一致——文档注释不产生可执行行，覆盖率不受影响）。

> 边界（交接必读）：① **命名规则的强制点是 `dotnet format`**（格式门禁），命令行 `dotnet build` 不会因 IDE1006 失败——不要据此认为命名无需遵守；② **私有成员不在命名规则内**（`_Forward` 这类事件转发方法是本仓既有约定，改名属无收益 churn）；③ `.editorconfig` 里的命名规则是**存量口径固化**，不是理想范式——若要收紧（如私有静态字段也统一 `_camelCase`），须先全仓实测再改，且同步改写本表口径；④ **新增公共 API 必须有 XML 文档**，`TreatWarningsAsErrors` 会把缺失直接打成构建错误；⑤ 安全守卫读源码文本（正则 + 方法体截取），**重命名关键类型/方法会使其失效**——改 `CliCommandBuilder`/`NormalizeAccountId` 时须同步改守卫，否则红灯即提醒。

---

## 附录 A：台账索引（按偿还方式分组）

| 方式            | ID                                                                                                                                               |
| ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| **重构**        | P0-1, P0-3~~P0-10, P0-12~~P0-15, P0-17, P1-1~~P1-5, P1-7, P1-9~~P1-12, P1-14~~P1-21, P2-1~P2-8, P2-10~P2-15, P2-16~P2-21, P2-23, P2-24, P2-26, P2-30 ✅（2026-10-21 已完成，见 §5.10）, P2-31 ✅（2026-10-21 已完成，见 §5.11）, P2-12 ✅（本次增量，见 §5.13）, **P2-9 ✅（本次，见 §5.15；＝ ARCH P-09 收口，#D5 结项）**【P2-22 已解决（P0-14 gitignore + 零引用）】 |
| **重写**（仅 4 项） | P0-2(局部), P1-13, P2-6, P2-15                                                                                                                     |
| **隔离**        | P0-11(钩), P1-1(过渡), P1-9, P1-16(守卫过渡), P2-7, P2-10/P2-11, P2-19/P2-28                                                                            |
| **主动不偿还**     | P2-24(部分), P2-14, P2-25(部分), P2-27（2026-10-20 评估维持，升级触发器见 §5.8）                                                                                                               |
| **顺手清理**      | P1-18, P1-20, P2-22, P2-29（✅ 2026-10-20 已完成，见 §5.9）                                                                                                                       |

## 附录 B：本版实测数据与事实更正

### B.1 复核结论（v1.0 断言仍成立）

> 注：下表为 v2.0 盘点时（**阶段 0 落地之前**）的快照。阶段 0 已于 2026-10-17 全部修复（P0-1~P0-10 + P0-17，见 §5.2），下表「仍存在」已不再成立，列此仅作历史复核留痕。

| 项               | 复核方式                            | 结果                                                                                            |
| --------------- | ------------------------------- | --------------------------------------------------------------------------------------------- |
| P0-1 成本封顶 no-op | 读 `BatchEngine.cs:1406`         | **阶段 0 已修复**：改为 `Math.Min(cost, Math.Max(0m, MaxCostPerTask))`，不变量测试 `兜底实测成本超出单任务上限时按上限截断` 锁住 |
| P0-2 输出未排空      | 读 `CliProcessRunner.cs:213,293` | **阶段 0 已修复**：回调 EOF + `DrainOutputAsync` 门；单测 `RunAsync_大量输出_进程退出后输出尾部不丢失` 锁住                 |
| P1-6 同步阻塞       | grep                            | 2 处确认：`EngineHost.cs:136`、`MainViewModel.cs:20`（属 P1-6，非阶段 0 范围，排期阶段 2）→ **阶段 2 已修复（2026-10-20）**：`MainViewModel` 抽 `InitializeAsync` 异步门 + `EngineHost.RebuildEngine`→`RebuildEngineAsync(CancellationToken)`，新增 IL 架构断言锁住「不得再引入 P1-6 类同步阻塞」，见 §5.5 |
| P0-4/P0-5 并发    | v1.0 结论沿用                       | 未复核，建议偿还前补并发测试确认                                                                              |

### B.2 实测数据（本版新增，均为命令实跑）

| 项                   | 命令                              | 结果                                                               |
| ------------------- | ------------------------------- | ---------------------------------------------------------------- |
| 代码规模                | `find -name "*.cs" \| wc -l`    | 182 文件 / 24,417 行（含 `_harness/obj`；该目录 3 个生成 .cs 已随 P2-29 清理于 2026-10-20 删除，当前源码口径 223 文件 / 35,042 行，见 §5.9）                              |
| 各项目规模               | 逐目录 `wc -l`                     | Core 6,563 / Desktop 5,411 / Infrastructure 4,491 / Agents 1,654 |
| 增量 vs 全量警告          | `dotnet build --no-incremental` | 增量 **0**；全量重建 Core **36 个 CA2007**                               |
| 提交信息                | `git log --format="%s"`         | 12 提交，**10 个为空**                                                 |
| 误提交产物               | `git ls-files \| grep`          | 10 项（见 P0-14）                                                    |
| `catch (Exception)` | grep                            | 37 处（src 内 catch 总数 69）                                          |
| 测试项目引用              | 读 csproj                        | 仅 Core / Infrastructure / Agents，**无 Desktop**                   |

### B.3 对既往记录的事实更正

1. **技术栈**：net10.0 + Avalonia 12.1.4，非 .NET 8 / WPF（v1.0 已更正，此处重申）
2. **缺陷行号**：`docs/CODE-REVIEW-性能瓶颈与Bug审查报告.md` 记 B1 为 `BatchEngine.cs:1011`，实测当前为 **`:1406`**；台账改用不变量描述
3. **"构建 0 警告"不成立**：增量构建不重跑分析器，真实基线为全量重建的 36+（P0-12）
4. **v1.0 未收录 git 历史、仓库卫生、构建真相三个维度**，本版补齐（P0-11~~P0-14、P2-26~~P2-31）
5. **P2-30 台账行号漂移 + 文档断言失真（2026-10-21 更正）**：① 原记「`.editorconfig:24` 将 `CA1002` 设为 `none`」，实测已漂移至 `:31`（同附录 B.3 #2 教训：台账一律改用不变量描述）；② §5.4 阶段 1 边界① 曾写「生产代码 0 豁免」，实测 `Desktop.csproj` 自 `3c20288` 起带 3 项 `NoWarn` 且从未登记——P2-30 本轮偿还 2 项、登记 1 项（§4.7 #10）并更正该表述
