# ADR-0010：多平台矩阵与 Release 分发

- 状态：已接受（Accepted）
- 日期：2026-10-22
- 决策者：架构组
- 关联：ARCH P-13 / A-9②；`.github/workflows/ci-platforms.yml`、`.github/workflows/release.yml`、`GitVersion.yml`

## 背景

应用是 Avalonia 跨平台桌面，但 CI 只在 `windows-latest` 上验证——mac/linux 的隐蔽缺陷
（路径分隔符、权限、原生库加载、`OutputType` 差异）无从暴露；同时没有正式发布流水线，
版本号靠手工、制品无校验、无法回退。

## 决策

1. **多平台矩阵（先非阻断）**：`ci-platforms.yml` 在 `windows-latest / macos-latest / ubuntu-latest`
   原生 runner 上全量构建 + 快跑单测，整体 `continue-on-error: true`，作为 `ci.yml` 的兄弟工作流。
   - 先晒缺陷、不挡合并（对应风险 R-6）；三平台稳定后去 `continue-on-error` 并纳入 required checks。
2. **架构测试独立桶**：IL 扫描断言在 `ci-platforms.yml` 的 `architecture-tests` job 隔离跑，提升可见性。
3. **Desktop 跨平台构建**：`.csproj` 按宿主 OS 条件化 `<OutputType>`（Windows=`WinExe`，其余=`Exe`），
   非 Windows 剔除 Windows 专有 `app.manifest` / `.ico`；Windows 行为逐字节不变。
4. **Release 流水线**：`push tags: v*`（+ 手动触发）→ GitVersion（Mainline 模式，`tag-prefix: v`）
   派生 SemVer → 四 RID（win-x64 / linux-x64 / osx-x64 / osx-arm64）在**原生 runner** 上
   `dotnet publish` 自包含单文件 → 每包附 `*.sha256` → 打标签时聚合建 GitHub Release。
5. **回退 = 下载上一版本 Release**；标签即版本锚点，可复现。
6. **签名范围诚实界定**：本阶段落地的是**完整性校验（SHA256）**，不是平台代码签名；
   Authenticode / Developer ID + 公证 / GPG 需证书机密，接入点已在 workflow 注释留位。

## 后果

**正面**

- 跨平台缺陷在 PR 阶段可见（即使暂不阻断，也留下证据链）。
- 发布可复现、制品可校验、可回退；版本号由标签派生，不需要人工维护。

**负面 / 代价**

- 三平台矩阵消耗更多 CI 分钟数；非阻断期间可能长期「黄而不修」，需要跟进门禁升级。
- 未做代码签名，用户首次运行会遇系统安全提示；SHA256 只解决「未被篡改」，不解决「来源可信」。
- 单文件自包含体积较大（win-x64 约 96MB + 原生 Skia/ANGLE/HarfBuzz 伴随库）。

## 替代方案

| 方案 | 为什么不选 |
| --- | --- |
| 只在 Windows 上验证 | 跨平台缺陷留到用户手上，修复成本最高 |
| 矩阵一上来就阻断 | 三平台历史缺陷未知，会长期红、阻塞所有 PR |
| 框架依赖发布（非自包含） | 用户需预装 .NET 运行时，桌面工具分发体验差 |
| 手工发布 + 手写版本号 | 不可复现、易错、无校验 |

## 关联

- `.github/workflows/ci-platforms.yml`、`.github/workflows/release.yml`、`GitVersion.yml`
- `src/BatchAgentRunner.Desktop/BatchAgentRunner.Desktop.csproj`
