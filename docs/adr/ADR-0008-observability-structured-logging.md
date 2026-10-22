# ADR-0008：可观测性——单一日志落盘点 + RunId + 运行级指标 + 脱敏

- 状态：已接受（Accepted）
- 日期：2026-10-21
- 决策者：架构组
- 关联：ARCH P-10 / A-8；`Core/Observability/*`、`Infrastructure/Logging/FileRunLogger.cs`

## 背景

P-10 之前：日志只有自由文本，无运行标识、无结构化通道；降级次数/兜底触发/冷却跳过/
误切换率等**运行级指标完全没有采集**；「凭证不落日志」只靠写入点自觉，没有统一脱敏。

## 决策

1. **唯一落盘点**：`IRunLogger.Write(RunLogEntry)` 是唯一写入通道，既有十余个 `Write*` 便捷方法
   一律转条目后经此渲染——新增日志点无法绕过结构化。
2. **双轨产物**：`run-*.log`（人读 tail，正文逐字不变）+ `run-*.jsonl`（机器采集，
   含 `ts/level/event/run_id/task_id/account/model/message/fields`）。
3. **RunId 贯穿**：`RunLogEntry.NewRunId()` 生成 `run-{时间戳}-{6位随机}`，引擎事件
   `BatchEvent.RunId` 统一填充；事件流 / 日志 / 指标按同一次运行可聚合。
4. **运行级指标**：`RunMetrics` 在状态机决策点埋点七类（吞吐、平均耗时、降级次数、兜底触发、
   冷却跳过、时段跳过、失败率、误切换率），批次终态输出 `metrics_snapshot`，并由
   `RunMetricsWriter` 落 `metrics-<run_id>.json`（snake_case）。
5. **脱敏单点**：`SecretMasker`（键名形态 / Bearer / JWT / `sk-`/`ghp_`/`AKIA` 前缀）
   在渲染入口统一生效；凭证绝不落日志。
6. **四类事实各自单一来源**：`SecretMasker` / `RunLogLevels` / `LogEventNames` / `RunMetrics`，
   由 `ObservabilityHygieneTests` 锁死，禁止「再写一份」。

## 后果

**正面**

- 日志可机器采集、可按 RunId 关联；指标为运维提供批次级量化视图（与 `BatchReport` 面向用户互补）。
- 脱敏在唯一入口生效，新增日志点不可能泄漏凭证。
- 指标口径（失败率/误切换率/吞吐）由测试锁死，避免各写一套。

**负面 / 代价**

- 每个日志点需构造条目，写入路径略有成本；日志级别/事件名必须引用常量（守卫强制）。
- JSONL 与文本双写带来少量磁盘开销；文件体积随运行时长增长（沿用既有轮转策略）。

## 替代方案

| 方案 | 为什么不选 |
| --- | --- |
| 引入 Serilog/NLog 等日志框架 | 需要额外依赖与配置；本项目已有 `IRunLogger` 抽象，扩展成本更低 |
| 只写 JSONL，不要人读文本 | 排障时 tail 可读性下降；双轨成本可接受 |
| 指标仅从日志离线聚合 | 无法保证口径一致（每个分析脚本各算一套）；且终态快照难以稳定产出 |
| 在各写入点分别脱敏 | 必然漏点；P-10 前的问题正是「靠自觉」 |

## 关联

- `src/BatchAgentRunner.Core/Observability/*`
- `src/BatchAgentRunner.Infrastructure/Logging/FileRunLogger.cs`
- `ObservabilityHygieneTests` / `SecretMaskerTests` / `RunMetricsTests` / `RunMetricsInstrumentationTests`
