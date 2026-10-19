# BatchAgentRunner 技术债务治理方案 v2.0

> 盘点日期：2026-10-17　｜　取代 v1.0（同日早些时候，未提交）  
> 盘点范围：`src/**`（117 文件 / 18,119 行）、`tests/**`（32 文件 / 5,845 行）、全部工程配置、git 历史（12 提交）  
> 事实基线：net10.0 / Avalonia 12.1.4 / CommunityToolkit.Mvvm 8.4.2，**无 CI、无门禁、无 CPM**
>
> **当前进度（2026-10-19）**：阶段 −1（B 组 5 项）、阶段 0（A 组 10 项 + P0-17）、阶段 1（建网 6 项）**均已完成**；P0 清单 17 项全部清偿，全量构建 0 警告 / 0 错误（`TreatWarningsAsErrors=true`）。**阶段 2 动骨已大部分完成（2026-10-18 ~ 2026-10-19）**：P1-3（提 `IBatchEngine` + 窄化构造）、P1-9（`IProcessRunner`）、P1-10（`IFileSystem`）、P1-16（取消静态注册表）、P1-1（拆 6 协作者）、P1-2（状态机外置）均已提交；**P1-4/P1-5 已收口（2026-10-19）**——`Desktop/Composition/EngineFactory` 成为 Desktop 中唯一 `new` Infrastructure/Agents 具体类型的位置（原 `CredentialVaultHost.CreateDefault`、`EngineHost` 的 `ShutdownService`、`TemplateEditorViewModel` 的 `FileWorkflowTemplateStore` 三处残留装配全部收回组合根），§4.3「Desktop 不得直接 `new` Infrastructure 具体类型」IL 扫描断言落地（Desktop.Tests 3/3 通过）。仅 P1-19（Desktop 补测试）待做。P1-3 为本轮重点，配套 §4.3 断言与 `BatchEnginePhase2Tests` 契约测试已落地，详见 §5.5。测试基线（2026-10-19 晚复跑）：主测试项目 **409 用例**（快 382 / 慢 27）+ Desktop.Tests **39 用例** = **448 通过 / 0 失败**（较 2026-10-19 早前收口轮 441 增加 7 例，来自并发工作方 TaskRunner 防御性终止相关测试调整、尚未提交；本轮复跑未再现测试宿主退出崩溃，历史记录见 §5.5 边界）。

---

## 批准（Approval）

| 项          | 内容                                                                                   |
| ---------- | ------------------------------------------------------------------------------------ |
| **审批状态**   | ✅ 已批准（Approved）                                                                      |
| **批准人**    | 项目负责人（当前 WorkBuddy 会话用户）                                                             |
| **批准日期**   | 2026-10-18                                                                           |
| **批准范围**   | 全文：阶段 −1 / 0 / 1 执行记录、阶段 2 / 3 路线图、§3 偿还策略、§4 防新增六道墙与逃逸阀台账、附录 A/B                    |
| **效力说明**   | 自批准日起，本方案为 BatchAgentRunner 技术债务治理的**权威基线**；任何后续变更须按 §4.6 债务预算与 §4.7 逃逸阀规则登记，不得口头覆盖。 |
| **当前进度基线** | 阶段 −1 / 0 / 1 已完成，P0 清单 17 项全部清偿；阶段 2 动骨大部分完成（P1-3/4/5/9/10/16、P1-1 拆 6 协作者、P1-2 已提交），仅 P1-19 待做（详见 §2 / §5.5）。P1-4/P1-5 于 2026-10-19 收口：组合根 `EngineFactory` 为 Desktop 唯一 `new` 点，§4.3 对应 IL 断言落地。 |

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
| **P0-17** | `tests/BatchAgentRunner.Desktop.Tests/`（新建，引用 Desktop） | 测试项目只引用 Core / Infrastructure / Agents，**唯独不引用 Desktop（5,411 行 = src 的 30%）**                                                                                                                                                                           | Desktop **结构上不可测试**，P0-6/P0-7/P1-7 类缺陷无法用测试锁住，只能靠人眼                              | 中  | 重构（补引用 + 首批冒烟测试）— ✅ 阶段 0 已完成（2026-10-17，新建 Desktop.Tests 引用 Desktop，首批冒烟 12 项）             |

> P0-17 说明：Desktop 不是"没写测试"，而是**测试项目根本没有引用它**——即使想写也写不了。这是可测性缺失，不是测试缺失，故列 P0 而非 P1。

---


### 1.2 P1 — 短期偿还（1–2 个迭代：结构性与可测性）

沿用 v1.0 的 P1-1 ~ P1-15，另补 6 项实测新增（P1-16 ~ P1-21）。

**沿用项（摘要，详见 v1.0 §1.2）**

| ID    | 债务                                                                                                                                                                | 偿还方式                                                                                                                                                                                                                                            |
| ----- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1-1  | `BatchEngine.cs` 1960 行 God Object，16 类职责（变更放大器）                                                                                                                  | 重构（拆 6 协作者，见 §3.4）✅ 阶段 2 已完成（2026-10-19：ITaskDispatcher / ITaskRunner / IFallbackBilling / IGitWorkflow / IBatchReportBuilder+IBatchNotifier / IPreflight，见 §5.5） |
| P1-2  | `ExecuteTaskAsync` 249 行 / `PreflightAsync` 196 行，`while(true)+switch+goto case`                                                                                  | 重构（状态机外置）✅ 阶段 2 已完成（2026-10-19：单任务状态机抽至 ITaskRunner/TaskRunner，见 §5.5）                                                                                                                                        |
| P1-3  | `BatchEngine` 为 `sealed` 且无 `IBatchEngine` 接口，14 参数构造                                                                                                             | 重构（提接口，P1-1 的前置）✅ 阶段 2 已完成（2026-10-19：提 `IBatchEngine` + 构造由 14 参数窄化为「5 必需 + 1 可选 `BatchEngineDependencies` 聚合」，配套 §4.3 断言与 `BatchEnginePhase2Tests` 契约测试，见 §5.5） |
| P1-4  | UI 层直引 Infrastructure/Agents 并 `new` 全部实现类 → **UI 成为组合根**（架构倒置）                                                                                                   | 重构（Core 暴露 `IEngineFactory` + DI）✅ 阶段 2 已完成并收口（2026-10-19：引入 `IEngineFactory`/`IEngineHost`，组合根下沉 EngineFactory；收口将三处残留装配——`CredentialVaultHost.CreateDefault`、`EngineHost` 的 `ShutdownService`、`TemplateEditorViewModel` 的 `FileWorkflowTemplateStore`——全部收回组合根，§4.3「Desktop 不得直接 new Infrastructure 具体类型」IL 扫描断言落地，见 §5.5）                                                                              |
| P1-5  | 无 DI 容器，手工 `new` 7 个 VM + `EngineHost`（sealed 无接口）                                                                                                                | 重构（与 P1-4 合并）✅ 阶段 2 已完成并收口（2026-10-19，随 P1-4：`EngineHost` 实现 `IEngineHost` 契约，页面 VM 只依赖接口；模板库等端口经宿主暴露（`IWorkflowTemplateStore`），`FakeEngineHost` 测试替身同步扩容，见 §5.5）                                                                                                                                                |
| P1-6  | `MainViewModel.cs:20`、`EngineHost.cs:136` `GetAwaiter().GetResult()` 同步阻塞                                                                                         | 重构（异步初始化门）                                                                                                                                                                                                                                      |
| P1-7  | 事件 lambda 订阅**全程无 `-=`**，行 VM 被长生命周期 `TaskItem` 永久持有                                                                                                              | 重构（弱事件 / Dispose）                                                                                                                                                                                                                               |
| P1-9  | `Core/Scheduling/GateEvaluator.cs:114-170` 在 Core 层直接 `new Process()` + `cmd.exe /c "{cmd}"`                                                                      | 隔离（`IProcessRunner` 端口定义落 `Core/Abstractions`、实现 `RealProcessRunner` 落 Infrastructure）✅ 阶段 2 已完成（2026-10-19，见 §5.5）                                                                                                                                                  |
| P1-10 | `BatchEngine.cs:196,226,1531` 绕过自有 `ITaskFileStore` 直接 IO                                                                                                         | 重构（IO 全走抽象）✅ 阶段 2 已完成（2026-10-19：引 `IFileSystem` 端口，见 §5.5）                                                                                                                                                      |
| P1-11 | Vault 目录继承默认 ACL；`manifest.json` 明文；`WriteManifest` 是同类中唯一非原子写                                                                                                    | 重构（收 ACL + 统一原子写）                                                                                                                                                                                                                               |
| P1-12 | `CliPermissionSettings.cs:17-46` 静默把用户 CLI 的 `defaultMode` 改为 `bypassPermissions`（**影响范围不止本工具**）                                                                  | 重构（作用域收窄 + 原子写）                                                                                                                                                                                                                                 |
| P1-13 | `CliProcessRunner.cs:311-322` `KillTree` 依赖外部 `taskkill` + `WaitForExit(3000)` 同步阻塞                                                                               | 重写（.NET 原生 `Kill(entireProcessTree:true)`）                                                                                                                                                                                                      |
| P1-14 | `LocalGitIntegration.cs:80-101` `WaitForExitAsync(ct)` 无超时、无 stdin 重定向                                                                                            | 重构（超时 + stdin 重定向）                                                                                                                                                                                                                              |
| P1-15 | 高危模块零覆盖：`CodeBuddyCreditsApiClient`、`XlsxTaskParser/Writer`、`BackupManager`、`LocalGitIntegration`、`ShutdownService`、`DangerousOperationGuard`、`CliCommandBuilder` | 补齐（先 `XlsxTaskWriter` + `CliCommandBuilder`）✅ 阶段 1 已完成（2026-10-18：XlsxTaskWriter 2 项 + CliCommandBuilder 8 项 + DangerousOperationGuard 6 项，见 §5.4；剩余 `BackupManager`/`LocalGitIntegration`/`ShutdownService`/`CodeBuddyCreditsApiClient` 4 项顺延） |

**本版新增项**

| ID        | 位置                                                                      | 债务                                                                                                                                                       | 影响                                                                          | 成本 | 偿还方式                                                                                                                                                  |
| --------- | ----------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- | -- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| **P1-16** | `Infrastructure/CredentialVault/CredentialLocationRegistry.cs:40,72,74` | `public static class` + `static readonly Dictionary` + `public static void SetLiveRootOverride(...)`；生产代码 `Desktop/Services/EngineHost.cs:94` 调用它做全局路径注入 | Service Locator 反模式 + **非线程安全 `Dictionary`**；全局状态被 UI 生命周期意外改写（原 P1-8 的实体化） | 中  | 重构（显式传入，取消静态入口）✅ 阶段 2 已完成（2026-10-19：改为显式注入实例服务，见 §5.5）                                                                                                       |
| **P1-17** | 5 个 csproj                                                              | **无 `Directory.Packages.props`（无 CPM）**，包版本手写散落；`Core.csproj` 引用 `Microsoft.Extensions.Logging.Abstractions 10.0.13` 而全仓 `ILogger` 使用 **0 处**（无效依赖）      | 版本漂移；依赖图不可审计                                                                | 小  | 重构（引入 CPM + 删无效依赖）✅ 阶段 1 已完成（2026-10-18：`Directory.Packages.props` + `ManagePackageVersionsCentrally=true`；任何 csproj 再写 Version 即 NU1008 构建错误，结构性防漂移） |
| **P1-18** | `Infrastructure/Persistence/JsonCreditsClaimStore.cs:21`                | 第 18 行 `directory ?? ...` 建目录，第 21 行却 `Path.Combine(directory!, ...)` → 默认构造**必抛 `ArgumentNullException`**；生产唯一调用点 `EngineHost.cs:202` 恰好总传参，故未暴露        | 定时炸弹：任何走默认构造的新调用点立刻崩                                                        | 极小 | 重构（1 行）✅ 阶段 1 已完成（2026-10-18，修复 + 不变量测试 2 项）                                                                                                          |
| **P1-19** | Desktop 层（churn 实测）                                                     | `ConfigView.axaml`、`TemplateEditorViewModel.cs`、`ReportViewModel.cs`、`ConfigViewModel.cs` 等 4 次提交被改（全仓仅 12 提交，即 1/3 提交都动过），且**零测试**                      | 界面契约未稳定却已铺开 5,411 行 → 重构 Desktop 时回归面最大                                     | 大  | 重构（补测试 + 收敛契约）⏳ **阶段 2 唯一剩余项**（P1-3~P1-16/P1-1/P1-2 均已完成，仅 P1-19 待做，见 §5.5）                                                                                    |
| **P1-20** | `Desktop/Converters/ConfigHubConverters.cs:18,31`                       | 两处 `ConvertBack => throw new NotImplementedException()`                                                                                                  | 双向绑定一旦触发即崩溃                                                                 | 极小 | 重构 ✅ 阶段 1 已完成（2026-10-18：返回 `BindingOperations.DoNothing` + 不变量测试）                                                                                    |
| **P1-21** | `Core/Options/RunOptions.cs:264,268,271`                                | 生产 API 地址 `https://www.codebuddy.cn` 及 `/billing/meter/*` 路径写进默认值                                                                                        | 环境切换只能靠覆盖，测试易打到生产                                                           | 小  | 重构（抽配置/常量）                                                                                                                                            |

---

### 1.3 P2 — 机会主义偿还（随任务带走）

沿用 v1.0 的 P2-1 ~ P2-25，另补 6 项。

**沿用项（摘要）**：P2-1~~P2-6（重复实现：`Truncate` 三份、终态集合三份、失败分派状态机逐字重复、`Clone` 六份手写）、P2-7（`TaskItem : INotifyPropertyChanged` 领域层被 UI 契约侵入）、P2-8（`FakeClock` 进生产程序集）、P2-9（40+ 配置项 God Config，`Clone()` 靠 JSON 往返）、P2-10~~P2-15（UI 一致性：硬编码色值绕过 token、无 `x:DataType` 反射绑定、按中文字面量着色、手写 DTO 编辑器）、P2-20~P2-25（测试分层缺 `Trait`、Flaky 三件套、`*.xlsx` 未标 binary、性能项、死代码）。

**本版新增项**

| ID        | 位置                                                                                                                                                                                           | 债务                                                                                                                                                                                                                                                     |
| --------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **P2-26** | 全仓 147 处中文注释（`二期` / `暂不支持` / `遗留` / `尚未`）                                                                                                                                                    | **计划性延期未进台账**，且已与实现脱节：`Core/Domain/TaskItem.cs:10` 仍写"一期**不实现** workflow/depends_on/max_loops"，但 `Domain/WorkflowTypes.cs`(512 行)、`Scheduling/DagScheduler.cs`、`BatchEngine.cs` 的 DAG 派发**已全部实现**；`BatchEngine.cs` 中 20+ 处注释仍自称"二期扩展"。**误导性注释比无注释更危险** |
| **P2-27** | `tests/**` 252/252 个测试方法为中文名                                                                                                                                                                 | `dotnet test --filter` / CI 报表 / 日志 diff 在非 UTF-8 环境下易碎                                                                                                                                                                                                |
| **P2-28** | `samples/`（13 文件）                                                                                                                                                                            | 双重角色：既是用户示例，又被测试当契约（`RunOptionsSerializationTests.cs:95` 断言 `samples/config.example.json` 与代码同步）→ 改示例会断测试，改代码要同步示例                                                                                                                                     |
| **P2-29** | `_harness/`（214 文件）                                                                                                                                                                          | 已 ignore 的 publish 产物堆（Avalonia DLL + exe），与 `samples/` 职责重叠，无人清理                                                                                                                                                                                      |
| **P2-30** | `.editorconfig:24` 将 `CA1002` 设为 `none`；`NotificationDispatchers.cs:101` `#pragma warning disable SYSLIB0014`（过时 `SmtpClient`，注释自认"二期换 MailKit"）                                             | 规则豁免无登记、无到期日                                                                                                                                                                                                                                           |
| **P2-31** | `JsonCooldownStore` / `JsonCreditsClaimStore` / `JsonConfigStore` 三份逐行同构（构造 + 默认目录 + `LoadAsync` catch(JsonException) + `AtomicFileWriter.SaveAsync`）；`"BatchAgentRunner"` 路径魔法串在 **9 处**硬编码 | 改一处必漏改两处                                                                                                                                                                                                                                               |

**P2 中三项已随阶段 1「建网」提前偿还（2026-10-18，见 §5.4）**：  
P2-20（测试分层：E2E/Usability/Process 打 `Trait`，CI 快跑单元 / 慢跑集成）、  
P2-21（去 Flaky：真实进程 → `IProcessLauncher` 注入缝；samples → 嵌入式资源副本）、  
P2-28（samples 降级为只读示例，测试契约改读嵌入式副本，内容经 csproj `Link` 随 samples 同步）。

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
P1-1  BatchEngine 按 6 协作者拆分（strangler fig，见 §3.4）              ✅ 已完成（2026-10-19）
   ↓
P1-2  状态机外置                                                          ✅ 已完成（2026-10-19）
   ↓
P1-19 Desktop 补测试 + 契约收敛                                            ⏳ 待做（阶段 2 唯一剩余项）
```

> ✅ **阶段 2 已于 2026-10-18 ~ 2026-10-19 大部分完成**（P1-3/4/5/9/10/16、P1-1 拆 6 协作者、P1-2 全部提交；仅 P1-19 待做）。执行记录与验证口径见 §5.5。P1-3 为本轮治理重点：提 `IBatchEngine` 目标接口 + 构造窄化 + 配套 §4.3 架构断言（`Desktop` 除组合根 `EngineFactory` 外不得直引 `BatchEngine` 具体类型）+ `BatchEnginePhase2Tests` 契约测试。

**护栏**（每一步都要满足）：

- 保持 `BatchEngine` 外壳存在（先转门面，最后再删），确保每步可编译、现有全量测试通过
- 每步结束跑 `git diff --stat`，**单步 > 400 行则回退重切**（说明切得不够细）
- 每步必须有提交信息（阶段 −1 已强制）

### 阶段 3：持续化（第 4 个迭代起，长期）

1. 机会主义偿还 P2 各项（§3.5）
2. 阶段 2 拆出的 5 个协作者各自补齐单测，逐步摆脱对 `BatchEngine` 黑箱测试的依赖
3. 台账月度复核，按 §4.7 看趋势而非绝对值

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
| 中文测试方法名（P2-27）                         | 现状一致且可读；仅在 CI 出现编码问题时批量处理 |

---

## 4. 防新增机制（本方案的重心）

存量修得再干净，没有这层，下个季度回到原点。

### 4.1 第一道墙：提交门禁（写不进）—— 本版新增

针对实测的「10/12 提交信息为空」：

- `commit-msg` 钩：拒绝空消息、拒绝纯标点、要求正文含「改了什么 / 为什么」✅ 阶段 −1 已完成
- `pre-commit` 钩：`dotnet format --verify-no-changes` ✅ **阶段 1 已完成（2026-10-18，`.githooks/pre-commit`）**。落地细节与已知 SDK 限制见 §5.4「格式门禁」行与 §4.7 台账 #9（CHARSET 假阳性过滤）。
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

> 「Desktop 不得直接 new」断言的实现口径（2026-10-19）：用反射自建最小 IL walker（`OpCodes` 反射建查表，逐指令步进），扫描 Desktop 程序集全部方法体（含构造器 / 属性访问器 / 编译器生成的闭包与异步状态机）中的 `newobj` / `newarr`，解析元数据 token 得目标类型；`Desktop.Composition` 命名空间（组合根）为唯一豁免。**按规则本意排除委托实例化**：捕获型 lambda 会被编译器转换为 `newobj`（如 `CredentialVaultHost.GuidedLoginAsync` 把校验回调传给 `RunAsync`），源码并无 `new`，且回调委托类型是协作者签名强制的契约、无法按「须经接口」替换——规则目标是「UI 不得实例化 Infrastructure/Agents 实现类」，不覆盖编译器产物。

### 4.4 第四道墙：复杂度门禁（CI 不过）

**阈值先按现状标定到「不阻断现有代码中的合理部分」，再逐季度收紧**。一上来设理想值会导致团队习惯性 `// NOLINT` 越过，门禁形同虚设。

| 指标     | 建议阈值（含宽容量） | 当前最坏值                   | 拦截目标            |
| ------ | ---------- | ----------------------- | --------------- |
| 单文件行数  | ≤ 800      | 1960（`BatchEngine.cs`）  | 防下一个 God Object |
| 单方法行数  | ≤ 60       | 249（`ExecuteTaskAsync`） | 防超长方法           |
| 圈复杂度   | ≤ 15       | >35（`ExecuteTaskAsync`） | 防不可测分支          |
| 构造参数个数 | ≤ 7        | 14（`BatchEngine`）       | 防依赖爆炸           |
| 类公开成员数 | ≤ 30       | 60+（`BatchEngine`）      | 防 God Object    |

### 4.5 第五道墙：PR 评审清单（合不进）

在 `docs/CODE-REVIEW-代码审查标准与流程.md` 追加，PR 模板强制勾选：

- [ ] 本次改动是否让任何文件的行数/复杂度上升？若是，说明理由
- [ ] 是否新增 `catch { }`，或 `catch(Exception)` 后仅记 WARN？（当前 42 处，是排障困难主因）
- [ ] 是否新增可变 `static` 状态？（P1-16 类）
- [ ] 是否新增 `.GetAwaiter().GetResult()` / `.Result` / `.Wait()`？（P1-6 类）
- [ ] 后台线程 `await` 后是否直接改了 UI 集合？（P0-6 类）
- [ ] 是否新增文件读写而未走 `AtomicFileWriter`？（P0-8 类）
- [ ] 是否拼接命令行字符串而非 `ArgumentList`？（P0-3 类）
- [ ] 是否新增硬编码色值/魔法数字/绝对路径，而非引用 token/常量？
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

- 例外一律显式：`#pragma warning disable` / `[SuppressMessage]` / `// NOLINT` **必须附理由 + 到期日 + 责任人**
- CI 统计**逃逸数**并纳入看板（当前基线：`#pragma` 1 处、`.editorconfig` 规则置 `none` 1 处（CA1002）、`NoWarn` 1 项）
- 到期未清理的逃逸，自动升级为该迭代的 P1

**逃逸阀台账（2026-10-18 阶段 1 更新，共 9 项）**：

| # | 逃逸                                                    | 位置                                                       | 理由（为什么必须豁免）                                                                                                                                                               | 到期日 / 责任人            |
| - | ----------------------------------------------------- | -------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------- |
| 1 | `#pragma warning disable SYSLIB0014`（过时 `SmtpClient`） | `Infrastructure/Notification/NotificationDispatchers.cs` | 注释自认「二期换 MailKit」；.NET 仍支持 `SmtpClient`，换库属功能迁移非缺陷修复                                                                                                                      | 二期换 MailKit 时 / 工程   |
| 2 | `.editorconfig` 将 `CA1002` 置 `none`                   | `.editorconfig:24`                                       | 既有豁免（阶段 −1 基线）：`List<T>` 公开返回是产品 API 契约的一部分                                                                                                                               | 待评估，未登记到期日 / 工程      |
| 3 | `NoWarn CS1591`（公共 API 缺文档注释）                         | `Directory.Build.props`                                  | 全仓公共 API 文档注释未补齐，一次性开启会爆数百警告；与 P0-15 同期决策「先 warning 不阻断」同源                                                                                                                | 阶段 2 收尾 / 工程         |
| 4 | `NoWarn CA1707`（标识符含下划线）                              | 两个测试 csproj                                              | 测试项目专用：「下划线 + 中文名」是本仓测试命名约定，对生产代码不适用                                                                                                                                      | 长期有效 / 工程            |
| 5 | `NoWarn CA2007`（await 须 ConfigureAwait）               | 两个测试 csproj                                              | 测试项目专用：与 xUnit 自家 `xUnit1030` 直接冲突——xUnit 明确建议测试方法**省略** `ConfigureAwait`（可 bypass 并行化限制）；故阶段 1 同步删除了测试中全部 `ConfigureAwait(false)`                                        | 长期有效 / 工程            |
| 6 | `NoWarn CA1861`（常量数组提 static readonly）                | 两个测试 csproj                                              | 测试项目专用：该规则针对生产热路径的重复分配；测试期望值数组一次性使用且内容各异，提字段损害可读性且无性能收益                                                                                                                   | 长期有效 / 工程            |
| 7 | `[SuppressMessage] CA1720`（`ExpertKind.Single` 含类型名）  | `Core/Domain/ExpertRef.cs`                               | 持久化兼容：`ExpertKind` 经 `JsonStringEnumConverter` 以字符串形式随用户模板落盘，重命名破坏既有模板（同 `RunOptions` 契约保护原则）                                                                             | 长期有效 / 工程            |
| 8 | `[SuppressMessage] CA1859`（接口字段/返回改具体类型）              | `Desktop/Composition/EngineFactory.cs`（1 处）+ `Core/Engine/BatchEngine.cs`（6 处，P1-1 协作者测试缝） | 架构方向：接口类型是 P1-4/P1-5「UI 只依赖 Core 抽象 + DI 化」的目标形态，拒绝为微性能向具体类型回退（P1-4/P1-5 收口后 EngineHost 已无此类豁免，余下两处为组合根返回类型与协作者测试缝） | **2026-10-19 收口时已复核：保留（长期有效）/ 工程** |
| 9 | `dotnet format` CHARSET 类诊断过滤（pre-commit 钩）           | `.githooks/pre-commit`（`grep -v 'error CHARSET'`）        | SDK 10.0.401 回归：`Directory.Build.props` 的 `CodePage=65001`（中文源码跨代码页编译所需，保留）触发 CharsetFixer 对全部 .cs 文件误报，且修复器为空操作（最小复现：同内容去掉 CodePage 即 0 误报）。钩子改用显式 `iconv` UTF-8 有效性校验补偿 | SDK 修复该回归后移除过滤 / 工程  |

> 说明：P2-30 登记的 `.editorconfig` CA1002 与 `#pragma` SYSLIB0014 在阶段 1 的警告清零中**保留原样**（不属 CA 警告类），正式登记为本表 #1/#2。

### 4.8 度量与趋势看板

每迭代采集，**看趋势而非绝对值**：

| 指标                     | 当前基线（实测）                                                                                                                                                                   | 目标方向     |
| ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------- |
| 最大文件行数                 | **1065**（`ConfigViewModel.cs`，Desktop；2026-10-19 复测——`BatchEngine.cs` 经 P1-1 拆分降至 1036 后不再是最大文件， Desktop 长文件成为新头部）                                                                                          | ↓        |
| 最大方法行数                 | **287**（`TaskRunner.ExecuteTaskAsync`，2026-10-19 复测；P1-2 将状态机从 BatchEngine 外置但未再做方法内拆分，仍超 §4.4 阈值 60——登记为阶段 3 / 机会主义偿还候选）                                                                                                | ↓        |
| 全量构建警告数                | **0**（2026-10-18 阶段 1 清零：P0-12 完成，`TreatWarningsAsErrors=true` 生效，任何新增警告即构建失败；2026-10-19 P1-4/P1-5 收口后复测仍 0）                                                                             | → 0 并保持  |
| `catch (Exception)` 占比 | **src 44 处**（2026-10-19 复测，较阶段 1 的 42 略升；占比分母随 P1-1 拆分变化，待精确复算）                                                                                  | ↓        |
| 高危模块零覆盖数               | 7 → **4**（阶段 1 补齐 XlsxTaskParser/Writer、CliCommandBuilder、DangerousOperationGuard；余 CodeBuddyCreditsApiClient / BackupManager / LocalGitIntegration / ShutdownService 4 项） | ↓ 至 0    |
| Desktop 测试数            | **39 用例**（2026-10-19 实跑 Desktop.Tests 全绿：架构断言 3 + VM 白箱（Config/ConfigHub/Report/TemplateEditor）+ 转换器 / 多值文本；阶段 2 随 P1-19 铺路从 13 增长）                                                              | ↑        |
| 空提交信息数 / 迭代            | 10/12 历史；阶段 −1 起由 commit-msg 钩强制「非空 + 含改了什么/为什么」                                                                                                                           | → 0 并保持  |
| 误提交产物数                 | 10；阶段 −1 已 `git rm --cached` 并 ignore，当前 0                                                                                                                                 | → 0 并保持  |
| P0 未偿数                 | 17 → 阶段 −1 完成 5 项、阶段 0 完成 11 项、阶段 1 完成最后 1 项（P0-12），当前 **0**                                                                                                               | → 0 并保持  |
| 每迭代**新增**债务数           | 未采集（阶段 −1 起采集）                                                                                                                                                             | → 趋 0    |
| churn 热点 Top3          | `BatchEngine.cs`(4)、`RunOptions.cs`(4)、`ConfigView.axaml`(4)                                                                                                               | 热点应随拆分下降 |
| 测试总数                   | **448 用例通过 / 0 失败**（2026-10-19 晚复跑：主测试项目 409 = 快 382 + 慢 27，Desktop.Tests 39；较 441 增加 7 例系并发工作方 TaskRunner 防御性终止测试调整，实跑以 CI 复跑为准）                                                                     | ↑        |

> **台账登记改用不变量描述，不用行号**。例：P0-1 登记为「`TryFallbackAsync` 中 `cost` 必须被 `MaxCostPerTask` 截断」并配断言测试。理由：既有审查报告记录 `:1011`，实测已漂移到 `:1406`，行号型台账必然失效。

### 4.9 引入顺序（避免门禁一次性压垮团队）

```
当天   : 提交规范 + gitignore 目录级 + 清产物 + 编译期分析 + 最小 CI（阶段 −1 的 1–5，已全部完成 2026-10-17）
迭代1  : CI（--no-incremental，只报不阻断）+ 格式门禁 + 测试分层 + 盲区补齐（已完成 2026-10-18，见 §5.4；格式门禁 = pre-commit 钩 + end_of_line/eol=lf 约定，CHARSET 假阳性按 §4.7 #9 过滤）
迭代2  : EnforceCodeStyleInBuild + ArchUnitNET 断言（Core 优先）
迭代3  : 复杂度阈值按基线标定（先 warning） + 逃逸阀登记
迭代4  : 复杂度阈值转 error + TreatWarningsAsErrors 分批开启（已提前完成 2026-10-18：存量清零后 TreatWarningsAsErrors=true）
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
| P0-16 | 新增 `.github/workflows/ci.yml`：`windows-latest` + .NET 10，`dotnet build --no-incremental` + `dotnet test`，**只报告不阻断**                                                       | 工作流文件已就位；门禁转阻断（必需状态检查）留待阶段 1（§4.9）                                                           |

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

> 阶段 0 边界（交接必读）：① 仅止血，未动 `BatchEngine` 骨架（P1-1~P1-3 仍待阶段 2）；② ~~全量构建仍有 48 个 CA 警告~~（2026-10-18 更新：阶段 1 已清零并开启 `TreatWarningsAsErrors`，见 §5.4 / P0-12）；③ 不变量测试已锁住「一眼看不出是错的」类缺陷（P0-1/2/3/4/5/8/9/10），但 Desktop 线程收敛（P0-6/7）目前靠编译期收口 + 复用约定保证，尚未有运行时断言测试（需阶段 2 引入 Avalonia 头less 测试基座，见 P1-19）。

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

> 阶段 1 边界（交接必读）：① CA2007/CA1861/CA1707 在**测试项目**经 NoWarn 豁免（xUnit1030 与 CA2007 直接冲突等原因，已登记 §4.7）；生产代码 0 豁免。② `IProcessLauncher` 目前只被 `CliProcessRunner` 消费，GateEvaluator 内的 `cmd.exe` 直拉（P1-9）仍待阶段 2 抽 `IProcessRunner`。③ 分支保护 / 必需状态检查仍未配置（GitHub 设置），CI 红不阻断合并——这是 P0-16「只报告不阻断」的最后残留，需仓库管理员在 GitHub 页面完成。④ `TreatWarningsAsErrors` 为全量开启而非 §4.2 的分批（存量清零后分批前提已消失，见 §4.9 注）。⑤ `pre-commit` 钩与 commit-msg 钩同为**本机生效**（`core.hooksPath` 写在本机 `.git/config`，clone 不自动继承），新人接入需执行 `git config core.hooksPath .githooks`；CI runner 不跑钩子，靠 `TreatWarningsAsErrors` + CI build 步骤兜底。⑥ CHARSET 过滤依赖 SDK 10.0.401 的回归行为，升级 SDK 后需复测（§4.7 #9）。

---

### 5.5 阶段 2（2026-10-18 ~ 2026-10-19，动骨）

> **本轮重点：P1-3（提 `IBatchEngine` 接口 + 窄化构造）**。P1-3 是 strangler fig 的前置：先有稳定接口面，后续每个拆分步骤才能以「接口契约不变」增量验证。P1-3 的改造（接口提取 + 构造窄化 + 契约测试）已在 prior session 提交（`1ff7c4e`），本轮（2026-10-19）补齐其配套 **§4.3 架构断言**并使文档与代码一致。**同轮次完成 P1-4/P1-5 收口**（见下表「P1-4/P1-5 收口」行）：`5d0fb14` 引入 `IEngineFactory`/`IEngineHost` 时组合根并未真正唯一——`CredentialVaultHost.CreateDefault` 等三处仍在 Desktop 层自行 `new` Infrastructure 具体类型，且 §4.3 表格中「Desktop 不得直接 new」断言一直未落地；本轮将装配全部收回组合根并补上该断言，P1-4/P1-5 才真正完成。

**验证统一口径（2026-10-19 收口轮实跑）**：`dotnet build --no-incremental` = **0 警告 / 0 错误**（全仓，含新增断言）；`dotnet test tests/BatchAgentRunner.Desktop.Tests` = **39 通过 / 0 失败**（含架构断言 3/3）；主测试项目分层实跑 = **402 通过 / 0 失败**（快 375 + 慢 27）。**已知环境问题**：主测试项目快速 bucket 跑完（375 全过）后测试宿主进程在退出时崩溃（与全量跑同源，疑似进程类测试残留），结论以「已执行用例全绿」为准；慢速 bucket 可完整跑完。**P1-9/P1-10/P1-16 复核轮（2026-10-19 晚）复跑**：全量构建 **0 警告 / 0 错误**；主测试分层实跑 **409 通过 / 0 失败**（快 382 + 慢 27）+ Desktop.Tests **39 通过 / 0 失败** = **448 / 0**（较收口轮 +7：并发工作方 TaskRunner 防御性终止相关测试调整，尚未提交）；本轮快速 bucket 跑完未再现宿主退出崩溃。

| ID        | 落地内容                                                                                                                                                                                                                                                                                                    | 验证 / 提交                                                                                                                                                    |
| --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1-3      | 提 `IBatchEngine` 目标接口（`Core/Abstractions/IBatchEngine.cs`，接口面与 `BatchEngine` 公共面严格一致，`IDisposable`）；`BatchEngine` 改为 `sealed class BatchEngine : IBatchEngine`；构造由 14 参数窄化为「5 必需（`RunOptions`/`IAgentCliAdapter`/`ITaskFileStore`/`ICooldownStore`/`IRunLogger`）+ 1 可选 `BatchEngineDependencies` 聚合」，聚合成员 `null` = 用默认实现，纯签名重构、行为不变 | 提交 `1ff7c4e`；`BatchEnginePhase2Tests`（14 方法）锁定公开行为；本轮新增 §4.3 断言（`Desktop` 除组合根 `EngineFactory` 外不得直引 `BatchEngine` 具体类型）通过 |
| P1-4/P1-5 | 引入 `IEngineFactory`/`IEngineHost`，组合根下沉到 `Desktop/Composition/EngineFactory`（唯一允许 `new` Infrastructure/Agents 具体类型与 `new BatchEngine` 的位置）；`EngineHost` 只持 `IBatchEngine`                                                                                                                                        | 提交 `5d0fb14`（`192eec6` 配套 §4.3 断言：`EngineHost` 不得持 Infrastructure/Agents 具体类型）                                                               |
| P1-4/P1-5 收口 | **组合根唯一化**：`5d0fb14` 后仍有三处「第二组合根」残留——① `CredentialVaultHost.CreateDefault`（5 个 Infrastructure/Agents `new`）→ 装配上移 `EngineFactory.CreateVaultHost`，宿主改纯构造注入，`Orchestrator` 收敛为 private（不再经 `IEngineHost` 泄漏 Infrastructure 具体类型）；② `EngineHost` 的 `new(new ShutdownService())` → 改 `EngineFactory.CreateShutdownCoordinator()`；③ `TemplateEditorViewModel` 的 `new FileWorkflowTemplateStore()` → 改经 `IEngineHost.TemplateStore`（`IWorkflowTemplateStore` 端口，`FakeEngineHost` 补零 IO 桩）。**§4.3 断言落地**：新增「Desktop 除组合根外不得直接 new Infrastructure/Agents 具体类型」IL 扫描断言（反射 `OpCodes` 自建 walker，覆盖构造器/访问器/闭包/异步状态机；捕获型 lambda 的委托实例化按规则本意排除） | 2026-10-19；Desktop.Tests 39/39（新断言 3/3）、主测试 402/402、全量构建 0 警告 / 0 错误；`dotnet format` 本轮文件 0 问题 |
| P1-9      | 进程 IO 赶出 Core：`IProcessRunner` 端口落 `Core/Abstractions/IProcessRunner.cs`（端口与默认实现同驻 Core，同 `IClock`/`IFileSystem` 模式），唯一允许 `new Process()` 的 `RealProcessRunner` 落 Infrastructure，Core 内留 `NullProcessRunner` 惰性默认（未显式注入时命令门禁直接通过）；`GateEvaluator` 改构造注入不再直拉 `cmd.exe`，引擎侧经 `BatchEngineDependencies.ProcessRunner` 接线 | 提交 `e1d82c8`；§4.3 断言「Core 不得出现 `System.Diagnostics.Process`/`ProcessStartInfo`」（成员 + IL 局部变量反射）随阶段 1 落地并覆盖本轮；`GateEvaluatorTests` 10 项（FakeProcessRunner：退出码/期望退出码/输出正则/失败模板/审查/人工门禁）全绿 |
| P1-10     | 引擎内 IO 全走抽象：引 `IFileSystem` 端口（`Core/Abstractions/IFileSystem.cs` + `SystemFileSystem` 默认实现，同 `IClock` 模式），经 `BatchEngineDependencies.FileSystem` 注入；`BatchEngine` 原三处绕过 `ITaskFileStore` 的直接 IO 全部改走端口（任务文件读写仍走 `ITaskFileStore`），`BatchEngine` 零直接 IO | 提交 `13aed90`；`BatchEngineTests` 不变量 2 项（任务文件存在性 / 工作空间存在性必须经 `IFileSystem` 端口，`FakeFileSystem` 桩验证不碰真实磁盘） |
| P1-16     | 取消 `CredentialLocationRegistry` 静态覆写：`public static class` + 静态 `Dictionary` → `sealed class : ICredentialLocationRegistry` 实例服务，`SetLiveRootOverride` 覆写状态改实例 `ConcurrentDictionary`（线程安全、无跨实例全局副作用）；规格表 `Specs` 为不可变静态数据（全部实例共享）。组合根 `EngineFactory` 持唯一实例并一次性覆写 Mock Live 根，Vault / 引导登录 / CLI 适配器 / 积分领取全链路显式注入同一实例 | 提交 `6ca29c0`；src 内零静态调用残留（grep 实证，仅剩文档注释引用）；凭证域 6 个测试文件（VaultSwitch / VaultSnapshot / MockVaultE2E / GuidedLoginFlow / CreditsClaim / CliPermissionSettings）改注入实例后全绿 |
| P1-1      | 按 **6** 协作者拆分（原路线图记 5，实际多抽出一个 `IPreflight`）：`ITaskDispatcher`（派发主循环/受控并发/DAG）、`ITaskRunner`（单任务状态机+终态写入+兜底计费+凭证/组合切换）、`IFallbackBilling`（兜底成本/用量）、`IGitWorkflow`（建分支/开 PR/Diff/Markdown 回写）、`IBatchReportBuilder`+`IBatchNotifier`（报告/通知）、`IPreflight`（启动前预检） | 提交 `0e5aba5`/`b43a65a`/`afc6943`/`1f0caa6`/`3d76083`（协作者白箱测试 `b372d60`/`0b7c194`）；`BatchEngine` 由 1960 行降至 1036 行                             |
| P1-2      | 状态机外置：单任务状态机（重试/退避/降级/超时+终态写入）抽至 `ITaskRunner`/`TaskRunner`，`BatchEngine` 仅保留批处理级协调（落盘单写者、waiting 门、状态广播）                                                                                                                                                          | 提交 `e7c9eb9`                                                                                                                                                |
| P1-19     | ⏳ **未做**（阶段 2 唯一剩余项）：Desktop 补测试 + 契约收敛（原 churn 热点，零测试）；阶段 2 已铺路：`IEngineHost` 替身（`FakeEngineHost`）+ VM 白箱 + 架构断言共 39 用例，Avalonia headless 基座与线程收敛契约仍未补                                                                                                                        | 留作下一轮；路线图见 §2                                                                                                                                              |

> 阶段 2 边界（交接必读）：① 路线图原计划 P1-1「拆 5 协作者」，实际抽了 **6** 个（多一个 `IPreflight`），`BatchEngine` 外壳仍保留为 `IBatchEngine` 门面，对外契约不变，可整体回退。② 构造窄化是纯签名重构：可选依赖聚合 `null` 语义与原逐个 `?? new …` 回退严格一致，行为零变化。③ 全量测试的快速 bucket 可跑完且全绿（375/375），但宿主进程在退出时崩溃（老问题，与是否包含本轮改动无关——改动前同样崩溃）；慢速 bucket 完整通过（27/27）；实际用例数以 CI 复跑为准（§4.8 现按 448 实跑登记）。④ P1-19 待做不影响已提交项的正确性；Desktop 已有 39 用例但线程收敛等运行时契约仍无 headless 断言。⑤ P1-4/P1-5 收口新增发现（2026-10-19 复测）：`TaskRunner.ExecuteTaskAsync` 287 行，超 §4.4 单方法阈值——P1-2 只做了「外置」未做「内拆」，登记为阶段 3 / 机会主义偿还候选（§4.8「最大方法行数」）。⑥ P1-9/P1-10 边界（2026-10-19 复核轮）：`Core/Scheduling/TemplateExpander.cs` 仍有直接 `File`/`Directory` IO（CSV 读取 + glob 目录枚举），但其仅被 Desktop 模板编辑器预览调用、不在引擎运行路径，且属 P1-10 台账范围（`BatchEngine` 原三处）外——如实登记、不扩范围；若后续模板预览也要可测，随 P2 机会主义偿还一并收口。

---

## 附录 A：台账索引（按偿还方式分组）

| 方式            | ID                                                                                                                                               |
| ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| **重构**        | P0-1, P0-3~~P0-10, P0-12~~P0-15, P0-17, P1-1~~P1-5, P1-7, P1-9~~P1-12, P1-14~~P1-21, P2-1~~P2-14, P2-16~P2-21, P2-23, P2-24, P2-26, P2-28, P2-31 |
| **重写**（仅 4 项） | P0-2(局部), P1-13, P2-6, P2-15                                                                                                                     |
| **隔离**        | P0-11(钩), P1-1(过渡), P1-9, P1-16(守卫过渡), P2-7, P2-10/P2-11, P2-19/P2-28                                                                            |
| **主动不偿还**     | P2-24(部分), P2-14, P2-25(部分), P2-27                                                                                                               |
| **顺手清理**      | P1-18, P1-20, P2-22, P2-29                                                                                                                       |

## 附录 B：本版实测数据与事实更正

### B.1 复核结论（v1.0 断言仍成立）

> 注：下表为 v2.0 盘点时（**阶段 0 落地之前**）的快照。阶段 0 已于 2026-10-17 全部修复（P0-1~P0-10 + P0-17，见 §5.2），下表「仍存在」已不再成立，列此仅作历史复核留痕。

| 项               | 复核方式                            | 结果                                                                                            |
| --------------- | ------------------------------- | --------------------------------------------------------------------------------------------- |
| P0-1 成本封顶 no-op | 读 `BatchEngine.cs:1406`         | **阶段 0 已修复**：改为 `Math.Min(cost, Math.Max(0m, MaxCostPerTask))`，不变量测试 `兜底实测成本超出单任务上限时按上限截断` 锁住 |
| P0-2 输出未排空      | 读 `CliProcessRunner.cs:213,293` | **阶段 0 已修复**：回调 EOF + `DrainOutputAsync` 门；单测 `RunAsync_大量输出_进程退出后输出尾部不丢失` 锁住                 |
| P1-6 同步阻塞       | grep                            | 2 处确认：`EngineHost.cs:136`、`MainViewModel.cs:20`（属 P1-6，非阶段 0 范围，排期阶段 2）                       |
| P0-4/P0-5 并发    | v1.0 结论沿用                       | 未复核，建议偿还前补并发测试确认                                                                              |

### B.2 实测数据（本版新增，均为命令实跑）

| 项                   | 命令                              | 结果                                                               |
| ------------------- | ------------------------------- | ---------------------------------------------------------------- |
| 代码规模                | `find -name "*.cs" \| wc -l`    | 182 文件 / 24,417 行（含 `_harness/obj`）                              |
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
