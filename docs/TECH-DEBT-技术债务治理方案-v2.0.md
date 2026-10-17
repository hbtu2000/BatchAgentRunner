# BatchAgentRunner 技术债务治理方案 v2.0

> 盘点日期：2026-10-17　｜　取代 v1.0（同日早些时候，未提交）  
> 盘点范围：`src/**`（117 文件 / 18,119 行）、`tests/**`（32 文件 / 5,845 行）、全部工程配置、git 历史（12 提交）  
> 事实基线：net10.0 / Avalonia 12.1.4 / CommunityToolkit.Mvvm 8.4.2，**无 CI、无门禁、无 CPM**

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

| ID        | 位置                                                                                    | 债务                                                                                 | 影响                                                    | 成本 | 偿还方式                                |
| --------- | ------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------- | ----------------------------------------------------- | -- | ----------------------------------- |
| **P0-1**  | `Core/Engine/BatchEngine.cs:1406`                                                     | `cost = Math.Min(cost, Math.Max(MaxCostPerTask, cost))` **恒等于 cost**，是 no-op       | 单任务成本上限完全失效 → 超额扣费，且表面"已封顶"极具迷惑性                      | 极小 | 重构（1 行 + 1 不变量单测）                   |
| **P0-2**  | `Agents/Internal/CliProcessRunner.cs:213,293`                                         | `WaitForExitAsync` 后直接读 buffer，未排空 `OutputDataReceived` 回调                         | 输出尾部丢失 → 漏判配额重置（误降级/兜底）、stream-json `result` 截断导致误判失败 | 中  | 重写（局部：回调 EOF 信号 + TCS 门）            |
| **P0-3**  | `Infrastructure/Git/LocalGitIntegration.cs:25-27,42,50`                               | 命令行字符串拼接，仅用 `"` 包裹未转义 `"`                                                          | 配置驱动的**任意参数注入**，可注入 `-c core.sshCommand=` 实现代码执行      | 中  | 重构（`ArgumentList` + 输入白名单）          |
| **P0-4**  | `Core/Engine/BatchEngine.cs:1462/1476/1498`                                           | `CompleteTask/FailTask/MarkInterrupted` 并发调 `PersistAsync(CancellationToken.None)` | `Concurrency>1` 时并发写同一结果文件 → 状态回退、文件损坏                | 中  | 重构（单写者队列 + 读改写同锁）                   |
| **P0-5**  | `Core/Engine/BatchEngine.cs:1410/1434`                                                | `_fallbackBatchCost +=`、`_fallbackModelsUsed.Add` 无锁                               | 并发下成本累计重复/越限，`List.Add` 内部损坏                          | 小  | 重构（`Interlocked` + `ConcurrentBag`） |
| **P0-6**  | `Desktop/ViewModels/ConfigViewModel.cs:628,656`；`MonitorViewModel.cs:522,535,555,574` | `ConfigureAwait(false)` 后直接写 `ObservableCollection`                                | 后台线程改 UI 集合 → Avalonia 跨线程异常，用户侧随机崩溃                  | 小  | 重构（EngineHost 统一 Post，见 P0-7）       |
| **P0-7**  | `Desktop/Services/EngineHost.cs`（事件广播）                                                | 引擎事件在后台线程触发，注释明示"订阅方自行切线程"                                                         | 线程切换责任外包给 7 个订阅方，必然有人踩坑（即 P0-6）                       | 小  | 重构（单一收敛点）                           |
| **P0-8**  | `Infrastructure/TaskFiles/MarkdownInPlaceWriter.cs:19-21`                             | 读—改—写非原子，同库已有 `AtomicFileWriter` 却不用                                               | 写入中断 = **用户任务文件截断**                                   | 小  | 重构（复用 `AtomicFileWriter`）           |
| **P0-9**  | `Infrastructure/CredentialVault/GuidedLoginOrchestrator.cs:104-112`                   | 引导登录先 `File.Delete(livePath)` 无备份，后等待超时抛异常                                         | 失败后**用户凭证已消失且不可恢复**                                   | 中  | 重构（先备份再删 + 失败回滚）                    |
| **P0-10** | `Desktop/Services/EngineHost.cs:326`                                                  | 自动关机直接 `TryShutdownAsync()`                                                        | 违反设计文档 §6.3「需 60s 可撤销对话框」硬约束                          | 中  | 补齐功能（倒计时 + 可撤销）                     |

#### B 组：治理真空（7 项，本版新增 / 由 P2 提级）

特征：**不修，则 A 组修完下一季度会原样长回来**。合计成本 < 1 人日。

| ID        | 位置                                          | 债务（实测证据）                                                                                                                                                                                                                                                | 影响                                                                               | 成本 | 偿还方式                          |
| --------- | ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- | -- | ----------------------------- |
| **P0-11** | git 历史                                      | **12 个提交中 10 个提交信息为空**；首提交 `9be9485` 一次性 176 文件 / +22,000 行；`b86c0fe` 单次 63 文件 / +8,487                                                                                                                                                                 | 6 周后**无法追溯任何一次变更的意图**，债务无法归因、churn 分析失效；与自家 `CODE-REVIEW §5.2`「单 PR 聚焦一个关注点」直接冲突 | 极小 | 隔离（commit-msg 钩 + 模板）         |
| **P0-12** | `Directory.Build.props`                     | 增量构建报 **0 警告**，`--no-incremental` 全量重建 Core 实测 **36 个 CA2007**（全方案 50+）。CI 若看增量结果会**永远全绿**                                                                                                                                                              | "构建干净"是假象，`TreatWarningsAsErrors` 一旦开启会一次性爆雷                                     | 小  | 重构（先清存量警告，再改 CI 用全量基线）        |
| **P0-13** | `.gitignore` 末尾 4 行                         | **按日期逐个文件名 ignore**：`/.workbuddy/memory/2026-10-16.md`、`/.workbuddy/memory/2026-10-17.md`… 而非 `.workbuddy/`                                                                                                                                             | 债务**每天都在自动新增**；`/TaskList` 是在 `tasks.xlsx` 已提交**之后**才补的，历史仍在                     | 极小 | 重构（目录级规则 + `git rm --cached`） |
| **P0-14** | 仓库（实测 `git ls-files`）                       | 10 项产物/隐私已入库：`.playwright-cli/page-*.yml`×2、`.workbuddy/memory/*.md`×2、`.workbuddy/tmp/check_px.py`、`gen_icon2.py`、`.tmp_reflect.ps1`（**含个人绝对路径**）、`models.yaml`（**0 字节**）、`samples/tasks.json.backup-20261015181009`、`TaskList/tasks.xlsx`（**真实运行数据**） | 隐私外泄面 + 仓库膨胀 + 新人 `clone` 后环境被污染                                                 | 小  | 重构（清理 + 净化历史）                 |
| **P0-15** | `Directory.Build.props:8,11`                | `TreatWarningsAsErrors=false` + `NoWarn CS1591`；无 `EnforceCodeStyleInBuild`、无 `AnalysisLevel`、无 `AnalysisMode`                                                                                                                                          | 公共 API 文档约束被显式关闭；`.editorconfig` 的 40+ 条规则**构建期完全不执行**                           | 极小 | 重构（配置落地，见 §4.2）               |
| **P0-16** | 仓库根                                         | **无任何 CI**（无 `.github/`，无流水线）；`coverlet.collector` 已引用但无收集管线                                                                                                                                                                                            | 债务与回归**无自动发现机制**，全靠人                                                             | 中  | 补齐（build+test+覆盖率，先只报不阻断）     |
| **P0-17** | `tests/BatchAgentRunner.Tests.csproj:22-24` | 测试项目只引用 Core / Infrastructure / Agents，**唯独不引用 Desktop（5,411 行 = src 的 30%）**                                                                                                                                                                           | Desktop **结构上不可测试**，P0-6/P0-7/P1-7 类缺陷无法用测试锁住，只能靠人眼                              | 中  | 重构（补引用 + 首批冒烟测试）              |

> P0-17 说明：Desktop 不是"没写测试"，而是**测试项目根本没有引用它**——即使想写也写不了。这是可测性缺失，不是测试缺失，故列 P0 而非 P1。

---

### 1.2 P1 — 短期偿还（1–2 个迭代：结构性与可测性）

沿用 v1.0 的 P1-1 ~ P1-15，另补 6 项实测新增（P1-16 ~ P1-21）。

**沿用项（摘要，详见 v1.0 §1.2）**

| ID    | 债务                                                                                                                                                                | 偿还方式                                         |
| ----- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------- |
| P1-1  | `BatchEngine.cs` 1960 行 God Object，16 类职责（变更放大器）                                                                                                                  | 重构（拆 5 协作者，见 §3.4）                           |
| P1-2  | `ExecuteTaskAsync` 249 行 / `PreflightAsync` 196 行，`while(true)+switch+goto case`                                                                                  | 重构（状态机外置）                                    |
| P1-3  | `BatchEngine` 为 `sealed` 且无 `IBatchEngine` 接口，14 参数构造                                                                                                             | 重构（提接口，P1-1 的前置）                             |
| P1-4  | UI 层直引 Infrastructure/Agents 并 `new` 全部实现类 → **UI 成为组合根**（架构倒置）                                                                                                   | 重构（Core 暴露 `IEngineFactory` + DI）            |
| P1-5  | 无 DI 容器，手工 `new` 7 个 VM + `EngineHost`（sealed 无接口）                                                                                                                | 重构（与 P1-4 合并）                                |
| P1-6  | `MainViewModel.cs:20`、`EngineHost.cs:136` `GetAwaiter().GetResult()` 同步阻塞                                                                                         | 重构（异步初始化门）                                   |
| P1-7  | 事件 lambda 订阅**全程无 `-=`**，行 VM 被长生命周期 `TaskItem` 永久持有                                                                                                              | 重构（弱事件 / Dispose）                            |
| P1-9  | `Core/Scheduling/GateEvaluator.cs:114-170` 在 Core 层直接 `new Process()` + `cmd.exe /c "{cmd}"`                                                                      | 隔离（抽 `IProcessRunner` 至 Infrastructure）      |
| P1-10 | `BatchEngine.cs:196,226,1531` 绕过自有 `ITaskFileStore` 直接 IO                                                                                                         | 重构（IO 全走抽象）                                  |
| P1-11 | Vault 目录继承默认 ACL；`manifest.json` 明文；`WriteManifest` 是同类中唯一非原子写                                                                                                    | 重构（收 ACL + 统一原子写）                            |
| P1-12 | `CliPermissionSettings.cs:17-46` 静默把用户 CLI 的 `defaultMode` 改为 `bypassPermissions`（**影响范围不止本工具**）                                                                  | 重构（作用域收窄 + 原子写）                              |
| P1-13 | `CliProcessRunner.cs:311-322` `KillTree` 依赖外部 `taskkill` + `WaitForExit(3000)` 同步阻塞                                                                               | 重写（.NET 原生 `Kill(entireProcessTree:true)`）   |
| P1-14 | `LocalGitIntegration.cs:80-101` `WaitForExitAsync(ct)` 无超时、无 stdin 重定向                                                                                            | 重构（超时 + stdin 重定向）                           |
| P1-15 | 高危模块零覆盖：`CodeBuddyCreditsApiClient`、`XlsxTaskParser/Writer`、`BackupManager`、`LocalGitIntegration`、`ShutdownService`、`DangerousOperationGuard`、`CliCommandBuilder` | 补齐（先 `XlsxTaskWriter` + `CliCommandBuilder`） |

**本版新增项**

| ID        | 位置                                                                      | 债务                                                                                                                                                       | 影响                                                                          | 成本 | 偿还方式               |
| --------- | ----------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- | -- | ------------------ |
| **P1-16** | `Infrastructure/CredentialVault/CredentialLocationRegistry.cs:40,72,74` | `public static class` + `static readonly Dictionary` + `public static void SetLiveRootOverride(...)`；生产代码 `Desktop/Services/EngineHost.cs:94` 调用它做全局路径注入 | Service Locator 反模式 + **非线程安全 `Dictionary`**；全局状态被 UI 生命周期意外改写（原 P1-8 的实体化） | 中  | 重构（显式传入，取消静态入口）    |
| **P1-17** | 5 个 csproj                                                              | **无 `Directory.Packages.props`（无 CPM）**，包版本手写散落；`Core.csproj` 引用 `Microsoft.Extensions.Logging.Abstractions 10.0.13` 而全仓 `ILogger` 使用 **0 处**（无效依赖）      | 版本漂移；依赖图不可审计                                                                | 小  | 重构（引入 CPM + 删无效依赖） |
| **P1-18** | `Infrastructure/Persistence/JsonCreditsClaimStore.cs:21`                | 第 18 行 `directory ?? ...` 建目录，第 21 行却 `Path.Combine(directory!, ...)` → 默认构造**必抛 `ArgumentNullException`**；生产唯一调用点 `EngineHost.cs:202` 恰好总传参，故未暴露        | 定时炸弹：任何走默认构造的新调用点立刻崩                                                        | 极小 | 重构（1 行）            |
| **P1-19** | Desktop 层（churn 实测）                                                     | `ConfigView.axaml`、`TemplateEditorViewModel.cs`、`ReportViewModel.cs`、`ConfigViewModel.cs` 等 4 次提交被改（全仓仅 12 提交，即 1/3 提交都动过），且**零测试**                      | 界面契约未稳定却已铺开 5,411 行 → 重构 Desktop 时回归面最大                                     | 大  | 重构（补测试 + 收敛契约）     |
| **P1-20** | `Desktop/Converters/ConfigHubConverters.cs:18,31`                       | 两处 `ConvertBack => throw new NotImplementedException()`                                                                                                  | 双向绑定一旦触发即崩溃                                                                 | 极小 | 重构                 |
| **P1-21** | `Core/Options/RunOptions.cs:264,268,271`                                | 生产 API 地址 `https://www.codebuddy.cn` 及 `/billing/meter/*` 路径写进默认值                                                                                        | 环境切换只能靠覆盖，测试易打到生产                                                           | 小  | 重构（抽配置/常量）         |

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

### 阶段 0：止血（第 1 个迭代，A 组 10 项）

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

### 阶段 2：动骨（第 2–4 个迭代）

严格按依赖顺序，**期间冻结新功能**（§4.6 债务预算）：

```
P1-3 提 IBatchEngine 接口 + 窄化构造      ← 必须先做，否则无法增量验证
   ↓
P1-4/P1-5 引入 DI + IEngineFactory（解除 UI→Infrastructure 倒置）
   ↓
P1-9  IProcessRunner 抽离（把进程 IO 赶出 Core）
P1-10 引擎内 IO 全部走 ITaskFileStore
P1-16 取消 CredentialLocationRegistry 静态覆写
   ↓
P1-1  BatchEngine 按 5 协作者拆分（strangler fig，见 §3.4）
   ↓
P1-2  状态机外置
   ↓
P1-19 Desktop 补测试 + 契约收敛
```

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

- `commit-msg` 钩：拒绝空消息、拒绝纯标点、要求正文含「改了什么 / 为什么」
- `pre-commit` 钩：`dotnet format --verify-no-changes`（阶段 1 起）
- 提交模板 + `CODE-REVIEW §5.2` 的单 PR 单一关注点约束

### 4.2 第二道墙：编译期门禁（编不出）

```xml

<PropertyGroup>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <AnalysisLevel>latest-recommended</AnalysisLevel>
  <AnalysisMode>Recommended</AnalysisMode>
  
  <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
</PropertyGroup>
```

配套：

- 引入 `Directory.Packages.props`（CPM），包版本集中（P1-17）
- **CI 必须用 `--no-incremental` 全量基线**，否则复现 P0-12 的 0 警告假象
- 分批阻断顺序：Core → Agents → Infrastructure → Desktop（与测试补齐同步，避免一次性爆出几百警告）

### 4.3 第三道墙：架构约束自动化（过不了测试）

用 ArchUnitNET 把分层规则固化为**测试**，而不是文档约定：

| 断言                                              | 防什么                |
| ----------------------------------------------- | ------------------ |
| `Core` 不得引用 `Infrastructure`/`Agents`/`Desktop` | 防 P1-4/P1-9 类倒置复发  |
| `Core` 命名空间下不得出现 `System.Diagnostics.Process`   | 防 Core 再起进程        |
| `Core`/`Infrastructure` 不得出现 `Avalonia.*`       | 防 P2-7 类 UI 契约反向侵入 |
| `Desktop` 不得直接 `new` Infrastructure 具体类型（须经接口）  | 防 P1-4 复发          |
| `Domain` 下不得出现 `INotifyPropertyChanged`         | 防 P2-7 扩散          |
| 生产程序集不得出现 `FakeClock`/`Mock*` 类型                | 防 P2-8 测试替身进生产     |
| `Catch (Exception)` 占比不得上升（基线 37/69 = 54%）      | 防吞异常继续扩散           |

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
- [ ] 是否新增 `catch { }`，或 `catch(Exception)` 后仅记 WARN？（当前 37 处，是排障困难主因）
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

### 4.8 度量与趋势看板

每迭代采集，**看趋势而非绝对值**：

| 指标                     | 当前基线（实测）                                                     | 目标方向           |
| ---------------------- | ------------------------------------------------------------ | -------------- |
| 最大文件行数                 | 1960                                                         | ↓              |
| 最大方法行数                 | 249                                                          | ↓              |
| 全量构建警告数                | Core 36（增量显示 0 —— 假象）                                        | 先采集真实值，再 ↓ 至 0 |
| `catch (Exception)` 占比 | 37/69 = 54%                                                  | ↓              |
| 高危模块零覆盖数               | 7                                                            | ↓ 至 0          |
| Desktop 测试数            | **0**（且测试项目未引用）                                              | ↑              |
| 空提交信息数 / 迭代            | 10/12 历史                                                     | → 0 并保持        |
| 误提交产物数                 | 10                                                           | → 0 并保持        |
| P0 未偿数                 | 17                                                           | → 0 并保持        |
| 每迭代**新增**债务数           | 未采集（阶段 −1 起采集）                                               | → 趋 0          |
| churn 热点 Top3          | `BatchEngine.cs`(4)、`RunOptions.cs`(4)、`ConfigView.axaml`(4) | 热点应随拆分下降       |

> **台账登记改用不变量描述，不用行号**。例：P0-1 登记为「`TryFallbackAsync` 中 `cost` 必须被 `MaxCostPerTask` 截断」并配断言测试。理由：既有审查报告记录 `:1011`，实测已漂移到 `:1406`，行号型台账必然失效。

### 4.9 引入顺序（避免门禁一次性压垮团队）

```
当天   : 提交规范 + gitignore 目录级 + 清产物（阶段 −1 的 1–3）
迭代1  : CI（--no-incremental，只报不阻断）+ 格式门禁 + 测试分层 + 盲区补齐
迭代2  : EnforceCodeStyleInBuild + ArchUnitNET 断言（Core 优先）
迭代3  : 复杂度阈值按基线标定（先 warning） + 逃逸阀登记
迭代4  : 复杂度阈值转 error + TreatWarningsAsErrors 分批开启
持续   : PR 清单 + 20% 预算 + 台账月度复核
```

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

| 项               | 复核方式                            | 结果                                                                |
| --------------- | ------------------------------- | ----------------------------------------------------------------- |
| P0-1 成本封顶 no-op | 读 `BatchEngine.cs:1406`         | **仍存在**：`Math.Min(cost, Math.Max(MaxCostPerTask, cost))`          |
| P0-2 输出未排空      | 读 `CliProcessRunner.cs:213,293` | **仍存在**；工作区在途改动（+3/−2）仅涉及 `PromptClassifier` 自动应答分类，**与 P0-2 无关** |
| P1-6 同步阻塞       | grep                            | 2 处确认：`EngineHost.cs:136`、`MainViewModel.cs:20`                   |
| P0-4/P0-5 并发    | v1.0 结论沿用                       | 未复核，建议偿还前补并发测试确认                                                  |

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
