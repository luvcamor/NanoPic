# AGENTS.md — NanoPic 项目协作指南

本文件供 AI 编码代理与新成员快速了解本仓库的构建方式、硬性约束与约定。改动代码前请完整阅读。

## 项目概述

NanoPic 是一款 Windows 便携图片压缩与格式转换工具（WPF 桌面应用，简体中文界面）。

- 目标框架：**net48**（.NET Framework 4.8），仅 x64，单文件发布（Costura.Fody 将托管依赖织入单一 `NanoPic.exe`）
- 编解码后端：Windows WIC（`System.Windows.Media.Imaging`）+ 内嵌 libwebp 1.6.0 官方 `cwebp`/`dwebp`（作为嵌入资源，运行时释放到 `%LOCALAPPDATA%` 并做 SHA-256 校验）
- 无网络行为：图片全程本地处理，无遥测

## 仓库结构

```
src/
  NanoPic.Core/           领域层：契约、安全校验、头部探测、路径规划、批处理器（无 WPF 依赖）
  NanoPic.Codecs/         编解码：WicImageCodec（WPF）、PngQuantizer、内嵌 libwebp 调用（LibWebpTools）
  NanoPic.Infrastructure/ 设置存储（JSON 原子写+备份）、输出路径规划、路径脱敏日志、快捷键路由
  NanoPic.App/            WPF 主界面（单窗口）+ `--smoke-test <in> <out>` CLI 冒烟模式
build/                    发布脚本（Publish-NanoPic.ps1 / Build-NanoPicPortable.ps1）、SBOM/许可证模板
tests/                    Core / Codecs / Integration / App / Differential 五个测试项目
```

注意：`src/NanoPic.Codecs/MagickNet*.cs` 被 csproj `Compile Remove` 排除，仅作为差分测试基线存在。**不要重新启用**——若必须启用，必须同步更新 SBOM 模板与 THIRD-PARTY-NOTICES（当前发布物不含 ImageMagick 是许可合规的既定事实）。

## 常用命令

```powershell
# 全量测试（Debug）
dotnet test NanoPic.sln -c Debug

# 完整发布管线（锁定还原 → 构建 → Release 全量测试 → 大小门禁 → 产物+SBOM+清单）
powershell -ExecutionPolicy Bypass -File build/Publish-NanoPic.ps1 -Configuration Release -RuntimeIdentifier win-x64
```

- SDK 由 `global.json` 钉死（`10.0.200`，`rollForward: disable`），本机需安装该版本
- 仅支持 Windows + x64
- `Publish-NanoPic.ps1` 必须用 PowerShell 执行（Git Bash 直接 `./xxx.ps1` 会失败）

## 硬性门禁（违反即 CI 失败）

1. **大小门禁**：`NanoPic.exe` 必须 < 2,000,000 字节。新增托管依赖前先估算织入后体积。
2. **锁定还原**：CI 与发布脚本用 `dotnet restore --locked-mode`，仓库提交了各项目的 `packages.lock.json`。**改依赖必须同步再生成锁文件**（`dotnet restore` 不带 locked-mode 后提交），否则 NU1004 还原失败（Dependabot 的 PR 常因此失败，属已知现象）。
3. **TreatWarningsAsErrors**：所有警告即错误；`Nullable` 启用。
4. **net48 API 限制**：没有 `string.Contains(string, StringComparison)`（用 `IndexOf`），没有 .NET Core 专属 API；兼容垫片在 `build/NetFrameworkCompatibility.cs`。
5. **NuGet 源**：仅 nuget.org（`NuGet.config` 有 `<clear />`）；版本统一走中央管理 `Directory.Packages.props`，不要在 csproj 写版本号。
6. **CI 的 GitHub Actions 按 commit SHA 固定**，升级由 Dependabot 跟进；CI 会审计易受攻击的包（出现 `"vulnerabilities"` 键即失败）。
7. **内嵌 libwebp 哈希链**：`LibWebpTools.cs` 中的 `SourceUrl`/`ArchiveSha256`/`CwebpSha256`/`DwebpSha256` 必须与官方 zip 及 `src/NanoPic.Codecs/Native/libwebp-1.6.0/` 内的二进制三方一致，CI 有独立步骤校验。替换二进制必须同步更新全部常量。

## 关键设计约定（改代码前必读）

- **解压炸弹防御是双阶段的**：解码前纯头部探测（`ImageDimensionProbe`）+ 解码后元数据复核（`ImageSafetyValidator.ValidateWithAction`），软限触发降采样、硬限直接拒绝。改管线时不得绕过任何一相。
- **skip-if-larger**：源格式 == 输出格式且像素/元数据均未变化时，重编码若不小于源文件则保留源文件字节（`WicImageCodec.CanReuseSourceFile`）。这是防止"越压越大"的核心保护，覆盖 BMP/GIF 等全部同格式场景。
- **压平仅在必要时发生**：`shouldFlatten = 输出格式不支持 alpha && HasAlpha(prepared)`。不要给不透明图像无条件压平——会绕过 skip-if-larger 并使调色板 BMP 膨胀数倍（历史教训）。
- **多帧语义**：只有 GIF/TIFF 输出保留多帧；其余格式仅编码第一帧，服务层必须返回 `FrameNotice` 提示用户。
- **路径安全**：输出文件名模板经非法字符/分隔符校验；保留目录结构模式有 `..` 逃逸检查；扫描器跳过 reparse point。长路径统一走 `PortablePath.ForFileSystem`（`\\?\` 前缀）。
- **日志脱敏**：所有落盘日志经 `RedactingFileLogger` 隐藏文件路径；新增日志文案不要绕过它。
- **文件写入原子性**：临时文件 + `File.Replace` 落盘（图片输出与设置存储均如此），失败时清理临时文件，不得直接覆盖写目标路径。
- **动画 WebP 输入**：dwebp 不支持，返回"暂不支持动画 WebP"的结构化失败，不要试图绕过。

## 版本与发布

- 版本号单一来源：`Directory.Build.props`（`Version`/`AssemblyVersion`/`FileVersion` 三处一起改）；`src/NanoPic.App/THIRD-PARTY-NOTICES.txt` 与 `build/release-assets/licenses/README.md` 的版本字符串同步更新。
- 发布流程：改版本 → `Publish-NanoPic.ps1` 本地全量验证 → 提交（`release: vX.Y.Z ...`）→ 推 tag → `gh release create` 上传 `NanoPic.exe`、`NanoPic.exe.sha256`、`NanoPic-vX.Y.Z-win-x64.zip`。
- Release 说明写**用户视角**（行为变化，不讲内部实现），参考既有 Release 风格。
- 提交信息用中文 + 类型前缀（`fix:` / `ci:` / `docs:` / `release:`），遵循现有历史风格。
- **不要把开发过程文档（审计报告、计划、内部笔记）提交进仓库**；此类文件留在仓库外。

## 测试约定

- 测试图像优先用 Magick.NET（仅测试项目引用）在临时目录生成，结束必须清理；真实资产在 `tests/assets/legacy-inputs/`，经 csproj `CopyToOutputDirectory` 复制，取用路径为 `AppContext.BaseDirectory/assets/`。
- 集成测试通过 `ImageFileProcessingService` + `WicImageCodec` 走完整管线，断言落盘字节/签名/尺寸，而非仅断言返回值。
- 差分测试（DifferentialTests）以 Magick.NET 输出为基线对照旧行为，改压缩策略前先看 `assets/legacy-jpeg-quality-80.json`。
- 修复缺陷时补回归测试，命名延续现有风格（如 `C3c_...`、`T1_...`、`P1_...` 系列）。
