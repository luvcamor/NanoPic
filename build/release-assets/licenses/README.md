# NanoPic 3.2.2 发布许可证目录

此目录与发布根目录的 `THIRD-PARTY-NOTICES.txt`、`SBOM.spdx.json` 一起构成 NanoPic 3.2.2 的第三方组件许可证记录。

| 组件 | 版本 | 许可证 | 对应文件 |
|---|---:|---|---|
| libwebp（官方 Windows x64 `cwebp` / `dwebp`） | 1.6.0 | BSD-3-Clause | `BSD-3-CLAUSE-LIBWEBP.txt` |
| Costura.Fody（嵌入式程序集加载器） | 6.2.0 | MIT | `MIT-MANAGED-DEPENDENCIES.txt` |
| Microsoft 管理运行时库 | 见 `THIRD-PARTY-NOTICES.txt` / SBOM | MIT | `MIT-MANAGED-DEPENDENCIES.txt` |

运行时可执行文件为单一 `NanoPic.exe`。Windows Imaging Component 与 .NET Framework 4.8 是 Windows 系统组件，不随 NanoPic 重复分发。本目录仅提供许可证与审计材料，不是运行 NanoPic 所需的额外二进制依赖。
