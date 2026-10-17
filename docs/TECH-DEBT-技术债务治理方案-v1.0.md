# BatchAgentRunner 技术债务治理方案 v1.0

> 盘点日期：2026-10-17  
> 盘点范围：`src/**`（107 文件）、`tests/**`（38 文件）、全部工程配置  
> 事实基线：**net10.0 / Avalonia 12.1.4 / CommunityToolkit.Mvvm 8.4.2**（非 .NET 8、非 WPF），无 CI，无编译期门禁

> **治理进度（2026-10-17 更新）**
> - **§2 阶段 0（止血，9 项 / 含 P0-1~P0-10）已全部完成**：`dotnet build` 0 错误，**316/316 测试通过**
>   （基线 270 为本次治理前 `HEAD` 实测值，全场新增 **46** 例，其中 27 例是本阶段落地的断言式不变量测试）
> - **阶段 1（CI / 编译门禁 / 测试分层）尚未开始**，本文档 §3/§4 仍是待执行计划
> - 后续行号漂移不再影响台账有效性：P0 各项的追踪一律走「不变量断言」（见下面落地表的「护栏」列）

---

## 0. 结论摘要

| 指标   | 现状                                                              | 判断          |
| ---- | --------------------------------------------------------------- | ----------- |
| 代码规模 | 152 文件 / ~24k LOC                                               | 中等规模，尚在可控区间 |
| 最大单点 | `BatchEngine.cs` 1960 行 = Core 的 30%，承担 **16 类职责**              | 变更放大器       |
| 致命缺陷 | 2 个（成本封顶失效、进程输出竞态）                                              | 静默错误，用户无感知  |
| 安全缺陷 | 3 个高危（命令注入、凭证无 ACL、凭证无备份删除）                                     | 本机提权/凭证丢失   |
| 门禁   | `TreatWarningsAsErrors=false`、`EnforceCodeStyleInBuild` 未开、无 CI | **债务持续产生**  |
| 测试   | 38 个测试文件，但 7 个高危模块零覆盖                                           | 重构无安全网      |

**核心判断**：债务的**存量**不是主要风险，**增量**才是——当前无任何机制阻止新债务进入。因此本方案的重心是 §4 防新增机制，§2 路线图按「先止血 → 再建网 → 再动骨」排序，而不是按债务本身的技术复杂度排序。

---

## 1. 债务清单

### 1.1 P0 — 立即偿还（阻断级：正确性、安全、数据丢失）

共 10 项。特征：**用户已经或即将为此付出真实代价**（多扣费、丢结果、丢凭证、崩溃）。

> **已全部偿还（2026-10-17，阶段 0）**：落地位置与护栏见 §2 阶段 0 落地表。下表保留为盘点记录，
> 其中的行号是 2026-10-17 盘点当时的位置，**不作为后续追踪依据**——追踪一律走不变量断言。

| ID        | 位置                                                                                    | 债务                                                                                                  | 影响                                                          | 成本 | 偿还方式                                  |
| --------- | ------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- | ----------------------------------------------------------- | -- | ------------------------------------- |
| **P0-1**  | `Core/Engine/BatchEngine.cs:1406`                                                     | `cost = Math.Min(cost, Math.Max(MaxCostPerTask, cost))` **恒等于 cost**，是 no-op                        | 单任务成本上限完全失效 → 超额扣费，且表面"已封顶"极具迷惑性                            | 极小 | 重构（1 行 + 1 单测）                        |
| **P0-2**  | `Agents/Internal/CliProcessRunner.cs:213`、`293`                                       | `WaitForExitAsync` 后直接读 buffer，未排空 `OutputDataReceived` 回调                                          | 输出尾部丢失 → 漏判配额重置（误触发降级/兜底）、stream-json `result` 事件截断导致任务误判失败 | 中  | 重构（回调 EOF 信号 + TCS 门）                 |
| **P0-3**  | `Infrastructure/Git/LocalGitIntegration.cs:25-27,42,50`                               | 命令行字符串拼接，仅用 `"` 包裹未转义 `"`；`gh pr create --title "..."` 同理                                           | 配置驱动的**任意参数注入**，可注入 `-c core.sshCommand=` 实现代码执行            | 中  | 重构（`ArgumentList` + 输入白名单校验）          |
| **P0-4**  | `Core/Engine/BatchEngine.cs:1462/1476/1498`                                           | `CompleteTask/FailTask/MarkInterrupted` 并发调 `PersistAsync(CancellationToken.None)`                  | `Concurrency>1` 时多任务并发写同一结果文件 → 已完成状态回退、文件损坏                | 中  | 重构（单写者队列 + 读改写同锁）                     |
| **P0-5**  | `Core/Engine/BatchEngine.cs:1410/1434`                                                | `_fallbackBatchCost +=`、`_fallbackModelsUsed.Add` 无锁（`_fallbackBatchCost` 与 `_accountUsage` 锁策略不一致） | 并发下成本累计重复/越限，`List.Add` 内部损坏                                | 小  | 重构（`Interlocked` + `ConcurrentBag`）   |
| **P0-6**  | `Desktop/ViewModels/ConfigViewModel.cs:628,656`；`MonitorViewModel.cs:522,535,555,574` | `ConfigureAwait(false)` 后直接写 `ObservableCollection`                                                 | 后台线程改 UI 集合 → Avalonia 跨线程异常，**用户侧随机崩溃**                    | 小  | 重构（EngineHost 统一 Post 到 UI 线程，见 P0-7） |
| **P0-7**  | `Desktop/Services/EngineHost.cs`（事件广播）                                                | 引擎事件在后台线程触发，注释明示"订阅方自行切线程"                                                                          | 线程切换责任外包给 7 个订阅方，必然有人踩坑（即 P0-6）                             | 小  | 重构（单一收敛点：EngineHost 内 Post 后广播）       |
| **P0-8**  | `Infrastructure/TaskFiles/MarkdownInPlaceWriter.cs:19-21`                             | 读—改—写非原子（`File.WriteAllTextAsync` 直接覆写），而同库已有 `AtomicFileWriter`                                    | 写入中断 = **用户任务文件截断**；与 CLI 并发写冲突                             | 小  | 重构（复用 `AtomicFileWriter`）             |
| **P0-9**  | `Infrastructure/CredentialVault/GuidedLoginOrchestrator.cs:104-112`                   | 引导登录先 `File.Delete(livePath)` 无备份，后续等待超时抛异常                                                         | 失败后**用户凭证已消失且不可恢复**                                         | 中  | 重构（先备份再删 + 失败回滚）                      |
| **P0-10** | `Desktop/Services/EngineHost.cs:326`                                                  | 自动关机直接 `TryShutdownAsync()`                                                                         | 违反设计文档 §6.3「需 60s 可撤销对话框」硬约束 → 用户工作在途被强关                    | 中  | 补齐功能（倒计时 + 可撤销）                       |

> 附注 P0-1 的文档失效：`docs/CODE-REVIEW-性能瓶颈与Bug审查报告.md` 记录该缺陷在 `:1011`，实际已迁移至 `:1406`。行号型债务记录会随重构漂移，§4 将改为**不变量断言**登记。

### 1.2 P1 — 短期偿还（1–2 个迭代：结构性与可测性）

共 15 项。特征：**不修，则 P0 修完后下一轮改动仍会失控**。

| ID        | 位置                                                                                  | 债务                                                                                                                                                                            | 影响                                                                 | 成本 | 偿还方式                                                            |
| --------- | ----------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------ | -- | --------------------------------------------------------------- |
| **P1-1**  | `Core/Engine/BatchEngine.cs`（全文 1960 行）                                             | God Object，16 类职责：派发/状态机/DAG/预检/等待恢复/兜底计费/Vault 切换/Git/PR/diff/Markdown/通知/报告/冷却/文件IO/事件                                                                                      | 任一语义变更都触碰同一文件，**回归面不可控**；无法对调度/计费/Git 分别替换与测试                      | 大  | 重构（拆 5 协作者，见 §3.1）                                              |
| **P1-2**  | `BatchEngine.cs:973-1220` `ExecuteTaskAsync` 248 行、`188-378` `PreflightAsync` 191 行 | 超长方法，`while(true)+switch+goto case`，圈复杂度估 >35                                                                                                                                 | 无法单测分支，改动必引入回归                                                     | 大  | 重构（状态机外置 + 提取方法对象）                                              |
| **P1-3**  | `BatchEngine` 为 `sealed` 且**无 `IBatchEngine` 接口**，14 参数构造                           | 现有 `BatchEngineTests` 只能黑箱驱动                                                                                                                                                  | 拆分 P1-1 前必须先有接口，否则无法增量验证                                           | 中  | 重构（提接口 + 窄化构造）                                                  |
| **P1-4**  | `Desktop.csproj:33-34` + `EngineHost.cs:7-16` + `CredentialVaultHost.cs:28-30`      | UI 层直引 Infrastructure/Agents 并 `new` 全部实现类 → **UI 成为组合根**                                                                                                                     | 架构倒置：无法替换实现、无法单测 Desktop                                           | 大  | 重构（Core 暴露 `IEngineFactory` + 引入 DI）                            |
| **P1-5**  | `Desktop/ViewModels/MainViewModel.cs:19-31`、`App.axaml.cs:22`                       | 无 DI 容器，手工 `new` 7 个 VM + `EngineHost`（sealed 无接口）                                                                                                                            | Desktop 层零可测性                                                      | 大  | 重构与 P1-4 合并                                                     |
| **P1-6**  | `MainViewModel.cs:20`、`EngineHost.cs:136`                                           | `InitializeAsync().GetAwaiter().GetResult()` 同步阻塞异步                                                                                                                           | 启动期 UI 卡死 / Avalonia 上下文死锁风险                                       | 小  | 重构（异步初始化门）                                                      |
| **P1-7**  | `MonitorViewModel.cs:52-53`、`LoginViewModel.cs:181`、`TaskRowViewModel.cs:13`        | 事件以 lambda 订阅且**全程无 `-=`**；`SyncTasks:370` 每次重建行而 `TaskItem` 长生命周期 → 废弃行 VM 被永久持有                                                                                             | 真实内存泄漏，长跑后 OOM                                                     | 小  | 重构（弱事件 / 显式 Dispose）                                            |
| **P1-8**  | `EngineHost.cs:94`                                                                  | `CredentialLocationRegistry.SetLiveRootOverride(...)` **静态全局副作用**由 VM 构造触发                                                                                                    | 全局状态被 UI 生命周期意外改写                                                  | 中  | 重构（显式传入，取消静态覆写）                                                 |
| **P1-9**  | `Core/Scheduling/GateEvaluator.cs:114-170`                                          | Core 层直接 `new Process()` + `cmd.exe /c "{command}"`                                                                                                                           | 分层违规 + 配置驱动的命令执行注入面；Core 不可测                                       | 中  | 隔离（抽 `IProcessRunner` 至 Infrastructure）                         |
| **P1-10** | `Core/Engine/BatchEngine.cs:196,226,1531`                                           | 直接 `File.Exists/Directory.Exists/GetCurrentDirectory()`，绕过自有 `ITaskFileStore`                                                                                                 | 引擎内残留不可测的隐式 IO                                                     | 中  | 重构（IO 全部走抽象）                                                    |
| **P1-11** | `Infrastructure/CredentialVault/FileCredentialVault.cs:9-16,483-488`                | Vault 目录继承默认 ACL；`manifest.json` 明文；`WriteManifest` 是同类中**唯一非原子写**                                                                                                            | 同机其他用户/进程可读凭证；强杀留半截 manifest → 快照判定不存在 → 切换被拒                      | 中  | 重构（收 ACL + 统一 `AtomicFileWriter`）                               |
| **P1-12** | `Infrastructure/CredentialVault/CliPermissionSettings.cs:17-46`                     | 静默把用户 CLI 的 `defaultMode` 改为 `bypassPermissions`（**影响范围不止本工具**），且非原子写                                                                                                         | 安全副作用越界 + 与运行中 CLI 竞态互相覆盖                                          | 中  | 重构（作用域收窄 + 原子写 + 变更才写）                                          |
| **P1-13** | `Agents/Internal/CliProcessRunner.cs:311-322`                                       | `KillTree` 依赖外部 `taskkill` + `WaitForExit(3000)` 同步阻塞                                                                                                                         | 每次超时/取消额外 3s 阻塞；`killer` 未 `using`；非可移植                            | 中  | 重写（`Process.Kill(entireProcessTree:true)` + `WaitForExitAsync`） |
| **P1-14** | `Infrastructure/Git/LocalGitIntegration.cs:80-101`                                  | `WaitForExitAsync(ct)` 无超时、无 stdin 重定向                                                                                                                                        | `git push` 缺凭证时等待终端输入 → 永久挂起，ct 永不触发                               | 中  | 重构（超时 + stdin 重定向 + 环境变量注入凭证）                                   |
| **P1-15** | 测试覆盖盲区                                                                              | `CodeBuddyCreditsApiClient`、`XlsxTaskParser`/`XlsxTaskWriter`、`BackupManager`、`LocalGitIntegration`、`ShutdownService`、`DangerousOperationGuard`、`CliCommandBuilder` **全部零测试** | 其中 `DangerousOperationGuard`/`CliCommandBuilder` 是安全拦截与注入点，无测试即无保护 | 中  | 补齐（先补 `XlsxTaskWriter` 与 `CliCommandBuilder`）                   |

### 1.3 P2 — 机会主义偿还（随任务带走）

共 16 项。特征：**不影响正确性，但持续抬高每次改动的成本**。

**架构/重复（偿还成本中）**

| ID   | 位置                                                                                  | 债务                                                                                         |
| ---- | ----------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------ |
| P2-1 | `BatchEngine.cs:1240-1324` vs `WorkflowExecutor.cs:388-412`                         | 「等待恢复」双实现且**语义已分叉**（工作流路径无取消唤醒、无时长统计）                                                      |
| P2-2 | `BatchEngine.cs:1551-1567` vs `WorkflowExecutor.cs:414-430`                         | 「工作空间解析」同名不同义：一返 `null`、一返 CWD → 易跑错目录                                                     |
| P2-3 | `BatchEngine.cs:1146-1218` vs `WorkflowExecutor.cs:270-308`                         | 失败分派状态机（`RateLimited/QuotaExhausted/AuthExpired/Timeout`）逐字重复                              |
| P2-4 | `BatchEngine.cs:1775-1785`、`WorkflowExecutor.cs:479-480`、`GateEvaluator.cs:172-173` | `Truncate` 三份实现，截断后缀文案各不相同                                                                 |
| P2-5 | `Domain/TaskItem.cs:178-180`、`WorkflowTypes.cs:180`、`DagScheduler.cs:152-154`       | 终态集合三份定义                                                                                   |
| P2-6 | `Domain/WorkflowTypes.cs:418-512`                                                   | 6 个手写 `Clone`，改字段必漏改                                                                       |
| P2-7 | `Core/Domain/TaskItem.cs:13`                                                        | 领域模型实现 `INotifyPropertyChanged`（WPF/Avalonia 绑定契约侵入 Domain），且 `SetField:224-231` 在工作线程触发事件 |
| P2-8 | `Core/Abstractions/IClock.cs:20-61`                                                 | 测试替身 `FakeClock` 放进生产程序集                                                                   |
| P2-9 | `Options/RunOptions.cs:12-227`                                                      | 40+ 配置项聚合为 God Config，`Clone():222-226` 靠 JSON 往返                                          |

**UI 一致性（偿还成本小–中）**

| ID    | 位置                                                                      | 债务                                                                                    |
| ----- | ----------------------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| P2-10 | `LoginView.axaml`（~30 处）、`SetupWizardView.axaml`、`ConfigHubView.axaml`  | 硬编码色值且不在使用 token 表内（`#2F6FED` vs 设计 token `#2563EB`）→ **深色主题下这些页完全失效**                |
| P2-11 | `MonitorViewModel`/`TaskRowViewModel`/`Phase2RowModels`/`DiffViewModel` | 第二套调色板与 `AppStyles.xaml:26-63` 的 token 并存冲突                                           |
| P2-12 | `Desktop.csproj:9`                                                      | `AvaloniaUseCompiledBindingsByDefault=false` + 全部 XAML 无 `x:DataType` → 反射绑定，拼错属性静默失效 |
| P2-13 | `MonitorView.axaml:130/136/140`、`StatusDotHelper.cs:67-75`              | 按 `"运行中"/"等待恢复"` 中文字面量着色 → 改文案即断色                                                     |
| P2-14 | `MainWindow.axaml:11`                                                   | 固定 `1280×880`、侧栏 220（设计 200/收起 56）；无响应式/快捷键/AutomationProperties；设计 §7/§8 未落地         |
| P2-15 | `Desktop/Models/ConfigEditors.cs:163-505`                               | 5 个手写 DTO 编辑器重复「字段→字符串→`Apply()` 回写」三段式，且 `Apply()` 解析失败**静默置 null**                  |

**工程化（偿还成本小）**

| ID    | 位置                            | 债务                                                                                                                                                                                                                                    |
| ----- | ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P2-16 | `Directory.Build.props:8`     | `TreatWarningsAsErrors=false` + `NoWarn CS1591`，无 `AnalysisLevel`/`EnforceCodeStyleInBuild`                                                                                                                                           |
| P2-17 | `.editorconfig:8-21`          | `csharp_style_*` 与 CA 规则缺 `EnforceCodeStyleInBuild` → **构建期完全不执行**，形同虚设                                                                                                                                                               |
| P2-18 | 仓库根                           | 无 CI（`.github/workflows` 等均无）；`coverlet.collector` 已引用但无收集管线                                                                                                                                                                          |
| P2-19 | `TaskList/tasks.xlsx`         | 被 `.gitignore:364` 忽略但仍被 git 跟踪（`git ls-files -i -c` 唯一命中）；`samples/` 完全未忽略，混入 `report-*`/`credits-claim-*`/`*.backup-*`/`runs/` 等真运行产物                                                                                               |
| P2-20 | `tests/E2E`、`tests/Usability` | E2E 与单元测试同项目无 `Trait` 分类，无法 `--filter` 分层跑                                                                                                                                                                                            |
| P2-21 | `tests/**`                    | 依赖真实进程（`CliProcessRunnerTests:19-22` 非 Windows 静默"通过"）、真实时钟（`TaskFileStoreTests:166` `Thread.Sleep(1100)`）、直读仓库 samples（`EndToEndPipelineTests:35-44` 找不到 slnx 时 NRE）→ Flaky 三件套                                                      |
| P2-22 | `models.yaml`                 | 0 字节空文件却被 README/配置路径引用                                                                                                                                                                                                               |
| P2-23 | `.gitattributes`              | VS 默认模板，3 条规则被注释，未标 `*.xlsx binary`                                                                                                                                                                                                   |
| P2-24 | 性能项                           | `CliProcessRunner.cs:92,153` 无界 buffer + `Tail(buffer.ToString(),400)` O(n²)；`FileRunLogger` 每条事件 `AutoFlush` 同步刷盘；`QuotaErrorClassifier.cs:52-66` 全量 `ToLowerInvariant()` 未预计算                                                       |
| P2-25 | 死代码                           | `DagScheduler.cs:116-150` `DependenciesSatisfied()` 仅测试引用（生产由 `BatchEngine.EvaluateDependency:843` 重新实现）；`WorkflowExecutor.cs:27` `SessionPolicy` 注入后从未读取；`TaskItem.cs:233-234` 私有方法未用；`AccountConfig.EffectiveModelPool` 等 4 项无生产消费者 |

---

## 2. 治理路线图

原则：**先止血 → 再建网 → 再动骨 → 持续化**。不按技术复杂度排序，按「风险 × 前置依赖」排序。

### 阶段 0：止血（第 1 个迭代）

目标：消灭所有**用户已付出真实代价**的缺陷，且全部是小改动。

> **状态：✅ 已完成（2026-10-17）**。全程 `dotnet build` 0 错误、**316/316 测试通过**（治理前基线 270，新增 46 例）。
> 9 项互不耦合，按下面顺序逐个落地并各自带护栏。

#### 阶段 0 落地表

| #  | 债务 | 落地位置                                                                                              | 护栏（不变量测试）                                                                    |
| -- | -- | ------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- |
| 1  | P0-1 成本封顶                                                                                             | `Core/Engine/BatchEngine.cs`（`TryFallbackAsync`：`cost = Math.Min(cost, Math.Max(0, MaxCostPerTask))`） | `BatchEngineTests.兜底实测成本超出单任务上限时按上限截断`（任务计费与批级累计都被截断）                    |
| 2  | P0-8 Markdown 回写非原子                                                                                     | `Infrastructure/TaskFiles/MarkdownInPlaceWriter.cs` 改用 `AtomicFileWriter`                          | `MarkdownInPlaceWriterTests.回写走原子写_目录内不残留临时文件`                               |
| 3  | P0-6/P0-7 UI 线程收敛                                                                                        | 新增 `Desktop/Services/UiThread.cs`；`EngineHost` 三个事件出口统一 Post；`MonitorViewModel`/`ConfigViewModel` 后台线程后的 UI 写入全部经 UiThread | 依赖 Avalonia `Dispatcher`，单元测试需 Desktop 项目可实例化（P1-4/P1-5 引入 DI 后再补，现阶段靠运行验证） |
| 4  | P0-5 fallback 累计加锁                                                                                       | `BatchEngine`：`_fallbackLock` 覆盖「检查上限 → 累加 → 快照读取」                                                | `BatchEngineTests.并发下兜底成本与用量累计准确`（并发 4 × 8 任务）                             |
| 5  | P0-4 持久化单写者化                                                                                            | `BatchEngine`：`_persistGate` 串行化落盘 + `_writeGate` 保证「状态写入 ↔ 序列化」互斥                                 | `BatchEngineTests.并发落盘串行化_终态不回退`（`MaxConcurrentSaves == 1`）               |
| 6  | P0-9 凭证删除前备份 + 失败回滚                                                                                    | `Infrastructure/CredentialVault/GuidedLoginOrchestrator.cs`：暂存副本 + 登录未完成原样回滚 + 成功后清暂存（失败则显式告警）  | `GuidedLoginFlowTests.登录超时_清理前的Live凭证被原样回滚`、`登录成功_暂存凭证备份已清理`              |
| 7  | P0-10 自动关机可撤销倒计时                                                                                        | 新增 `Core/Engine/AutoShutdownCoordinator.cs`（策略下沉 Core）+ `Desktop/Views/ShutdownCountdownWindow`（60s 倒计时窗口）；`EngineHost.MaybeShutdownAsync` 改为委托协调器 | `AutoShutdownCoordinatorTests`（10 例：取消 / 非完成态 / 仅零失败 / 平台不支持 / 窗口异常 / 被终止 → 一律不关机） |
| 8  | P0-2 进程输出排空                                                                                              | `Agents/Internal/CliProcessRunner.cs`：stdout/stderr 的 EOF 信号 + TCS 门（5s 兜底，超时只告警不挂死）             | `CliProcessRunnerTests.RunAsync_大量输出_进程退出后输出尾部不丢失`（20 万字符后哨兵行不丢）        |
| 9  | P0-3 命令注入                                                                                                | `Infrastructure/Git/LocalGitIntegration.cs`：`ProcessStartInfo.ArgumentList` 逐参数传递 + 新增 `GitRefName` 引用名白名单 + 仓库路径 `Path.GetFullPath` 归一 | `GitRefNameTests`（`-c core.sshCommand=` / `--upload-pack=` / 路径穿越 / 超长均被拒）  |

#### 阶段 0 的取舍记录

- **P0-10 未采用「OS 级延迟关机 + `shutdown /a` 撤销」**：倒计时放在应用内，在 App 崩溃/被杀的场景下默认**不关机**
  （失效即保护）；`IShutdownService` 因此无需新增 `Schedule/Cancel` 方法，接口保持最小。
- **P0-2 的 KillTree 重写（P1-13）按原计划推迟**，本阶段只改输出排空，Two-part 拆分不变。
- **P0-4 用 `SemaphoreSlim` 而非 `lock`**：落盘序列化需要在持锁期间 `await`，`lock` 不允许；
  `TaskItem` 可变，浅拷贝快照挡不住并发写，只能让「状态写入」与「序列化读取」互斥。
- **已知代价**：`BatchEngine.cs` 由 1960 行增至 **2060 行**、硬传 `CancellationToken.None` 由 8 处增至 11 处
  （均集中在 `_persistGate`/`_writeGate` 的不可取消等待上）。**这两项是「先止血」的账面成本**，
  偿还动作排在阶段 2（P1-1 拆分 + P1-2 状态机外置），本阶段不做夹带式的额外重构。

### 阶段 1：建网（第 1–2 个迭代，与阶段 0 尾部并行启动）

目标：**在动 P1-1 的骨架之前，先造出安全网**。这是本方案最关键的顺序决策——没有网就拆 `BatchEngine`，等于盲拆。

1. 建 CI（P2-18）：`build + test + 覆盖率采集`，先**只报告不阻断**
2. 开编译期门禁（P2-16/P2-17）：`EnforceCodeStyleInBuild=true`，`TreatWarningsAsErrors` **按项目分批开启**（Core 先开，Desktop 最后）
3. 测试分层（P2-20）：E2E/Usability 打 `Trait`，CI 快跑单元、慢跑 E2E
4. 去 Flaky（P2-21）：真实进程→`IProcessLauncher` 注入、真实时钟→`IClock`、samples→嵌入式资源
5. 补高危盲区测试（P1-15）：**优先 `XlsxTaskWriter`（唯一二进制原子写路径）与 `CliCommandBuilder`（注入点）**，再补 `DangerousOperationGuard`
6. 仓库净化（P2-19/P2-22/P2-23）：`git rm --cached TaskList/tasks.xlsx`，忽略 `samples/report-*`、`samples/credits-claim-*`、`samples/*.backup-*`、`samples/runs/`
7. 追加**不变量测试**：为 P0-1 等「一眼看不出是错的」缺陷补断言式测试（成本 > MaxCostPerTask 时断言被截断）


### 阶段 2：动骨（第 2–4 个迭代）

目标：解除「改动放大器」。顺序严格按依赖：

```
P1-3 提 IBatchEngine 接口 + 窄化构造   ← 必须先做，否则无法增量验证
   ↓
P1-4/P1-5 引入 DI + IEngineFactory（解除 UI→Infrastructure 倒置）
   ↓
P1-9 IProcessRunner 抽离（把 Core 的进程 IO 赶出 Core）
P1-10 引擎内 IO 全部走 ITaskFileStore
   ↓
P1-1 BatchEngine 按 5 协作者拆分
   ↓
P1-2 状态机外置
```

| 步骤 | 拆分产出                           | 承接的原职责                              |
| -- | ------------------------------ | ----------------------------------- |
| 1  | `ITaskDispatcher`              | ①派发/并发控制、③DAG 判定                    |
| 2  | `ITaskRunner`                  | ②单任务状态机（重试/退避/降级/超时）                |
| 3  | `IFallbackBilling`             | ⑥兜底模型选择与计费（含 P0-1/P0-5 的成本不变量）      |
| 4  | `IGitWorkflow`                 | ⑧Git 建分支/开 PR、⑨diff 采集、⑩Markdown 回写 |
| 5  | `INotifier` + `IReportBuilder` | ⑪通知、⑫报告/用量/时间线聚合                    |
| 6  | `IPreflight`                   | ④启动预检                               |

护栏：

- 每一步**保持 `BatchEngine` 的外壳存在**（先转为门面，最后再删），确保每步可编译、可跑全部现有测试
- 每步结束时跑 `git diff --stat`，若单步改动 >400 行则回退重切（说明切得不够细）
- 阶段 2 期间**冻结新功能需求**（见 §4.5 债务预算）

### 阶段 3：持续化（第 4 个迭代起，长期）

1. 机会主义偿还 P2 各项（随相关任务带走，见 §3.3）
2. debt 台账月度复核，按 §4 度量看趋势
3. 阶段 2 拆出的 5 个协作者各自补齐单测，逐步移除对 `BatchEngine` 黑箱测试的依赖

---

## 3. 偿还策略：重构 / 重写 / 隔离

### 3.1 决策判据

对每个债务 ID，先回答三个问题再选策略：

| 判据                                      | 指向                     |
| --------------------------------------- | ---------------------- |
| 组件是否仍承载**独有且正确的领域知识**（积累多年、文档未覆盖的业务规则）？ | 是 → **绝不重写**，只能重构      |
| 组件是否**有测试覆盖**，或能否在改动前低成本补上？             | 否 → 先补测试，否则不得重构        |
| 组件是否被**多处依赖**，还是单点调用？                   | 单点 → 可重写；多处 → 只能重构或隔离  |
| 缺陷是**局部的**还是**设计级的**？                   | 设计级且无领域知识 → 重写；局部 → 重构 |

### 3.2 归类结果

**重构（30 项，占绝大多数）**

保留领域知识，改结构。适用于：P0-1、P0-3、P0-4、P0-5、P0-6、P0-7、P0-8、P0-9、P1-1、P1-2、P1-3、P1-4、P1-5、P1-7、P1-9、P1-10、P1-11、P1-12、P1-14、P2-1~P2-13、P2-15、P2-24 等。

> 尤其 P1-1：它持有「降级矩阵 / 冷却 / 兜底计费 / 时段窗口」等**未被文档完整覆盖的业务规则**，重写必然丢语义。**必须 strangler fig**：外壳保留 → 职责逐个外迁 → 外壳转门面 → 最后删除。

**重写（4 项）**

仅限「无领域知识 + 单点/窄接口 + 结构性缺陷」：

| ID    | 对象                          | 理由                                                                           |
| ----- | --------------------------- | ---------------------------------------------------------------------------- |
| P1-13 | `CliProcessRunner.KillTree` | 依赖外部 `taskkill`、同步阻塞、不可移植、无领域知识；.NET 原生 `Kill(entireProcessTree:true)` 可直接替代 |
| P0-2  | `CliProcessRunner` 输出读取段    | 现有「轮询 + WaitForExit 后立即读」模型本身错误，应整体替换为「回调 EOF 信号 + TCS」；是局部替换不是整体重写          |
| P2-15 | `ConfigEditors.cs` 的 5 个编辑器 | 纯样板，无领域知识；改为泛型 `FieldEditor<T>` + 统一校验反馈，或引入 PropertyGrid                    |
| P2-6  | 6 个手写 `Clone`               | 改为源生成 / `record with`，消除「改字段必漏改」                                             |

**隔离（5 项）**

不消除债务，只在它周围**筑墙**，防止扩散。适用于「改动风险 >> 收益」的部分：

| ID          | 对象                                               | 隔离手段                                                                                               |
| ----------- | ------------------------------------------------ | -------------------------------------------------------------------------------------------------- |
| P1-1（过渡期）   | `BatchEngine`                                    | 拆分期间保留外壳为门面，**新代码一律不得直接引用 `BatchEngine` 内部成员**，只允许走 `IBatchEngine`                                 |
| P2-7        | `TaskItem : INotifyPropertyChanged`              | Domain 保留契约但标记 `[Obsolete(仅 Desktop 适配)]`；Desktop 侧引入 `TaskItemViewModel` 适配层，新代码禁止直接绑定 `TaskItem` |
| P1-8        | `CredentialLocationRegistry.SetLiveRootOverride` | 先包一层静态守卫（重复覆写直接抛），待 DI 落地后取消静态入口                                                                   |
| P2-11/P2-10 | 第二套调色板                                           | 在 token 与新色值间加映射层，禁止新 XAML 直接写字面色；旧页逐个迁移                                                           |
| P2-19       | `samples/` 产物混入                                  | 测试改读**嵌入式资源副本**，把「测试依赖产物」的闭环切断，samples 目录改为只读示例                                                    |

**主动不偿还（3 项）**

明确记录但不动，避免团队反复讨论：

| ID                                    | 对象                  | 理由            |
| ------------------------------------- | ------------------- | ------------- |
| P2-24 中的 `QuotaErrorClassifier` 大小写优化 | 性能充足，改动收益低于引入回归的风险  | 仅在出现实测性能证据时才动 |
| P2-14 响应式布局                           | 桌面工具固定窗口尺寸，当前无多形态需求 | 有明确需求再动       |
| P2-25 部分死代码（如 `SessionPolicy`）        | 可能是预留扩展点            | 需先确认预留意图，否则保留 |

### 3.3 机会主义偿还（Boy Scout Rule）边界

允许且鼓励：改某文件时顺手清掉**同文件内**的 P2 项（魔法值、重复 `Truncate`、空 catch）。

不允许：

- 顺手改动**其他文件**（会让 PR diff 失控，评审失效）
- 顺手改动**正在被 P0/P1 修复的同一区域**（冲突风险）

落地方式：这类清理**单独提交**，commit message 前缀 `chore(debt):`，与功能提交严格分离。

---

## 4. 防新增机制（本方案的重心）

存量债务修得再干净，没有这层的下一个季度会回到原点。

### 4.1 编译期门禁（第一道墙：写不出）

```xml

<PropertyGroup>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <AnalysisLevel>latest-recommended</AnalysisLevel>
  <AnalysisMode>Recommended</AnalysisMode>
  
  <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
</PropertyGroup>
```

**分批阻断顺序**（必须与阶段 1 的测试补齐同步，否则会一次性爆出几百个警告）：

1. Core → 2. Agents → 3. Infrastructure → 4. Desktop

### 4.2 架构约束自动化（第二道墙：编译不过）

用 ArchUnitNET（或 `Microsoft.CodeAnalysis` 自定义分析器）把 §1 的分层规则固化为**测试**，而不是文档约定：

| 断言                                              | 防什么                  |
| ----------------------------------------------- | -------------------- |
| `Core` 不得引用 `Infrastructure`/`Agents`/`Desktop` | 防 P1-9/P1-10 类分层违规复发 |
| `Core` 命名空间下不得出现 `System.Diagnostics.Process`   | 防 Core 再起进程          |
| `Core`/`Infrastructure` 不得出现 `Avalonia.*`       | 防 P2-7 类 UI 契约反向侵入   |
| `Desktop` 不得直接 `new` Infrastructure 具体类型（须经接口）  | 防 P1-4 倒置复发          |
| `Domain` 下不得出现 `INotifyPropertyChanged`         | 防 P2-7 扩散            |
| 生产程序集不得出现 `FakeClock`/`Mock*` 类型                | 防 P2-8 测试替身进生产       |

### 4.3 复杂度门禁（第三道墙：提交不进）

CI 中加硬性阈值，**超标即失败**（当前值仅为示意，需先跑基线后按「当前值 + 宽容量」标定）：

| 指标     | 建议阈值  | 直接拦截                                              |
| ------ | ----- | ------------------------------------------------- |
| 单文件行数  | ≤ 800 | 防下一个 `BatchEngine`(1960) / `ConfigViewModel`(824) |
| 单方法行数  | ≤ 60  | 防 `ExecuteTaskAsync`(248) / `PreflightAsync`(191) |
| 圈复杂度   | ≤ 15  | 防 `ExecuteTaskAsync` >35                          |
| 构造参数个数 | ≤ 7   | 防 `BatchEngine` 14 参构造                            |
| 类公开成员数 | ≤ 30  | 防 God Object                                      |

> 关键：**阈值先按现状标定到「不阻断现有代码中的合理部分」，再逐季度收紧**。一上来就设理想值会导致团队习惯性 `// NOLINT` 越过，门禁形同虚设。

### 4.4 PR 评审门禁（第四道墙：合不进）

在 `docs/CODE-REVIEW-代码审查标准与流程.md` 中追加**债务审查清单**，PR 模板强制勾选：

- [ ] 本次改动是否让任何文件的行数/复杂度上升？若是，说明理由
- [ ] 是否新增了 `catch { }` 或 `catch(Exception)` 后仅记 WARN？（当前全库此类吞异常 ≥ 12 处，是排障困难的主因）
- [ ] 是否新增 `static` 可变状态？（P1-8 类）
- [ ] 是否新增 `.GetAwaiter().GetResult()` / `.Result` / `.Wait()`？（P1-6 类）
- [ ] 后台线程完成的 `await` 后是否直接改了 UI 集合？（P0-6 类）
- [ ] 是否新增了文件读写而未走 `AtomicFileWriter`？（P0-8 类）
- [ ] 是否拼接了命令行字符串而非使用 `ArgumentList`？（P0-3 类）
- [ ] 是否新增了硬编码色值/魔法数字，而非引用 token/常量？
- [ ] 新增的高危路径（凭证/进程/Git/安全拦截）是否带测试？

**吞异常专项规则**：允许 `catch` 后记日志，但**必须保留原异常对象**且必须区分「可容忍降级」与「应中止」；空 `catch { }` 一律拒绝。

### 4.5 债务预算（第五道墙：排不进）

- 每个迭代预留 **20% 容量**用于债务偿还，且该容量**不可被功能需求挤占**
- 阶段 2（动骨）期间提升至 **50%**，并明确冻结新功能
- 每个迭代结束时更新债务台账，关闭已偿 ID，登记新发现 ID

### 4.6 度量与趋势看板

每迭代采集，看**趋势**而非绝对值：

| 指标                           | 当前基线          | 阶段 0 后实测（2026-10-17） | 目标方向    |
| ---------------------------- | ------------- | ----------------- | ------- |
| 最大文件行数                       | 1960          | 2060（`BatchEngine`，↑ 因 P0-4/P0-5 并发护栏） | ↓（阶段 2 偿还） |
| 最大方法行数                       | 248           | 248（`ExecuteTaskAsync` 未动）  | ↓       |
| 全库 `catch { }` / 吞异常数        | ≥ 12          | 空 `catch {` 3 处；`catch (Exception` 42 处待逐项甄别 | ↓       |
| `CancellationToken.None` 硬传数 | 8             | 11（新增 3 处为写者门的不可取消等待）     | ↓ 至 0   |
| 高危模块零覆盖数                     | 7             | 6（`GitRefName` 白名单已补；`LocalGitIntegration` 本体仍零覆盖） | ↓ 至 0   |
| 编译警告数                        | 未采集（阶段 1 起采集） | 未采集（仍属阶段 1）        | ↓       |
| 债务台账 P0 未偿数                  | 10            | **0**             | → 0 并保持 |
| 测试总数 / 通过率                   | 270 / 100%    | 316 / 100%        | ↑       |

> 台账登记改用**不变量描述**而非行号：如 P0-1 登记为「`TryFallbackAsync` 中 `cost` 必须被 `MaxCostPerTask` 截断」，并配断言测试。理由：`docs/CODE-REVIEW-性能瓶颈与Bug审查报告.md` 记录的 `:1011` 已漂移到 `:1406`，行号型台账会失效。

### 4.7 引入顺序（避免门禁一次性压垮团队）

```
迭代1: CI 建起来（只报告不阻断）+ 测试分层 + 覆盖盲区补齐
迭代2: EnforceCodeStyleInBuild + ArchUnitNET 断言（Core 优先）
迭代3: 复杂度阈值按基线标定（先 warning）
迭代4: 复杂度阈值转 error + TreatWarningsAsErrors 分批开启
持续:  PR 清单 + 20% 预算 + 台账月度复核
```

---

## 附录 A：债务台账索引（按偿还方式分组）

| 方式    | ID                                                                                                                  |
| ----- | ------------------------------------------------------------------------------------------------------------------- |
| 重构    | P0-1, P0-3~~P0-10, P1-1~~P1-5, P1-7, P1-9~~P1-12, P1-14, P1-15, P2-1~~P2-5, P2-7~~P2-14, P2-16~~P2-21, P2-23, P2-24 |
| 重写    | P0-2(局部), P1-13, P2-6, P2-15                                                                                        |
| 隔离    | P1-1(过渡), P1-8, P2-7, P2-10/P2-11, P2-19                                                                            |
| 主动不偿还 | P2-24(部分), P2-14, P2-25(部分)                                                                                         |
| 附带清理  | P2-22                                                                                                               |

> P0-1~P0-10 全部标记为**已偿还**（2026-10-17，阶段 0），落地位置见 §2 阶段 0 落地表。

## 附录 B：事实更正

既有记录中的两处事实需更正，避免后续文档继续沿用：

1. **技术栈**：项目为 **net10.0 + Avalonia 12.1.4**，非 .NET 8 / WPF
2. **缺陷行号**：`docs/CODE-REVIEW-性能瓶颈与Bug审查报告.md` 中 B1 记录为 `BatchEngine.cs:1011`，实际位置随重构漂移
   （2026-10-17 偿还时位于 `TryFallbackAsync` 内，阶段 0 后行号再次变化）——这正是 §4.6 把台账改为**不变量断言**的直接原因

## 附录 C：阶段 0 偿还一览（2026-10-17）

| ID    | 一句话结论                                                          |
| ----- | -------------------------------------------------------------- |
| P0-1  | 兜底成本按 `MaxCostPerTask` 真实截断，任务计费与批级累计同时生效                      |
| P0-2  | 进程退出后等待 stdout/stderr 到达 EOF 再读缓冲，输出尾部不再丢失（5s 兜底防挂死）          |
| P0-3  | Git/gh 调用全部走 `ArgumentList`，引用名另加白名单，切断 `-c core.sshCommand=` 注入面 |
| P0-4  | 任务文件回写单写者 + 状态写入与序列化互斥，并发下终态不回退                                |
| P0-5  | 兜底成本/用量在同一把锁内「检查 + 累加 + 读取」，并发累计准确                             |
| P0-6  | 后台线程后续链路的 UI 写入（`ObservableCollection`/绑定属性）全部经 `UiThread` 收敛    |
| P0-7  | `EngineHost` 三个事件出口统一在 UI 线程广播，线程切换不再靠订阅方自觉                    |
| P0-8  | Markdown 原地回写改走 `AtomicFileWriter`，写中断不再把用户的任务文件截成半截            |
| P0-9  | 清理 Live 凭证前先留暂存副本，登录未完成原样回滚；成功后清暂存，清理失败显式告警                   |
| P0-10 | 自动关机策略下沉 `AutoShutdownCoordinator`，必须经 60s 倒计时窗口且未被取消才执行       |
