# BatchAgentRunner 对外契约文档（API Contract）

> 版本：v1.0 ｜ 日期：2026-10-22 ｜ 角色：资深软件架构师  
> 关联文档：`docs/ARCH-架构重构与工程化治理方案.md`（架构战略层）、`docs/TECH-DEBT-技术债务治理方案-v2.0.md`（战术台账）、`docs/config.schema.json`（机器可校验的配置契约）、`docs/adr/ADR-0007-config-contract-and-schema.md`（配置契约决策）  
> 权威声明：本文件描述的是**当前代码实际对外承诺的契约**。凡与 `ARCH` 文中 aspirational 描述不符之处，以本文件与代码为准，并标注「已知缺口」。

本工具对外承诺四类契约（与 `ARCH §2.2` 不动清单一致，最高优先级）：

1. **CLI 调用方式**（命令行参数、外部 agent 进程协议、headless 行为）
2. **`config.json` 字段语义与向后兼容**
3. **任务文件 sidecar（`<name>.md.result.json`）写入格式**
4. **`BatchReport` 报告结构**

任何破坏上述契约的改动，由 `tests/BatchAgentRunner.Tests` 的契约测试桶（`IBatchEngine` 行为 / config schema / sidecar 格式 / 报告结构）直接拦截（红）。

---

## 1. CLI 调用契约

### 1.1 发货应用形态（重要澄清）

> ⚠️ **与 ARCH 文中「headless 模式」表述的差异（如实声明）**：发货的 `BatchAgentRunner.Desktop` 入口 `Program.Main`（`src/BatchAgentRunner.Desktop/Program.cs:25`）**不做任何自定义命令行解析**——它仅把 `args` 原样转交 Avalonia 的经典桌面生命周期（`StartWithClassicDesktopLifetime(args)`，标准框架参数如 `--no-gpu`）。**当前发货应用没有 headless / 非交互 CLI 批处理模式**。
>
> ARCH 文中出现的「headless」一词，实际指向 **Avalonia UI 测试基座**（`tests/.../HeadlessFramework.cs`、`Desktop.Tests/Helpers/DesktopHeadlessTestBase.cs`），是测试手段而非运行期契约。
>
> 因此「CLI 调用方式」在本工具语境下，指 **BatchAgentRunner 构造并拉起的外部 agent CLI**（CodeBuddy / WorkBuddy / Mock），其命令形态由 `CliCommandBuilder` 唯一拼装。

### 1.2 外部 agent 命令构造（唯一拼装点）

命令拼接的**唯一事实来源**为 `src/BatchAgentRunner.Agents/Internal/CliCommandBuilder.cs`，由 `SecurityHygieneTests`（ARCH P-12）锁死「唯一拼装点 + 引号/单行折叠形态 + 引号实现唯一 + 适配器不得裸替换占位符」。

**占位符集合**（被 `Build()` 替换，所有取值均经 `NormalizeToSingleLine(...)` + `Quote(...)` 处理，防命令注入）：

| 占位符          | 含义       |
| --------------- | ---------- |
| `{prompt}`      | 任务指令   |
| `{model}`       | 模型名     |
| `{expert}`      | 专家标识   |
| `{workspace}`   | 工作区路径 |
| `{extra}`       | 附加参数   |

**命令模板**（按 `agent_kind` 选择）：

- **默认模板**：`--print {prompt} --model {model} {extra}`
- **CodeBuddy 模板**：`-p {prompt} --model {model} --output-format stream-json --include-partial-messages {extra}`
- **WorkBuddy 模板**：`--print {prompt} --model {model} --output-format stream-json --include-partial-messages {extra}`（`-expert` 对 WorkBuddy 无效，专家降级为 prompt 注入）

**推理力度**（`reasoning_effort`）：`AppendEffort` 追加 `--effort <level>`，取值 `minimal / low / medium / high / xhigh / max`（别名映射到 `medium`；为空或已存在则省略）。

**窗口与输出**：`show_cli_window`（`config.json`）控制是否显示原生控制台窗口；不显示时捕获 stdout 并解析 `stream-json` 行。`auto_answer` 配置向子进程 stdin 写入应答。

> 安全红线：占位符替换一律走 `Quote(NormalizeToSingleLine(...))`；新增任何命令构造路径必须经 `CliCommandBuilder.Build`，不得出现裸 `{prompt}`/`{workspace}` 字符串替换（守卫见 `SecurityHygieneTests`）。

---

## 2. config.json 契约

### 2.1 权威来源与位置

- **机器可校验单一事实来源**：`docs/config.schema.json`（JSON Schema 2020-12），由 `RunOptions`（`RunOptions.01~11-*.cs`）及其引用领域类型逐字派生；键集合与代码 `[JsonPropertyName]` 逐字相等，由 `ConfigSchemaTests`（4 项）锁死、并被 `scripts/ci/Assert-ConfigSchema.ps1` 纳入 CI 阻断门禁。
- **落盘位置**：`%LOCALAPPDATA%\BatchAgentRunner\config.json`（见 `src/BatchAgentRunner.Infrastructure/AppPaths.cs`）。
- **兼容口径**：schema 所有对象 `additionalProperties: true`——旧版本 `config.json`（含已废弃字段）仍可校验通过；键集合漂移由等值断言拦截，而非 `additionalProperties:false` 误伤。

### 2.2 顶层字段（41 个，snake_case）

按 12 配置域分组（`RunOptions.01~11-*.cs`）：

| 配置域             | 关键字段（节选，全量以 schema 为准）                                                                                                              |
| ------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| 任务源（Execution） | `task_file_path`（必填）、`workspaces[]`、`default_workspace`、`max_workspaces`                                                                  |
| Agent/CLI          | `agent_kind`（WorkBuddy/CodeBuddy/Mock）、`cli_path`、`cli_arguments_template`、`reasoning_effort`、`show_cli_window`                            |
| 账号/模型          | `accounts[]`、`model_candidates[]`、`degradation_strategy`（ModelFirst/AccountFirst）、`expert`、`fallback`                                       |
| 重试/超时          | `max_retries_per_task`、`rate_limit_backoff_retries`、`rate_limit_initial_backoff_seconds`、`task_timeout_minutes`、`max_attempts_per_task`、`max_total_minutes_per_task`、`max_wait_minutes`、`wait_for_quota_reset`、`default_cooldown_minutes` |
| 并发/流程          | `concurrency`、`force_rerun`、`stop_on_failure`、`terminate_after_current_task`、`enable_session_resume`、`default_max_gate_loops`、`dependency_failure_policy`（BlockDependents/AbortBatch/SkipDependents） |
| 生命周期/自动化    | `auto_shutdown`、`credits_claim`、`auto_answer`、`notifications[]`                                                                               |
| Markdown/模板/Diff | `markdown_in_place_writeback`、`template_batch_expansion_limit`、`collect_diffs`、`diff_target_globs[]`                                          |
| VCS/保留           | `git`、`result_max_kb`、`backup_retention`                                                                                                       |

> 完整 41 字段及类型/枚举/边界/`pattern`（如时段 `HH:mm`）见 `docs/config.schema.json`。落盘字节顺序＝切片文件名升序（`RunOptionsContractTests` 顺序契约锁死），调整/新增切片须同步守卫。

### 2.3 向后兼容

- 新增字段 opt-in + 版本化迁移器；未知字段宽容解析；破坏性变更走主版本号。
- 凭证不入 `config.json`（由 Vault 处理），配置仅存非敏感项。

---

## 3. Sidecar 任务结果格式

### 3.1 命名与写入

- 由 `src/BatchAgentRunner.Infrastructure/TaskFiles/TaskFileStore.cs` 的 `ResolveResultPath` 决定：
  - Markdown 任务输入 `<name>.md` → 写入 sidecar `<name>.md.result.json`
  - JSON / XLSX 任务 → **原地写入**（同路径）
- `SaveAsync` 序列化 `List<TaskItem>`（经 `JsonDefaults`）；`MergeSidecar` 在断点续跑时重读该数组进行合并。

### 3.2 写入字段（JSON 键，来自 `TaskItem` `[JsonPropertyName]`）

`id`、`workspace`、`instruction`、`status`、`result`、`model_used`、`account_used`、`expert_used`、`fallback_used`、`fallback_cost`、`retry_count`、`error`、`started_at`、`finished_at`、`duration`，以及工作流/DAG 相关：`workflow`、`depends_on`、`project_ref`、`template_vars`、`max_loops`、`batch_size`、`stage_results`、`session`。部分输出以 `[部分输出]` 前缀标记（`TaskItem.cs`）。

**`StageResult`**（仅工作流任务，`WorkflowTypes.cs`）：`stage_id`、`name`、`status`、`output_summary`、`gate_passed`、`gate_leftover`、`loop_count`、`model_used`。

### 3.3 ⚠️ 已知缺口（如实声明）

> ARCH §2.5 / §7.2 曾表述「sidecar 写入带 `schema_version`，旧读取器能降级兼容」。经核实（`TaskFileStore.cs` / `TaskItem.cs`），**任务 sidecar 当前并不携带 `schema_version` 字段**——该字段只存在于 `CreditsClaim`（`CreditsClaim.cs:79`）与 `CredentialVault`（`CredentialVault.cs:46`），任务结果文件无版本标记。
>
> 这是一个**尚未实现的契约增强**，非刻意设计。在补齐 `schema_version` 之前，任务 sidecar 的「版本兼容」依赖字段的宽容解析（新增字段不影响旧读取器，删除字段旧读取器给默认值）。将其登记为后续项，待补时应同步补充「旧读取器降级读旧 / 新读取器读新」的契约测试（与 ADR-0007「文档不漂移」原则一致，不把 aspirational 描述当既成事实）。

---

## 4. BatchReport 报告结构

报告模型见 `src/BatchAgentRunner.Core/Domain/BatchReport.cs`，顶层 JSON 键：

`generated_at`、`batch_status`、`task_file`、`total`、`done`、`failed`、`interrupted`、`pending`、`needs_attention`、`blocked`、`skipped_already_done`、`started_at`、`finished_at`、`duration_seconds`、`active_duration_seconds`、`account_usage[]`、`model_usage[]`、`timeline[]`、`fallback`、`failed_tasks[]`、`window_wait_seconds`、`cooldown_wait_seconds`、`shutdown`、`diffs[]`、`tasks[]`

嵌套结构（精确 JSON 键）：

- `account_usage` / `model_usage` → `UsageEntry{ name, task_count, cost, cost_basis }`
- `timeline[]` → `TimelineEntry{ at, kind, message, task_id, account, model, deferred_switch }`
- `fallback` → `FallbackSummary{ used_count, model, total_cost, cost_basis, stopped_by_limit }`
- `failed_tasks[]` → `FailedTaskEntry{ task_id, error, retry_count, model, account }`
- `shutdown` → `ShutdownOutcome{ requested, executed, reason }`
- `tasks[]`（phase-2 明细）→ `TaskReportEntry{ task_id, status, depends_on, blocked_by, error, leftover, stage_results, template_id, duration_seconds, model_used, account_used }`

报告按批次时间命名，与 `metrics-<run_id>.json` 同目录；指标与报告刻意分离落盘（报告给用户看，指标给运维看，见 ADR-0008）。

---

## 5. 契约测试守护（回归红线）

| 契约                  | 守护测试                                                                     |
| --------------------- | ---------------------------------------------------------------------------- |
| config 键集合 / 形状   | `ConfigSchemaTests`（4 项）+ `RunOptionsContractTests`（5 项）+ `Assert-ConfigSchema.ps1`（CI 阻断） |
| 配置切片不得回缩       | `RunOptionsHygieneTests`（3 项）                                             |
| 编辑器泛型化 + 校验反馈 | `ConfigEditorsHygieneTests`（3 项）                                          |
| 命令注入 / 路径遍历 / 硬编码密钥 | `SecurityHygieneTests`（7 方法 / 11 用例）                               |
| 引擎行为 / 报告 / sidecar | `BatchEngineTests` 契约桶、`ReportWriter` 测试、`TaskFileStore` 续跑测试    |

任何破坏上述四类对外契约的改动，在 CI 单测桶内即红，不依赖人肉 review。
