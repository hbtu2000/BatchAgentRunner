# NexYawNav · 纽曜·领航（BatchAgentRunner）

> 纽曜 NexYaw 产品矩阵成员 · AI 智能层 · 批处理执行枢纽 · 子口号"枢纽驭批量，曜启领航新境"

AI 批处理任务调度工具：把一批任务清单交给 WorkBuddy / CodeBuddy 等 CLI 智能体，**无人值守**串行跑完，自动降级（账号 × 模型池）、免费时段感知、额度冷却跳过、断点续跑、汇总报告。C# / .NET 10，界面基于 Avalonia 12（跨平台桌面，Windows 优先）。

详细设计、配置项、用户手册见 [`docs/`](docs/) 目录。

---

## 快速开始

### 0. 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)（仓库已按 `net10.0` 配置，本机实测 10.0.401）
- Windows / macOS / Linux 均可运行（Windows 优先）
- 接入真实 CLI 时，需先在 PATH 中能直接运行 `workbuddy --version` 或 `codebuddy --version`（2026-10-15 实测两者同用 `codebuddy` 命令，来自 `@tencent-ai/codebuddy-code`）

### 1. 构建并启动

```bash
# 在仓库根目录
dotnet build BatchAgentRunner.slnx
dotnet run --project src/BatchAgentRunner.Desktop
```

启动即进入图形界面；首次使用推荐走左侧「配置向导（首次推荐）」。

### 2. 不装 CLI，先用 Mock 跑通全流程

Mock 适配器不调用任何 CLI，用来验证调度逻辑（降级、时段跳过、冷却等待、兜底、危险操作拦截），不验证 Agent 真实输出质量。

1. 配置页把 `agent_kind` 选 **Mock**；
2. `task_file_path` 选仓库内的 `samples/tasks.json`（示例任务清单）；
3. 切到监控页点「预检」→ 应显示「预检通过：N 条任务，待处理 N 条」；
4. 点「开始」→ 观察状态流转：执行中 → 已完成，任务文件里的 `status` 被回写成 `done`。

### 3. 接入真实 CLI

1. `agent_kind` 选 **WorkBuddy** / **CodeBuddy**；
2. 确认 PATH 里能直接 `codebuddy --version`，否则在 `cli_path` 填绝对路径；
3. 把各账号 `model_candidates[].name` 换成**该账号**真实可用的模型标识（默认的 `model-a`/`model-b` 是占位名，不同账号的池相互独立，分别填写）；
4. 把 `accounts[].credential_ref` 换成你的 CLI 登录 profile / 环境变量名（凭据只存引用，明文不落盘）；
5. 点「预检」——预检会逐个账号探测，探测失败的账号会被**自动停用**并给出警告。

### 4. 运行产物

- **运行日志**：任务文件同目录 `runs/run-<时间戳>.log`
- **汇总报告**：同目录 `report-<时间戳>.json` / `.md`
- **任务状态回写**：`.json` 原地原子回写；`.md` 只读输入、状态写 `<name>.md.result.json`
- **额度冷却记录**：`%LOCALAPPDATA%\BatchAgentRunner\cooldowns.json`（重启仍有效）

> 改了账号 / 模型 / 时段 / 降级策略后需让引擎重建（界面「保存配置」按钮已自动做），否则调度用的还是旧矩阵。

---

## 开发约定（技术债务治理阶段 −1 起生效）

本仓库按 [`docs/TECH-DEBT-技术债务治理方案-v2.0.md`](docs/TECH-DEBT-技术债务治理方案-v2.0.md) 治理。**当前进度：阶段 −1（B 组 5 项，2026-10-17）、阶段 0（A 组 10 项 + P0-17，2026-10-17）、阶段 1（建网 6 项，2026-10-18）、阶段 2（动骨，2026-10-18 ~ 10-20）、阶段 3（持续化补完，2026-10-20）均已完成；P0 清单 17 项全部清偿**。阶段 4（持续化，2026-10-20 ~ 10-21）已收口 P2 批次五项：P2-26（误导性注释清算 + 计划性延期台账 + `CommentHygieneTests`）、P2-27（评估维持主动不偿还）、P2-29（`_harness/` 产物堆清理）、P2-30（逃逸阀台账四要素补齐 + 未登记的生产代码豁免归位 + `EscapeValveHygieneTests`）、P2-31（三份 `Json*Store` 抽 `JsonStoreBase<T>` + 路径魔法串收口 `AppPaths` + `AppPathsHygieneTests`）、P2-9（`RunOptions` 按 12 配置域横向切片，JSON 契约零变化；原 #D5 计划性延期结项）。

同期 ARCH 架构重构阶段（[`docs/ARCH-架构重构与工程化治理方案.md`](docs/ARCH-架构重构与工程化治理方案.md)）已收口 P-01~P-11：P-01（`WaitCoordinator` 抽取）、P-02（`BatchEngine` facade 953→400 行 + 依赖方向 IL 断言）、P-03（`TaskRunner` 策略对象化）、P-04（`ConfigViewModel` 1065→366 行分解为 11 子编辑器）、P-05（`TemplateEditorViewModel` 674→240 行 + 3 Coordinator）、P-06（`MonitorViewModel` 983→388 行 + `LoginViewModel` 751→280 行）、P-07（`JsonStoreBase<T>` + `AppPaths` 去重）、P-08（领域原语单一事实来源 + 修 `TaskItem.Clone()` 浅拷贝缺陷）、P-09（配置契约收口：`RunOptions` 363 行 God Config 按 12 配置域切片 + 禁 JSON 往返克隆；`ConfigEditors` 解析字段全量 `FieldEditor<T>` + 修「非法输入静默抹掉已配置时段」缺陷 + 视图侧校验反馈统一）、P-10（可观测性：结构化日志接线——`IRunLogger` 唯一落盘点 `Write(RunLogEntry)` + `run-*.jsonl` 结构化流；`RunId` 贯穿事件/日志/指标；`RunMetrics` 七类运行级指标埋点 + `metrics-<run_id>.json` 落盘；`SecretMasker` 脱敏在渲染入口统一生效；`facade` 维持 399 行）、**P-11（CI 转阻断：四道门禁——全量 `--no-incremental` 构建 / `dotnet format --verify-no-changes`（`scripts/ci/Assert-Format.ps1`，CHARSET 假阳性按 SDK 10.0.401 已知问题过滤）/ 分层单测 / 覆盖率门禁（`scripts/ci/Assert-Coverage.ps1`，锁定基线 Core 44% / Infra 39% / Agents 23% / Desktop 50%）——任一步骤失败即 CI 红）**、**P-13（多平台矩阵 win/mac/linux 非阻断 + 架构测试独立桶 + Release 流水线：自包含单文件四 RID + GitVersion + SHA256 校验）**、**P-14（ADR 机制：`docs/adr/` 11 篇决策记录 + `AdrHygieneTests` 索引/结构守卫；配置 JSON Schema 单一事实来源 `docs/config.schema.json`，`ConfigSchemaTests` 锁死 schema 与 `RunOptions` 键集合并校验示例/畸形配置，`scripts/ci/Assert-ConfigSchema.ps1` 进 CI 门禁）**。

测试基线（2026-10-22 ARCH P-14 收口轮）：主测试项目 **662** 用例 + Desktop.Tests **168** 用例 = **830 通过 / 0 失败**（`dotnet test BatchAgentRunner.slnx`；较 P-11 轮 822 +8：`ConfigSchemaTests` 4 项 + `AdrHygieneTests` 4 项）。ARCH 进展：P-01~P-14 中除 P-12（规范集 + 静态安全扫描，进行中）外均已收口；机会主义偿还余项：P2-12 全量编译绑定翻转、`git filter-repo` 历史净化。

已落地的「防新增」机制：

- ~~**提交信息门禁**：`.githooks/commit-msg` 拒绝空消息 / 纯标点 / 缺「改了什么·为什么」正文的提交；模板见 `.gitmessage.txt`。~~ ⚠️ **已于 2026-10-21 按仓库所有者明确要求永久停用**（`.githooks/commit-msg` 现为 no-op 存根，禁止恢复）；提交信息规范改由 CODE-REVIEW §5.2 人工把关，模板 `.gitmessage.txt` 仅作参考。
- **编译期门禁**：`EnforceCodeStyleInBuild` + `AnalysisLevel=latest-recommended` + **`TreatWarningsAsErrors=true`**（阶段 1 / P0-12：存量警告清零后开启，新增任何分析器警告即构建失败）。豁免走治理方案 §4.7 逃逸阀台账（四要素：发现日 / 责任人 / 到期日 / 不变量断言），并须在代码就近注释写明「逃逸阀台账 #N / 到期日 / 责任人」——由 `EscapeValveHygieneTests` 机器校验（P2-30，2026-10-21）。
- **CPM**：`Directory.Packages.props` 集中全部包版本；任何 csproj 再写内联 `Version=` 即 NU1008 构建错误。
- **CI 门禁（五道，失败即红）**（ARCH P-11 + P-14，2026-10-22）：`.github/workflows/ci.yml` 在 `windows-latest` 上依次执行 ① 全量 `dotnet build --no-incremental`（`TreatWarningsAsErrors` 下任何分析器警告即失败；必须 `--no-incremental`，否则增量构建会掩盖真实警告——P0-12）② 格式门禁 `scripts/ci/Assert-Format.ps1`（`dotnet format --verify-no-changes`，过滤 SDK 10.0.401 的 CHARSET 假阳性）③ 分层单测（快跑单元 + Desktop，带 `coverlet` 覆盖率采集；慢跑 E2E·Usability·Process）④ 配置 Schema 门禁 `scripts/ci/Assert-ConfigSchema.ps1`（`docs/config.schema.json` 键集合须与 `RunOptions` 逐字一致、示例配置合法、畸形配置被拦截）⑤ 覆盖率门禁 `scripts/ci/Assert-Coverage.ps1`（生产程序集行覆盖率 ≥ 锁定基线 Core 44% / Infra 39% / Agents 23% / Desktop 50%，阈值只升不降）。本地复跑：`powershell -ExecutionPolicy Bypass -File scripts/ci/Assert-Format.ps1`、`pwsh -File scripts/ci/Assert-ConfigSchema.ps1` 与 `dotnet test <工程> --settings tests/coverlet.runsettings` + `Assert-Coverage.ps1`。
- **required status check（待仓库管理员配置）**：分支保护属 GitHub 仓库管理设置（非仓库文件）。Settings → Branches → 编辑/新增 `master` 规则 → 勾选 **Require status checks to pass before merging** → 搜索并勾选 **`build-and-test`**（建议同时勾选 Require branches to be up to date）。勾选后红 CI 即阻止 PR 合并（ARCH R-3 缓解闭环）。
- **`.gitignore` 目录级规则**：`.workbuddy/`、`/.playwright-cli/`、`/TaskList/`、`*.backup-*`、`/models.yaml`、`/.tmp_reflect.ps1` 等运行产物/隐私不再入库。
- **路径常量收口**（P2-31，2026-10-21）：应用本地数据目录一律走 `Infrastructure/AppPaths`（`Root` / `ConfigDirectory` / `TemplatesDirectory` / `VaultDirectory` / `RunsDirectory` / `LogsDirectory` / `MockLiveDirectory`），`src` 内不得再内联应用名路径魔法串；新增数据目录先在 `AppPaths` 登记。由 `AppPathsHygieneTests` 机器校验。
- **单文件 JSON 仓储**（P2-31，2026-10-21）：新增 `Json*Store` 须继承 `Infrastructure/Persistence/JsonStoreBase<T>`，只实现 `CreateEmpty()`；加载后的自愈/迁移走 `NormalizeAsync()` 钩子，不得自带「建目录 / `JsonSerializer` / `AtomicFileWriter`」样板。
- **测试约定**：E2E / Usability / Process 三类打 `[Trait("Category", …)]`，CI 分快慢跑；测试方法省略 `ConfigureAwait`（xUnit 建议）；高危路径（凭证/进程/Git/安全拦截/二进制写回）改动须带测试。

---

## 相关文档

- [`docs/PRD-BatchAgentRunner-v2.0.md`](docs/PRD-BatchAgentRunner-v2.0.md) — 产品需求
- [`docs/DESIGN-BatchAgentRunner-v2.0.md`](docs/DESIGN-BatchAgentRunner-v2.0.md) — 技术设计
- [`docs/CONFIG-配置项说明.md`](docs/CONFIG-配置项说明.md) — 全配置项速查与常见坑
- [`docs/config.schema.json`](docs/config.schema.json) — 配置 JSON Schema（机器可校验的单一事实来源，CI 门禁校验）
- [`docs/adr/`](docs/adr/README.md) — 架构决策记录（ADR）：重大决策的背景、取舍与后果
- [`docs/用户手册-BatchAgentRunner-v1.0.md`](docs/用户手册-BatchAgentRunner-v1.0.md) — 使用手册
- [`samples/config.example.json`](samples/config.example.json) — 全字段带注释配置模板
