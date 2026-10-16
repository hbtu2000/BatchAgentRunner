# DESIGN-额度耗尽暂停续跑与暂停时长剔除-v1.0

> 背景：批处理在运行中可能遇到「所有启用账号 × 所有候选模型」的额度同时耗尽（CLI 报 quota / 余额不足，被 `QuotaErrorClassifier` 归一为 `QuotaExhausted` 并写入 `QuotaCooldownRecord`）。
> 现状引擎在「全部组合不可用」时会进入 waiting，但受 `max_wait_minutes`（默认 60）上限约束——若最早恢复时间超出该上限，则直接把任务判 `Interrupted`、整批转 `Blocked`，不会自动续跑。
> 本设计在**额度耗尽**这一确定会恢复的场景下放开等待上限，并让「暂停等待」的时长不计入批处理与各任务的总时长。

---

## 1. 需求（Requirements）

- **R1 暂停续跑**：当所有启用账号的所有候选模型组合**因额度耗尽**（`WaitKind == QuotaReset`）而同时不可用时，引擎进入 `Waiting` 暂停，等待最早恢复的组合额度重置后，**自动恢复并继续执行**剩余任务；不转为 `Blocked`。
- **R2 暂停时长剔除**：暂停/等待期间消耗的真实时间**不计入**对外报告的「时长」。批级与单任务级时长均为「净执行时长 = 墙钟时长 − 等待时长」。
- **R3 作用域限制**：无限等待只针对**纯额度耗尽**（`QuotaReset`）。纯免费时段外（`FreeWindow`）仍受 `max_wait_minutes` 约束（避免跨天/跨周末无限挂起）；时段+额度混合（`Mixed`）维持上限约束（时段可能主导恢复点）。
- **R4 可随时中止**：暂停期间用户仍可通过「取消等待」（`CancelWaiting` → `Blocked`）或「终止」（`TerminateAsync`）退出，行为不变。
- **R5 可配置开关**：新增 `wait_for_quota_reset`（默认 `true`）。关闭后退回现状（按 `max_wait_minutes` 判定阻塞）。
- **R6 可续跑（重启安全）**：`QuotaCooldownRecord` 已落盘并在 `InitializeAsync` 重新加载，暂停后若进程退出/重启，再次启动会重新评估可用性并在额度恢复后自动继续。

---

## 2. 设计（Design）

### 2.1 配置项（RunOptions）
新增字段：
```
wait_for_quota_reset  : bool  (默认 true)   // 额度耗尽时无限等待重置
```
派生属性 `WaitForQuotaReset`（与 `MaxWait` 等同类）。

### 2.2 引擎判定（BatchEngine）
- 新增私有判定 `IsQuotaUnboundedWait(RestoreCandidate? c)`：
  `_options.WaitForQuotaReset && c is { WaitKind: WaitKind.QuotaReset }`。
- **单任务重试循环**（`ExecuteTaskAsync`）：原 `canWait = earliest is not null && earliest.RestoreAt <= now + MaxWait`
  改为 `canWait = earliest is not null && (IsQuotaUnboundedWait(earliest) || earliest.RestoreAt <= now + MaxWait)`。
- **启动前预派发**（`StartAsync`）：原 `if (waitIfUnavailable && earliest is not null)`
  改为 `if (earliest is not null && (waitIfUnavailable || IsQuotaUnboundedWait(earliest)))`（额度耗尽即便未勾选「等待自动启动」也自动挂起等待，因其必定恢复）。
- `WaitForRestoreAsync` 增加 `string? taskId` 参数：累计等待时长除计入 `_windowWaitSeconds` / `_cooldownWaitSeconds`（批级）外，若 `taskId` 非空，额外累加进 `_taskWaitSeconds[taskId]`（单任务级）。
- 调用点：
  - `ExecuteTaskAsync` 内 `WaitForRestoreAsync(earliest!, ...)` → 传入 `task.Id`。
  - `StartAsync` 内调用 → 传 `null`（不归属任何任务）。

### 2.3 暂停时长剔除
- 终止方法（`CompleteTask` / `FailTask` / `MarkInterrupted`）在 `task.Finish(status, now)` 后调用 `ApplyPausedExclusion(task)`：
  若 `_taskWaitSeconds[task.Id]` 有值，从 `task.DurationSeconds` 中减去并夹紧到 ≥ 0。
- `BuildReport` 新增 `ActiveDurationSeconds = max(0, DurationSeconds − WindowWaitSeconds − CooldownWaitSeconds)`，
  原 `DurationSeconds` 保留为墙钟总时长（含等待），便于对照。

### 2.4 事件/日志文案
`WaitForRestoreAsync` 的 `DescribeWaiting` 在 `QuotaReset` 分支文案补充「…等待额度重置（暂停不计入时长），可取消」。

### 2.5 报告与界面
- `BatchReport` 新增 `active_duration_seconds`。
- `ReportViewModel.DurationText`：主显示净执行时长，括号附注墙钟总时长，例如 `净执行 12.3 秒（总耗时含等待 5402.0 秒）`。
- 单任务行 `TaskRowViewModel.DurationText` 已读 `Item.DurationSeconds`，因 2.3 已剔除本任务等待，自动正确。
- `ConfigView`：在「⑦ 并发·重试·超时·单任务上限」区新增复选框「额度耗尽时无限等待重置（暂停不计入时长）」绑定 `WaitForQuotaReset`；`ConfigViewModel` 增加对应属性并在 `Reload` / `SaveAsync` 中读写。

### 2.6 边界与并发
- `_waitGate` 已保证同时仅一个等待在进行；并发模式（`concurrency > 1`）下每任务等待也按 `taskId` 独立累计，互不串扰。
- `MockAdapter` 与 `FakeClock` 已支持推进时钟，测试可直接验证越过 `max_wait` 的额度等待。

---

## 3. 测试计划（覆盖点）
1. 全部组合额度耗尽且恢复时间 > `max_wait` → 仍自动等待重置后完成（证明 R1/R3 放上限）。
2. 上述场景：`report.ActiveDurationSeconds` 不含等待、单任务 `DurationSeconds` 不含本任务等待（R2）。
3. 关闭 `wait_for_quota_reset` 且恢复时间 > `max_wait` → 维持 `Blocked`（证明 R5 开关有效）。
4. 纯免费时段外（无额度）超出 `max_wait` → 仍 `Blocked`（证明 R3 不误用无限等待）。
5. 暂停期间 `CancelWaiting` → `Blocked`（R4）。

---

## 4. 影响面
- 仅新增配置项与一处等待上限判定 + 时长计算；不改动降级矩阵、冷却登记、兜底、凭证切换既有契约。
- 现有 `全部组合冷却中_按最早恢复时间等待后继续执行` 等测试不受影响（`max_wait` 内仍走原路径）。
