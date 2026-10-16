# 端到端（E2E）测试用例 — BatchAgentRunner

- 版本：v1.0（2026-10-15）
- 测试层级：**管线级端到端**。真实链路 = 配置/任务文件落盘 → 预检 → 调度（时段/冷却/降级矩阵/兜底）→ 适配器执行 → 原子回写 → 冷却落盘 → 报告/运行日志。
- 执行环境：真实 `TaskFileStore` / `JsonCooldownStore` / `FileRunLogger` / `ReportWriter` / `BackupManager`，文件写入真实临时目录。
- 适配器：`MockAdapter`（真实 CLI 接入依赖 M0 V-1~V-10 技术验证，属已知范围外项）。
- 时钟：`FakeClock`（精确控制时段与冷却恢复时间）。
- 唯一真实来源：本文件为用例权威来源；`TEST-EXECUTION-TRACKING.csv` 仅记录执行状态。

## 覆盖范围与用例清单

| 编号         | 名称                                 | 优先级 | 覆盖需求                |
| ---------- | ---------------------------------- | --- | ------------------- |
| TC-E2E-001 | JSON 任务文件全链路：执行→原子回写→备份→报告→运行日志    | P0  | REQ-001/006/011/014 |
| TC-E2E-002 | Markdown 任务文件：sidecar 回写 + 重启续跑不重跑 | P0  | REQ-001a/008        |
| TC-E2E-003 | 额度耗尽→切换→冷却落盘→重启后冷却仍生效              | P0  | REQ-004c/005        |
| TC-E2E-004 | 全组合时段外→启动等待→到点自动恢复完成               | P1  | REQ-003b/010        |
| TC-E2E-005 | 全组合额度耗尽→兜底模型收尾→成本入报告               | P1  | REQ-004b            |
| TC-E2E-006 | 危险操作拦截→任务失败→批继续→日志高危标记             | P0  | REQ-002a/010b       |
| TC-E2E-007 | 预检拦截：任务文件缺失 / 工作空间目录不存在            | P1  | REQ-010/003a        |
| TC-E2E-008 | 配置全链路：注释模板加载→保存→重载一致→派生属性不入盘       | P1  | REQ-009 配置域         |

---

## TC-E2E-001 JSON 任务文件全链路

- **优先级**：P0
- **前置条件（Arrange）**：临时目录；真实 `tasks.json`（2 条 pending）；`MockAdapter` 全部成功；真实 `TaskFileStore`/`FileRunLogger`；单账号 acc1、单模型 m1。
- **执行（Act）**：`StartAsync()` → `ReportWriter.WriteAsync()`。
- **断言（Assert）**：
  1. 批状态 `Completed`，成功 2；
  2. `tasks.json` 原地回写：`status=done`、`model_used=m1`、`account_used=账号 1`；
  3. 启动前备份文件 `tasks.json.backup-*` 存在且内容仍为 `pending`；
  4. `report-*.md` 与 `report-*.json` 生成且 MD 含「批处理汇总报告」；
  5. `runs/run-*.log` 存在且含 `task_finished`。

## TC-E2E-002 Markdown sidecar 回写与重启续跑

- **优先级**：P0
- **前置条件**：真实 `tasks.md`（3 条）；第一轮适配器：task-001 成功、task-002 普通失败（重试 0 次）、task-003 成功。
- **执行**：第一轮 `StartAsync()` → 读取 sidecar 校验 → 全新引擎实例（模拟重启）第二轮 `StartAsync()`。
- **断言**：
  1. sidecar `<name>.md.result.json` 生成，状态 done/failed/done；
  2. `tasks.md` 本体未被修改（只读输入）；
  3. 第二轮零适配器调用（`TotalCalls==0`），`SkippedAlreadyDone==3`；
  4. 批状态 `Completed`，失败计数保留 1。

## TC-E2E-003 冷却跨重启持久化

- **优先级**：P0
- **前置条件**：单账号、模型 m1/m2；适配器：m1 恒额度耗尽（60 分钟后重置），m2 成功；共享冷却目录。
- **执行**：第一轮跑 2 条任务 → 任务文件替换为 1 条全新 pending 任务 → 时钟 +1 分钟 → 全新引擎（同冷却目录）第二轮。
- **断言**：
  1. 两轮任务全部经 m2 完成，`model_used=m2`；
  2. m1 全程仅被调用 1 次（第一轮第 1 条任务），冷却期内零无效调用；
  3. `cooldowns.json` 落盘且含 m1 记录；
  4. 第二轮初始化事件含「已加载 1 条额度冷却记录」。

## TC-E2E-004 时段外启动等待与自动恢复

- **优先级**：P1
- **前置条件**：单模型 m1 免费时段 22:00→次日 06:00；当前时刻周四 12:00（时段外）。
- **执行**：`StartAsync(waitIfUnavailable: true)`。
- **断言**：
  1. 事件序列包含 `BatchWaiting` 与 `BatchResumed`；
  2. 恢复后任务全部完成，批状态 `Completed`；
  3. 报告「免费时段外累计等待」> 0。

## TC-E2E-005 兜底模型收尾

- **优先级**：P1
- **前置条件**：单模型 m1 恒额度耗尽；兜底启用（模型 fb，估算单价 ¥0.5，单任务/单批上限充足）。
- **执行**：`StartAsync()`（任务第 1 次尝试已投入执行）。
- **断言**：
  1. 任务完成且 `fallback_used=true`、`model_used=fb`；
  2. 报告兜底 `UsedCount==1`、总花费 ¥0.5；
  3. 额度冷却记录已落盘（兜底前已记录 m1 重置时间）。

## TC-E2E-006 危险操作拦截

- **优先级**：P0
- **前置条件**：3 条任务；适配器：task-002 返回危险操作确认（`rm -rf /`），其余成功。
- **执行**：`StartAsync()`。
- **断言**：
  1. task-002 状态 failed、错误信息含「危险操作」；task-001/003 正常完成；
  2. 事件含 `DangerousOperationBlocked`，批状态 `Completed`（批不中断）；
  3. 运行日志含 `*** 高危 ***` 标记。

## TC-E2E-007 预检拦截

- **优先级**：P1
- **前置条件**：① 任务文件路径不存在；② 任务文件有效但引用的工作空间目录不存在。
- **执行**：分别调用 `PreflightAsync()`。
- **断言**：
  1. ①错误清单含「任务文件不存在」，不抛异常，`LoadedTaskCount==0`；
  2. ②错误清单含「工作空间」目录不存在提示。

## TC-E2E-008 配置持久化全链路

- **优先级**：P1
- **前置条件**：仓库 `samples/config.example.json`（含 `//` 注释与尾随逗号）复制为临时目录 `config.json`。
- **执行**：`JsonConfigStore.LoadAsync()` → `SaveAsync()` → 读取落盘原文 → 再次 `LoadAsync()`。
- **断言**：
  1. 首次加载：账号池 ≥2、候选模型含 model-a/model-b、免费时段跨零点字段正确；
  2. 落盘原文为合法 JSON（无注释），不含派生属性键 `task_timeout`/`max_task_duration`/`max_wait`/`default_cooldown`；
  3. 重载后账号数、模型名、降级策略与首载一致（往返无损）。

---

## 范围外项（记录在案）

- 真实 WorkBuddy/CodeBuddy CLI 调用（依赖 M0 技术验证 V-1~V-10）；
- Avalonia 桌面 UI 交互（无头环境不适用，以引擎事件驱动逻辑覆盖）；
- 自动关机（`ShutdownService`，涉及真实系统指令，不纳入 E2E）。
