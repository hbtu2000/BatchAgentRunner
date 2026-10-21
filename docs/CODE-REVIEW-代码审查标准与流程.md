# BatchAgentRunner 代码审查标准与流程（v1.0）

> 制定者：火眼眼（代码审查专家）｜适用工程：`BatchAgentRunner.slnx`（C# / .NET 10 / Avalonia 12 跨平台桌面）
> 适用范围：Core / Agents / Infrastructure / Desktop / Tests 五个分层；一期 P0 + P1 功能。
>
> 本文档是「标准（审什么）」+「流程（怎么审）」合一的落地手册，可直接作为 Pull Request / 合并请求的评审依据。

---

## 0. 现状基线（先看清楚起点）

先肯定已经做对的地方，再看差距——标准要补的是差距，不是重复已有的好习惯。

**已经做好的（请保持）**
- 分层清晰：`Core`（领域/调度/引擎）↔ `Agents`（CLI 适配）↔ `Infrastructure`（持久化/凭据/任务文件）↔ `Desktop`（Avalonia 视图模型）职责基本不串。
- 关键路径有 XML 文档注释（`BatchEngine`、`DegradationMatrix`、`AccountConfig` 等），并标注了 PRD/DESIGN 的出处条款，可追溯。
- 可靠性关键处做了原子写（`Infrastructure/TaskFiles/AtomicFileWriter.cs`：临时文件 → 刷新落盘 → `File.Move` 原子替换）。
- 安全有两道闸：凭据 Vault（DPAPI 加密、明文不落盘不写日志）+ 危险操作防护（`Agents/Internal/DangerousOperationGuard.cs` 黑名单/白名单）。
- 测试套件存在且覆盖核心调度、降级矩阵、额度判定、凭据切换、端到端等（`tests/BatchAgentRunner.Tests`）。

**参差不齐、需要标准兜住的差距**
> 2026-10-18 更新（治理阶段 −1/0/1 完成后）：以下各项为本文档制定时的起点差距，**已全部闭合**——`.editorconfig` 已建、`TreatWarningsAsErrors=true` 已开、静态分析已进构建、格式门禁已进 pre-commit 钩（详见 §7.2/§7.5 与治理方案 §5.4）。保留原文作为起点留痕。

- **没有任何静态分析基线**：~~仓库无 `.editorconfig`、无 `.ruleset`/`.globalconfig`，`Directory.Build.props` 里 `TreatWarningsAsErrors=false`。~~ → 格式化、warning 全靠人眼，最容易出现「参差」。（已闭合）
- **并发复杂度高**：`BatchEngine` 同时用 `_writeLock` / `_statsLock` / `_waitGate` 三把锁 + `SemaphoreSlim` + 多 `CancellationTokenSource`，锁的配对（`lock`→`Release`）、`ConfigureAwait(false)`、取消传播是主要正确性风险区。
- **配置序列化有「坑」**：snake_case 字段、派生属性必须 `[JsonIgnore]`、集合不能用接口类型声明（否则 `System.Text.Json` 反序列化抛 `NotSupportedException`）——这些是项目历史踩过的坑，但只写在记忆里，没进审查清单。
- **个别风格争议点**：`BatchEngine` 中 `goto case AgentFailureKind.QuotaExhausted;` 用于失败类型升级，可读性见仁见智，需在标准里给明确口径。
- **桌面端异常边界**：引擎侧已加 `ExecuteTaskSafeAsync` 兜底（2026-10-16 修复「异常被 UI 吞掉」），但桌面层是否也保证异常不静默冒泡、不阻塞 UI 线程，需要统一约定。

---

## 1. 审查原则（火眼眼方法论）

1. **教，而不只是挑错**：每条评论争取让人「知其所以然」，下次能自己避坑。
2. **一次给完整反馈**：不要跨多轮零散 drip-feed，避免作者反复返工。
3. **分级处理**：🔴 必须修 / 🟡 须回应 / 💭 可忽略，不在细枝末节上卡合并。
4. **对事不对人**：评论指向代码与行为，不评价作者水平。
5. **能机器拦的别靠人眼**：凡是 `.editorconfig` + 分析器 + `dotnet format` 能挡的（格式、空值、可空注解、明显冗余），不占人工评审带宽。
6. **先保正确性、安全、数据可靠，后谈风格**：无人值守批处理工具，错一次可能是整批任务损坏或凭据泄露。

---

## 2. 严重度分级

| 标记 | 名称 | 含义 | 合并门槛 |
|------|------|------|----------|
| 🔴 | Blocker（必须修） | 正确性错误、安全漏洞、数据损坏/丢失风险、竞态/死锁、破坏 API 契约、关键路径无异常处理 | **不修不能合并** |
| 🟡 | Suggestion（应修复） | 缺输入校验、命名/逻辑令人困惑、重要行为缺测试、明显性能问题（N+1/不必要分配）、违反项目硬规则 | 作者须**逐条回应**（修 or 说明理由） |
| 💭 | Nit（可选） | 纯风格不一致（无 linter 兜底时）、微小命名、文档缺口、值得考虑的替代方案 | 可忽略，不阻塞合并 |

> 评论格式固定见第 6 节。一个 PR 里 🔴 数量应极少；🟡 是评审价值主战场。

---

## 3. 审查清单（按维度，可直接勾选）

### 3.1 正确性 · 并发安全（本项目最高优先级）
- 🔴 **锁配对**：`_writeLock`/`_statsLock` 进 `lock` 后，所有分支（含 `continue`/异常）是否仍会释放？优先用 `lock` 语句而非手动 Monitor。
- 🔴 **`SemaphoreSlim` / `_waitGate`**：`WaitAsync` 必须有配对的 `Release`，且放在 `finally` 中（`BatchEngine.WaitForRestoreAsync` 是正确样例）。
- 🔴 **取消传播**：`CancellationToken` 是否从入口一路传到 leaf（`StartAsync`→`DispatchAsync`→`ExecuteTaskAsync`→`adapter.ExecuteAsync`）？中途新建 `CreateLinkedTokenSource` 是否把父 token 链进去？
- 🔴 **`ConfigureAwait(false)`**：`Core`/`Infrastructure`/`Agents` 全部库代码 async 必须 `.ConfigureAwait(false)`（`BatchEngine` 当前已遵照，新增代码必须一致）。**桌面层 `Desktop` 的 async 可省略**（需要回到 UI 上下文）。
- 🔴 **共享可变状态**：`_tasks`、`_accountUsage`、`_modelUsage`、`_timeline`、`_taskWaitSeconds` 等跨线程字典/列表是否都进了对应锁？新增字段若被多任务并发读写，必须补锁或改成并发集合。
- 🟡 **`goto case` 升级**：`BatchEngine` 用 `goto case AgentFailureKind.QuotaExhausted;` 把「退避耗尽」升级为额度耗尽。新增代码**默认禁止 `goto`**；若复用此模式，必须加注释说明跳转语义且不跨方法。
- 🟡 **整数字面量与溢出**：额度计数、`max_attempts` 等用 `int` 足够，但重试/退避的 `TotalSeconds` 计算注意不会因超大配置溢出。

### 3.2 安全（无人值守工具的生命线）
- 🔴 **凭据明文**：任何账号密码 / API Key / token **绝不**写入任务文件、日志、报告、异常消息（`AccountConfig` 仅存 `credential_ref` 是正确样例）。
- 🔴 **危险操作防护**：CLI 静默等待确认时，命中 `dangerous_blacklist` 必须**拒绝并杀进程**；白名单优先级高于黑名单（见 `DangerousOperationGuard`），审查时确认黑名单词条覆盖所用 CLI 的真实措辞。
- 🔴 **命令/参数注入**：拼 CLI 参数或进程启动参数时，禁止字符串拼接可执行与参数；使用参数数组 / `ProcessStartInfo.ArgumentList`，凭据经环境变量注入（`BATCH_AGENT_ACCOUNT` / `CODEBUDDY_API_KEY`）而非内联。
- 🔴 **路径注入**：`task_file_path`、工作空间路径、临时文件路径用户可控，须校验在允许目录内、拒绝 `..\` 越界、symlink 指向敏感区。
- 🟡 **DPAPI / Vault**：凭据静态存储经 DPAPI 加密；审查 Vault 读写路径确认加密在落盘前、解密在内存中即清零、审计只记账号 id 与哈希前 8 位。
- 🟡 **日志脱敏**：新增日志点确认不打印 `credential_ref` 明文、不打印完整 token、不打印任务指令里的敏感内容。

### 3.3 配置与序列化（项目历史坑，优先机器 + 人工双保险）
- 🔴 **字段命名 snake_case**：所有会被 `config.json` 序列化的 DTO 属性，`[JsonPropertyName]` 必须为 snake_case（如 `credential_ref`、`model_candidates`、`fallback_models`）。
- 🔴 **派生/计算属性必须 `[JsonIgnore]`**：`AccountConfig.Label`、`EffectiveModelPool`、`HasFallbackModels` 等只读计算属性必须标 `[JsonIgnore]`，否则会被写进 `config.json` 干扰人读、还可能破坏反序列化。
- 🔴 **集合类型不用接口声明**：反序列化目标**禁止** `IReadOnlyList<T>` / `IList<T>` / `ISet<T>` 等接口类型（`System.Text.Json` 无法实例化接口，加载会抛 `NotSupportedException`）。用 `List<T>` 具体类型；只读暴露可用 `IReadOnlyList<T>` 接口但**存储字段**必须是具体类。
- 🟡 **枚举序列化**：枚举一律走字符串（`JsonDefaults` 已配 `JsonStringEnumConverter`），新增枚举类型确认不会被序列化成数字。
- 🟡 **解析宽容度**：`JsonDefaults` 允许注释 + 尾随逗号 + 忽略未知字段（前向兼容）；新增配置读取点确认沿用 `JsonDefaults.Options`，不要自起 `new JsonSerializerOptions`。
- 💭 **`DefaultIgnoreCondition.WhenWritingNull`**：确认 null 字段不写入 JSON，避免配置文件噪声——新增 DTO 时不必特意改，但别覆盖全局配置。

### 3.4 数据可靠性（批处理工具的核心承诺）
- 🔴 **原子写回**：任何「覆盖式」写入任务文件 / 配置 / 冷却记录，必须走 `AtomicFileWriter`（临时文件 + `Flush(flushToDisk:true)` + `File.Move(overwrite)`）。禁止直接 `File.WriteAllText` 覆盖原位——强杀/断电会损坏任务文件。
- 🔴 **备份保留**：`backup_retention` 控制保留份数，删除更旧备份的逻辑须确认不会误删当前文件，且每次启动前备份一次。
- 🟡 **JSON 原地回写 vs Markdown sidecar**：`*.json` 任务原地原子回写；`*.md` 任务文件是**只读输入**，状态/结果写 sidecar `<name>.md.result.json`。新增任务源类型时不能破坏这套区分。
- 🟡 **部分输出保留**：任务被中断/切换时，已产生的磁盘副作用保留，最终结果依赖指令幂等性——审查切换逻辑确认不会清空已完成的部分输出（除非是 `interrupted` 重跑的刻意清除，见 `ExecuteTaskAsync` 的 `HasPartialResult` 处理）。
- 🟡 **结果截断**：`result_max_kb` 截断时提示「完整内容见运行日志」且日志里有保留，不能只截断不说明。

### 3.5 可维护性与架构
- 🟡 **分层职责**：`Core` 不依赖 `Desktop`/`Infrastructure` 具体实现（只依赖 `Abstractions` 接口）；CLI 适配只在 `Agents`；文件/系统操作只在 `Infrastructure`。跨层 new 具体类要警惕。
- 🟡 **命名与注释**：公共类型/方法有 XML 注释且标注关键决策出处（PRD/DESIGN 条款）；命名表达意图，避免 `tmp`、`data2`、`flag` 之类。
- 🟡 **注释只描述当前实现（P2-26）**：禁止以注释自述计划性延期或能力未实现（"二期扩展""暂不支持""待实现"等）——真实延期登记 `TECH-DEBT-技术债务治理方案-v2.0.md` §4.7.1 计划性延期台账后在注释引用台账 ID；已由 `CommentHygieneTests` 源码扫描自动把关，新增即测试失败。
- 🟡 **门禁例外先登记再落地（P2-30）**：`#pragma warning disable` / `[SuppressMessage]` / `.editorconfig` `severity = none` / csproj `NoWarn` / 钩子诊断过滤，一律先在 `TECH-DEBT-技术债务治理方案-v2.0.md` §4.7 逃逸阀台账登记四要素（发现日 / 责任人 / 到期日 / 不变量断言），再在就近注释写明「逃逸阀台账 #N / 到期日 YYYY-MM-DD / 责任人 X」；已由 `EscapeValveHygieneTests` 自动把关——未登记、无到期日、到期未清理、代码与台账不一致，任一即测试失败。
- 🟡 **路径常量与 JSON 仓储样板（P2-31）**：应用本地数据目录（`%LOCALAPPDATA%` 下）一律走 `Infrastructure/AppPaths` 登记（`Root` / `ConfigDirectory` / `TemplatesDirectory` / `VaultDirectory` / `RunsDirectory` / `LogsDirectory` / `MockLiveDirectory`），`src` 内不得再内联应用名路径魔法串；新增单文件 `Json*Store` 须继承 `Infrastructure/Persistence/JsonStoreBase<T>`，只实现 `CreateEmpty()`（自愈/迁移走 `NormalizeAsync()` 钩子），不得自带「建目录 / `JsonSerializer` / `AtomicFileWriter`」样板；已由 `AppPathsHygieneTests` 自动把关——内联魔法串、样板回潮、`AppPaths` 派生目录脱根，任一即测试失败。
- 🟡 **方法/类复杂度**：单方法超过 ~80 行或嵌套超过 4 层，建议拆分子方法（`ExecuteTaskAsync` 已按 P1-2 内拆 1790708 显式状态机化，全部子方法 ≤60 行 / 圈复杂度 ≤15；新增分支优先抽子方法而非继续膨胀）。
- 🟡 **色值经 token / 单一入口（P2-10 / P2-11）**：UI 字面色（状态 / 级别 / diff / 导航）一律经 `Desktop/Helpers/AppBrushes` 按 token 名取色（`{DynamicResource XxxBrush}` 于 XAML、`AppBrushes.ForXxx` 于代码），不得再内联 `Color.Parse("#…")` / `#hex`；token 表在 `Styles/AppStyles.xaml`（Light/Dark 双字典），新增色须在该处登记而非散落。已由 `AppBrushesTests` 把关——`src` 内 `Color.Parse` 仅允许出现在 `AppBrushes` 的 fallback 字面量，`AppStyles.xaml` 之外不得出现字面色。
- 🟡 **XAML 须带类型安全绑定（P2-12 增量）**：改 / 新增视图时根节点加 `x:CompileBindings="True"` + `x:DataType`，`DataTemplate` 也须标 `x:DataType`（子项类型）——把属性拼写错误从「运行时静默失效」升为编译错误；全局默认维持 `false`，不得为图省事翻转 `Desktop.csproj` 的 `AvaloniaUseCompiledBindingsByDefault`（会让未带类型的绑定失效）。
- 💭 **`record` vs `class`**：值语义（组合键、结果）用 `record`；有可变状态/身份的对象用 `class`；当前 `AgentCombo`/`RestoreCandidate` 用 `record` 合理，新增时遵循此区分。

### 3.6 性能
- 🟡 **不必要的具体化**：避免对 `IEnumerable` 反复 `ToList()`/`ToHashSet()`；`DegradationMatrix` 构造时 `pools[a]` 已 `ToList()` 合理，但热路径里避免每轮重算。
- 🟡 **轮询 vs 事件**：等待恢复用 `TaskCompletionSource` + 2 秒切片轮询（`WaitForRestoreAsync`）是可接受的；新增「等待某状态」逻辑优先复用事件/回调，而非忙等 `while(true)` 无延迟。
- 💭 **大对象分配**：`Truncate`、字符串拼接 `task.Result + "\n" + output` 在长任务下会频繁分配；高频路径可考虑 `StringBuilder`/分块，但非阻塞项。

### 3.7 测试
- 🔴 **重要行为须有测试**：降级矩阵（ModelFirst/AccountFirst）、额度错误分类、危险操作拦截、原子写、配置序列化（含接口集合/派生属性 `[JsonIgnore]` 反序列化）、waiting/blocked 转换，任一修改都必须有对应测试不退化。
- 🟡 **用 TestDoubles / MockAdapter**：用 `tests/.../TestDoubles.cs` 和内置 `MockAdapter` 仿真，不依赖真实 CLI（`config.example.json` 里 `agent_kind: Mock` 是设计意图）。新增调度逻辑测试应可离线跑。
- 🟡 **边界覆盖**：空矩阵、全部 blocked、单任务超时、额度耗尽无限等待、并发 >1 的凭证覆盖告警。
- 🟡 **命名约定**：测试类 `XxxTests`、方法用 `Method_Scenario_Expected` 或中文描述，与现有 `BatchEngineTests`/`DegradationTests` 风格一致。

### 3.8 桌面端（Avalonia / ViewModel）
- 🔴 **不阻塞 UI 线程**：VM 中耗时操作（启动引擎、预检、文件读写）必须 `await` 异步，禁止 `.GetAwaiter().GetResult()` / `.Wait()` 在 UI 线程同步阻塞。
- 🔴 **异常不静默冒泡**：引擎侧已用 `ExecuteTaskSafeAsync` 兜底，桌面层订阅 `EventRaised`/`TasksChanged` 时确认异常被记录而非被框架吞掉（这正是 2026-10-16 修复的痛点）。
- 🟡 **属性通知**：VM 实现 `INotifyPropertyChanged`（基类 `ViewModelBase`），集合用 `ObservableCollection`；避免手动 `Raise` 遗漏导致界面不刷新。
- 🟡 **绑定与转换**：`Converters/`（如 `StatusClassConverters`、`ConfigHubConverters`）必须是无状态、可复用的纯转换，不在这里做业务判断。

---

## 4. 项目专属硬规则（违反即 🔴/🟡，来自已确认约定）

> 这些是从项目记忆里固化的约定，审查时**优先机器校验、人工抽检**。

| # | 硬规则 | 违反级别 | 校验方式 |
|---|--------|----------|----------|
| H1 | 配置字段 snake_case（DTO `[JsonPropertyName]`） | 🔴 | `.editorconfig` + 人工 |
| H2 | 派生/计算属性标 `[JsonIgnore]` | 🔴 | 人工 + 测试 `RunOptionsSerializationTests` 扩展 |
| H3 | 反序列化目标集合用具体 `List<T>`，禁用接口类型 | 🔴 | 编译期 + 反序列化测试 |
| H4 | 枚举走字符串序列化（`JsonStringEnumConverter`） | 🟡 | `JsonDefaults` 已全局，新增枚举免忧 |
| H5 | 凭据明文不落盘、不写日志 | 🔴 | 人工 + 日志脱敏审查 |
| H6 | Markdown 任务文件只读，状态写 sidecar | 🟡 | 人工 |
| H7 | 覆盖式写入一律原子写（`AtomicFileWriter`） | 🔴 | 人工 grep `File.WriteAllText` |
| H8 | 危险操作黑名单优先拦截、白名单优先于黑名单 | 🔴 | 单测 `DangerousOperationGuard` |
| H9 | 暂停等待时长从任务/批处理时长剔除 | 🟡 | 单测 `ApplyPausedExclusion` |
| H10 | 库层 async 必须 `.ConfigureAwait(false)`（测试方法相反：一律省略，见 xUnit1030） | 🟡 | 分析器 `CA2007`（生产为 error；测试项目经 §4.7 台账豁免） |
| H11 | 不新增分析器警告（存量已清零） | 🔴 | `TreatWarningsAsErrors=true`，构建期即失败 |
| H12 | 包版本只写在 `Directory.Packages.props` | 🔴 | CPM + NU1008 构建错误 |
| H13 | 测试分层：E2E/Usability/Process 打 `[Trait("Category", …)]`；高危路径（凭证/进程/Git/安全拦截/二进制写回）须带测试 | 🟡 | CI 快慢两步 + 人工 |
| H14 | 持 `IDisposable` 字段的类型自身须可释放（`SemaphoreSlim`/`CancellationTokenSource`/日志） | 🟡 | 分析器 `CA1001`（error） |
| H15 | 门禁例外须登记 §4.7 逃逸阀台账四要素并就近标注台账 ID / 到期日 / 责任人 | 🔴 | `EscapeValveHygieneTests`（2026-10-21 P2-30 落地） |
| H16 | 应用本地数据目录路径一律走 `AppPaths` 登记；单文件 JSON 仓储继承 `JsonStoreBase<T>`，不复制「建目录 / `JsonSerializer` / `AtomicFileWriter`」样板 | 🟡 | `AppPathsHygieneTests`（2026-10-21 P2-31 落地） |
| H17 | UI 字面色经 token / 单一入口（P2-10 / P2-11）：状态 / 级别 / diff / 导航等字面色一律经 `Desktop/Helpers/AppBrushes` 按 token 名取色（`{DynamicResource XxxBrush}` 于 XAML、`AppBrushes.ForXxx` 于代码），不得内联 `Color.Parse("#…")` / `#hex`；token 表仅在 `Styles/AppStyles.xaml`（Light/Dark 双字典）登记 | 🟡 | `AppBrushesTests`（源码扫描：`src` 内 `Color.Parse` 仅允许出现在 `AppBrushes` 的 fallback 字面量；`AppStyles.xaml` 之外不得出现字面色） |
| H18 | XAML 须带类型安全绑定（P2-12 增量）：改 / 新增视图时根节点加 `x:CompileBindings="True"` + `x:DataType`，`DataTemplate` 也须标 `x:DataType`（子项类型） | 🟡 | 人工 + 构建（编译绑定把属性拼写错误从「运行时静默失效」升为编译错误）；全局默认维持 `false`，不得翻转 `Desktop.csproj` 的 `AvaloniaUseCompiledBindingsByDefault` |

---

## 5. 审查流程

### 5.1 角色
- **作者（Author）**：完成实现、跑通构建与测试、自查清单、写清变更说明（含 PRD/DESIGN 条款引用）。
- **审查者（Reviewer）**：至少 **1 人**；**核心模块（Core/Engine、Agents 适配、Infrastructure 凭据/任务文件）须 2 人**（其中 1 人可为资深）。
- 小团队可轮值，但作者不能 self-approve 核心模块。

### 5.2 标准节奏（无需重型 CI 也能跑）
```
作者实现 → 本地自查(§3清单) → 提交(小步、语义化) → 开 PR/MR
   → 审查者评审(§6格式) → 作者修改/逐条回应 → 复阅 → 合并
```
- **小步提交**：单 PR 聚焦一个关注点（一个 bug / 一个功能 / 一次重构），避免「500 行大杂烩」。
- **变更说明必含**：改了什么、为什么（引 PRD/DESIGN 条款）、怎么测的、对调度语义有无副作用。

### 5.3 合并门禁（建议，渐进落地）
| 门禁 | 当前状态 | 目标 |
|------|----------|------|
| 构建通过（`dotnet build`） | 已可 | 必须 |
| 测试通过（`dotnet test`） | 已可 | 必须 |
| `dotnet format` 无差异 | **已落地**（2026-10-18：`pre-commit` 钩强制，见 §7.2/治理方案 §4.1） | 必须 |
| 无新增 compiler warning | **已开启** `TreatWarningsAsErrors=true`（2026-10-18，存量清零后） | 必须 |
| 核心模块双人审 | 无 | 必须 |
| 静态分析（CA 规则）零新增 🔴 | **已落地**（`EnforceCodeStyleInBuild` + `AnalysisLevel=latest-recommended`，2026-10-18） | 必须 |

### 5.4 时限与 SLA（建议）
- 普通 PR：审查者 **1 个工作日内** 首轮反馈；作者 **2 个工作日内** 完成修改。
- 紧急修复：标 `urgent`，审查者 **4 小时内** 响应。
- 长期挂起（>5 工作日无活动）的 PR：作者主动关闭或重开说明。

### 5.5 合并标准
- 0 个未解决的 🔴；所有 🟡 已「修改」或「作者说明理由且审查者认可」；CI/本地门禁全绿；核心模块满足双人审。

---

## 6. 审查评论模板（火眼眼风格）

每条评论固定结构，复制即用：

```
🔴/🟡/💭 **<维度>：<一句话标题>**
位置：<文件:行号 或 方法名>
**为什么**：<根因/风险，引用 PRD/DESIGN 或本项目坑>
**建议**：<具体改法，能给代码片段最好>
```

示例：
```
🟡 **并发安全：StatsLock 未覆盖新字典**
位置：BatchEngine._taskWaitSeconds 新增写入点（约 L909）
**为什么**：该字典被 `ApplyPausedExclusion` 与 `WaitForRestoreAsync` 并发读写，
当前仅后者锁了 `_statsLock`，前者若漏锁会偶发丢更新。
**建议**：所有对 `_taskWaitSeconds` 的读写都包进 `lock (_statsLock)`，
与现有 `TrackUsage` 保持一致。
```

```
🔴 **序列化：派生属性缺 [JsonIgnore]**
位置：AccountConfig.EffectiveModelPool
**为什么**：反序列化 config.json 时该属性会被尝试写回，污染配置且可能反序列化失败。
**建议**：加 `[JsonIgnore]`，参考同类 `Label` / `HasFallbackModels` 的写法。
```

---

## 7. 工具与自动化基线

### 7.1 立刻补的 `.editorconfig`（复制到仓库根，零风险）
```ini
root = true

[*.cs]
indent_style = space
indent_size = 4
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true
dotnet_sort_system_directives_first = true
csharp_style_namespace_declarations = file_scoped:suggestion

# 可空注解一致性
dotnet_diagnostic.CS8600.severity = warning
dotnet_diagnostic.CS8602.severity = warning

# 库层 async 必须 ConfigureAwait(false)
dotnet_diagnostic.CA2007.severity = warning
```

### 7.2 分析器门禁（`Directory.Build.props`，2026-10-18 阶段 1 已全量落地）
```xml
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<AnalysisLevel>latest-recommended</AnalysisLevel>
<AnalysisMode>Recommended</AnalysisMode>
<!-- 存量警告清零后已开启：新增任何分析器警告即构建失败。
     豁免须走治理方案 §4.7 逃逸阀台账四要素（发现日 / 责任人 / 到期日 / 不变量断言），
     并在代码就近注释写明「逃逸阀台账 #N / 到期日 YYYY-MM-DD / 责任人 X」；
     2026-10-21 P2-30 起由 EscapeValveHygieneTests 机器校验。 -->
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```
> 历史路径记录：直接开 `TreatWarningsAsErrors` 会因历史 warning 让构建失败；本项目先开 `EnforceCodeStyleInBuild` + 分析器，阶段 1（2026-10-18）把 99 个存量警告清零后才升格为 error。测试项目的 `CA1707`/`CA2007`/`CA1861` 豁免见 `tests/**/*.csproj` 注释与治理方案 §4.7 台账。

### 7.3 格式统一
- 推荐 `dotnet format` 或 `CSharpier` 作为保存时/提交前钩子，让 §3 里所有风格项交给机器。

### 7.4 测试与本地门禁命令
```bash
dotnet build BatchAgentRunner.slnx
dotnet test tests/BatchAgentRunner.Tests
dotnet format BatchAgentRunner.slnx --verify-no-changes   # pre-commit 钩已强制（CHARSET 假阳性除外，治理方案 §4.7 #9）
```

### 7.5 轻量 CI（已落地，`.github/workflows/ci.yml`）
- 触发：push/PR 到 `master` → `windows-latest` + .NET 10，`dotnet build --no-incremental`（必须全量重建，否则复现 P0-12 的 0 警告假象）+ 分层测试（快跑单元 / 慢跑 E2E·Usability·Process）。
- 核心模块（路径 `src/BatchAgentRunner.Core/**`、`src/BatchAgentRunner.Agents/**`、`src/BatchAgentRunner.Infrastructure/**`）要求 2 审批。
- 分支保护（必需状态检查）尚未配置：CI 红当前不阻止合并，需仓库管理员在 GitHub 仓库设置中补齐（治理方案 §5.3 边界 2）。

---

## 8. 落地路线图

- ~~**短期（0–2 周）**：本文件落地 + 加 `.editorconfig` + 团队按 §3 清单互审；核心模块开始双人审；记录前 10 个 PR 的 🔴/🟡 分布。~~ ✅ 已完成（`.editorconfig`、双人审、CI 均已落地）
- ~~**中期（1–2 月）**：开 `EnforceCodeStyleInBuild` + 分析器，warning 清零；`dotnet format` 接提交前钩子；核心模块门禁硬化。~~ ✅ 已完成（2026-10-18：99 个存量警告清零 + `TreatWarningsAsErrors=true`，见治理方案 §5.4）
- **长期（3 月+）**：分支保护设为必需状态检查（GitHub 设置，待仓库管理员）；ArchUnitNET 架构断言 + 复杂度阈值（治理方案 §4.3/§4.4，迭代 2/3）；建立误切换率、审查时长、🔴 密度等指标，季度复盘迭代本标准。
- ~~`dotnet format` 接提交前钩子（`pre-commit`，治理方案 §4.1 已规划未落地）~~ ✅ **已完成（2026-10-18 阶段 1）**：`.githooks/pre-commit` 强制 `dotnet format --verify-no-changes` + 暂存 `.cs` 的 UTF-8 校验；`.editorconfig` 声明 `end_of_line=lf`、`.gitattributes` 锁 `eol=lf`。CHARSET 类假阳性（SDK 10.0.401 + `CodePage` 回归）按治理方案 §4.7 #9 过滤并登记。

---

## 9. 附录：本项目常见反模式速查

| 反模式 | 正确做法 | 出处/样例 |
|--------|----------|-----------|
| `File.WriteAllText` 覆盖任务文件 | `AtomicFileWriter.WriteAsync` | `AtomicFileWriter.cs` |
| DTO 派生属性没 `[JsonIgnore]` | 标 `[JsonIgnore]` | `AccountConfig.cs` |
| 反序列化集合声明为 `IReadOnlyList<T>` | 用 `List<T>` | 项目记忆坑 |
| 库代码 async 漏 `ConfigureAwait(false)` | 全部加（测试方法相反：省略） | `BatchEngine.cs` 已遵；xUnit1030 |
| 锁内 `continue`/异常后漏 `Release` | `finally` 中 `Release` | `WaitForRestoreAsync` |
| CLI 参数字符串拼接 | `ArgumentList` + 环境变量注入 | `DangerousOperationGuard` / Vault |
| 凭据/指令写日志 | 仅写账号 id + 哈希前 8 位 | `AccountConfig` 安全约定 |
| 危险操作白名单误含危险词 | 白名单严格、优先级高于黑名单 | `DangerousOperationGuard` |
| csproj 内联包版本 | 只写 `Directory.Packages.props`（NU1008） | P1-17 |
| 测试依赖磁盘 samples / 仓库根路径 | 读嵌入式资源副本 `TestSampleResources` | P2-21/P2-28 |
| publish/bin/obj 产物堆遗留在仓库根（ignore 了之、无人清理） | `.gitignore` 显式目录级规则 + 及时整堆清理 | P2-29 / `_harness/`（214 文件 / 1.2G 已清） |
| 内联 `"BatchAgentRunner"` 路径魔法串；新 `Json*Store` 复制「建目录 / `JsonSerializer` / `AtomicFileWriter`」样板 | 路径走 `AppPaths` 登记；仓储继承 `JsonStoreBase<T>`，只实现 `CreateEmpty()` / `NormalizeAsync()` | P2-31 / `AppPathsHygieneTests` |
| 测试里起真实进程 | 注入 `IProcessLauncher` 假启动器 | P2-21 |
| 持有 `SemaphoreSlim`/`CTS` 却不实现 `IDisposable` | 补 `IDisposable` 链并级联释放 | P0-12 / CA1001 |
| 源码存成 GBK / 带 BOM / 行尾 CRLF 混用 | UTF-8 无 BOM + LF（`pre-commit` 钩强制，`iconv` + `end_of_line=lf`） | P0-12 格式门禁 / SDK 10.0.401 CHARSET 回归（治理方案 §4.7 #9） |
| XAML 内联 `#hex` / 代码 `Color.Parse("#…")` 硬编码字面色 | 经 `AppBrushes` / `{DynamicResource XxxBrush}` 设计 token 单一入口（`AppStyles.xaml` Light/Dark 双字典登记），新增色在该处登记而非散落 | P2-10 / P2-11 / `AppBrushesTests` |
| XAML 绑定无 `x:CompileBindings` + `x:DataType`（根节点 + `DataTemplate` 子类型） | 增量开启编译绑定，把属性拼写错误从运行时静默失效升为编译错误；不翻转全局默认 | P2-12 / `LoginView`·`SetupWizardView` |

---

> 标准不是用来「卡人」的，是用来让「参差」不再靠运气。先跑起来、再逐步硬化，比一次到位更可持续。
