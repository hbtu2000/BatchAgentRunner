# BatchAgentRunner

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

本仓库按 [`docs/TECH-DEBT-技术债务治理方案-v2.0.md`](docs/TECH-DEBT-技术债务治理方案-v2.0.md) 治理。**当前进度：阶段 −1（B 组 5 项，2026-10-17）、阶段 0（A 组 10 项 + P0-17，2026-10-17）、阶段 1（建网 6 项，2026-10-18）均已完成；P0 清单 17 项全部清偿**。全量测试 `dotnet test BatchAgentRunner.slnx` 当前 **345 通过 / 0 失败**（含 `tests/BatchAgentRunner.Desktop.Tests`）。下一站：阶段 2 动骨（P1-3 提接口 → P1-4/5 DI → P1-1 拆分 `BatchEngine`）。

已落地的「防新增」机制：

- **提交信息门禁**：`.githooks/commit-msg` 拒绝空消息 / 纯标点 / 缺「改了什么·为什么」正文的提交；模板见 `.gitmessage.txt`。
  新 clone 需先启用钩子（本机已配置，其他环境执行一次）：
  ```bash
  git config core.hooksPath .githooks
  git config commit.template .gitmessage.txt
  ```
- **编译期门禁**：`EnforceCodeStyleInBuild` + `AnalysisLevel=latest-recommended` + **`TreatWarningsAsErrors=true`**（阶段 1 / P0-12：存量警告清零后开启，新增任何分析器警告即构建失败）。豁免走治理方案 §4.7 逃逸阀台账（含理由/到期/责任人）。
- **CPM**：`Directory.Packages.props` 集中全部包版本；任何 csproj 再写内联 `Version=` 即 NU1008 构建错误。
- **CI（build 失败即红，合并仍不阻断）**：`.github/workflows/ci.yml` 在 `windows-latest` 上 `dotnet build --no-incremental` + 分层测试（快跑单元 / 慢跑 E2E·Usability·Process）。必须用 `--no-incremental` 全量重建，否则增量构建会掩盖真实警告（P0-12）。分支保护（必需状态检查）尚未配置，需仓库管理员在 GitHub 设置。
- **`.gitignore` 目录级规则**：`.workbuddy/`、`/.playwright-cli/`、`/TaskList/`、`*.backup-*`、`/models.yaml`、`/.tmp_reflect.ps1` 等运行产物/隐私不再入库。
- **测试约定**：E2E / Usability / Process 三类打 `[Trait("Category", …)]`，CI 分快慢跑；测试方法省略 `ConfigureAwait`（xUnit 建议）；高危路径（凭证/进程/Git/安全拦截/二进制写回）改动须带测试。

---

## 相关文档

- [`docs/PRD-BatchAgentRunner-v2.0.md`](docs/PRD-BatchAgentRunner-v2.0.md) — 产品需求
- [`docs/DESIGN-BatchAgentRunner-v2.0.md`](docs/DESIGN-BatchAgentRunner-v2.0.md) — 技术设计
- [`docs/CONFIG-配置项说明.md`](docs/CONFIG-配置项说明.md) — 全配置项速查与常见坑
- [`docs/用户手册-BatchAgentRunner-v1.0.md`](docs/用户手册-BatchAgentRunner-v1.0.md) — 使用手册
- [`samples/config.example.json`](samples/config.example.json) — 全字段带注释配置模板
