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

## 相关文档

- [`docs/PRD-AI批处理任务调度工具-v1.0.md`](docs/PRD-AI批处理任务调度工具-v1.0.md) — 产品需求
- [`docs/DESIGN-BatchAgentRunner-v1.0.md`](docs/DESIGN-BatchAgentRunner-v1.0.md) — 技术设计
- [`docs/CONFIG-配置项说明.md`](docs/CONFIG-配置项说明.md) — 全配置项速查与常见坑
- [`docs/用户手册-BatchAgentRunner-v1.0.md`](docs/用户手册-BatchAgentRunner-v1.0.md) — 使用手册
- [`samples/config.example.json`](samples/config.example.json) — 全字段带注释配置模板
