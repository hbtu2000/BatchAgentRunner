# ADR-0009：CI 门禁转阻断 + 覆盖率/格式基线

- 状态：已接受（Accepted）
- 日期：2026-10-22
- 决策者：架构组 + 仓库管理员
- 关联：ARCH P-11 / A-9①；`.github/workflows/ci.yml`、`scripts/ci/Assert-Format.ps1`、`scripts/ci/Assert-Coverage.ps1`

## 背景

CI 曾「只报告不阻断」：构建/测试失败只显示红叉，仍可合并；没有格式门禁，格式化漂移靠人；
没有覆盖率门禁，覆盖率只降不升。同时 `build` 若不加 `--no-incremental`，增量构建不重跑分析器，
会长期显示 0 警告，制造「债务已清」的假象。

## 决策

1. **阻断语义**：`build`（全量 `--no-incremental`）+ 分层单测 + 格式门禁 + 覆盖率门禁
   同属 job `build-and-test`，任一步失败即 job 失败；该 job 配为 master 分支保护的 required status check
   后，红 CI 阻止合并。
2. **测试分层执行**：快跑（单元 + Desktop headless）默认 bucket 并采覆盖率；
   慢跑（E2E / Usability / Process）单独跑、不采覆盖率，保证门禁可重复、不受慢机器影响。
3. **格式门禁**：`Assert-Format.ps1` = `dotnet format --verify-no-changes`，
   按脚本头过滤 SDK 10.0.401 的 CHARSET 假阳性，真实格式漂移即失败。
4. **覆盖率门禁**：`Assert-Coverage.ps1` 解析 cobertura，按程序集断言行覆盖率 ≥ 锁定基线
   （P-11 收口实测取整：Core 44% / Infrastructure 39% / Agents 23% / Desktop 50%）；
   阈值随覆盖率提升**在脚本内显式上调**，不做自动水位。
5. 长期目标（85/70/50）作为爬升方向，**刻意不硬门禁**——否则 CI 常态红、失去信号价值。

## 后果

**正面**

- 债务不会静默回潮：格式、覆盖率、构建、测试任一退化都在合并前拦截。
- 门禁脚本可本地运行，与 CI 行为一致（可复现）。

**负面 / 代价**

- 全量 `--no-incremental` 构建耗时较长（这是保证分析器真实效果的代价）。
- 覆盖率基线偏低（Agents 23%）是诚实现状，可能给人「门槛很低」的错觉；
  需靠阶段收口持续推进，而非一次性定高导致 CI 常红。
- required status check 属仓库管理设置，需管理员在分支保护中勾选，代码侧无法自证。

## 替代方案

| 方案 | 为什么不选 |
| --- | --- |
| 保持「只报告不阻断」 | 红叉无约束力，债务照常入库 |
| 一次性把覆盖率阈值定到 85/70/50 | 当前实际覆盖率远低于此，CI 会长期全红，门禁失效 |
| 用第三方覆盖率服务（Codecov 等）做门禁 | 外部依赖与配额；本地脚本已能满足按程序集断言 |
| 增量构建 + 单独跑分析器 | 仍需一次全量分析，且增量缓存会让结果与 CI 不一致 |

## 关联

- `.github/workflows/ci.yml`
- `scripts/ci/Assert-Format.ps1`、`scripts/ci/Assert-Coverage.ps1`
- `tests/coverlet.runsettings`（覆盖率采集范围）
