# QA 报告 — BatchAgentRunner 端到端测试

- 报告日期：2026-10-15
- 测试层级：管线级端到端（E2E）
- 被测版本：工作区 `E:\Src\CodeBak\WorkAgent`（M2 阶段，P0+P1 交付范围）
- 测试人：Horizon（AI 自主执行）

## 1. 结论（先说门禁）

**质量门禁：✅ 全绿，本批次可放行。**

| 门禁项 | 目标 | 实测 | 判定 |
|--------|------|------|------|
| 测试执行率 | 100% | 8/8（100%） | ✅ |
| 通过率 | ≥80% | 8/8（100%） | ✅ |
| P0 缺陷 | 0 | 0 | ✅ |
| P1 缺陷 | ≤5 | 0 | ✅ |
| 既有单测回归 | 全绿 | 57/57 | ✅ |

## 2. 执行环境与方法

- **前置基线**：`dotnet build BatchAgentRunner.slnx` 0 error；既有单测 57/57 通过后才启动 E2E。
- **真实链路**：真实 `TaskFileStore`（JSON 原子回写 / Markdown sidecar）、`BackupManager`、`JsonCooldownStore`、`FileRunLogger`、`ReportWriter` + 真实 `BatchEngine` 调度，全部文件写入测试专属临时目录。
- **仿真边界**：适配器用 `MockAdapter`（真实 CLI 依赖 M0 技术验证 V-1~V-10，属已知范围外项）；时钟用 `FakeClock` 精确控制时段/冷却恢复点。
- **用例来源**：`tests/docs/01-E2E-TEST-CASES.md`（AAA 格式，唯一真实来源）；执行状态见 `TEST-EXECUTION-TRACKING.csv`。

## 3. 执行结果明细

| 编号 | 场景 | 优先级 | 结果 | 关键证据 |
|------|------|--------|------|----------|
| TC-E2E-001 | JSON 全链路：执行→回写→备份→报告→日志 | P0 | ✅ | `tasks.json` 回写 done/m1/账号 1；备份保留 pending；report-*.md/json 落盘；run-*.log 含 task_finished |
| TC-E2E-002 | Markdown sidecar + 重启续跑 | P0 | ✅ | sidecar 状态 done/failed/done；md 本体零改动；第二轮 **0 次调用**、跳过 3 条 |
| TC-E2E-003 | 冷却落盘跨重启 | P0 | ✅ | m1 全程仅 1 次调用；cooldowns.json 落盘；重启加载事件「已加载 1 条额度冷却记录」 |
| TC-E2E-004 | 时段外等待→自动恢复 | P1 | ✅ | BatchWaiting → BatchResumed → Completed；免费时段外累计等待 > 0 |
| TC-E2E-005 | 兜底模型收尾 | P1 | ✅ | fallback_used=true、model_used=fb；报告兜底成本 ¥0.5；冷却先于兜底落盘 |
| TC-E2E-006 | 危险操作拦截 | P0 | ✅ | 危险任务 failed 且错误含「危险操作」；批继续完成；日志含 `*** 高危 ***` |
| TC-E2E-007 | 预检拦截 | P1 | ✅ | 任务文件缺失/工作空间目录不存在均给出清晰错误，未抛异常 |
| TC-E2E-008 | 配置持久化全链路 | P1 | ✅ | 注释+尾随逗号模板可解析；落盘无派生属性键；重载往返无损 |

## 4. 缺陷清单

**产品缺陷：0 个（P0–P4 均无）。**

测试脚本调试期间发现并当场修正的 4 个用例问题（非产品缺陷，记录供复现）：

| # | 问题 | 定性 | 处理 |
|---|------|------|------|
| 1 | 备份内容断言大小写口径错（枚举序列化为 `Pending`） | 用例脚本 | 改大小写不敏感匹配 |
| 2 | `MockAdapter.CallLog` 格式为 `账号+m1 attempt1`，`" m1 "` 匹配不到 | 用例脚本 | 改 `"+m1 "` 匹配 |
| 3 | 兜底用例 planner 对兜底模型也返回额度耗尽，导致兜底失败→任务中断→批 Blocked | 用例脚本（顺带确认了引擎「兜底失败不循环兜底」语义符合设计） | planner 按模型区分 |
| 4 | `FileRunLogger` 持有写句柄期间读日志抛 IOException | 用例脚本 | 读取前先 Dispose（AutoFlush 已保证落盘） |

## 5. 观察与建议（非阻塞）

1. **P4 · 测试工程告警**：`TaskFileStoreTests.cs` 存在 10 处 xUnit1031 阻塞等待告警（`.Wait()`/`.Result`），建议后续改 async 测试方法。全解决方案构建本身仍为 0 warning（告警仅限测试分析器）。
2. **语义确认**：「兜底失败 → 任务转中断 → 批 Blocked（等待恢复）」的链路行为与 PRD 边缘情况 10 的设计意图一致，建议在 `docs/CONFIG-配置项说明.md` 兜底章节补充一句说明，避免用户误判为缺陷。
3. **范围外项**（放行不含背书）：真实 WorkBuddy/CodeBuddy CLI 调用（M0 V-1~V-10 未完成前无法背书参数模板与额度错误特征）；Avalonia UI 交互；自动关机。
4. **待校准默认值**：单任务超时 10min / 总时长上限 60min / 最长可等待 60min / 默认冷却 60min 仍为假设值，真实任务跑批后需回归校准。

## 6. 交付物清单

- 用例文档（AAA）：`tests/docs/01-E2E-TEST-CASES.md`
- 执行追踪表：`tests/docs/TEST-EXECUTION-TRACKING.csv`
- 可执行 E2E 套件：`tests/BatchAgentRunner.Tests/E2E/EndToEndPipelineTests.cs`（8 用例，随解决方案 `dotnet test` 一键回归）
