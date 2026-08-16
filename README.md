<div align="center">

# NanoPic

一款面向 Windows 用户的批量图片压缩工具

[![Release](https://img.shields.io/github/v/release/luvcamor/NanoPic?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC)](https://github.com/luvcamor/NanoPic/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/luvcamor/NanoPic/total?label=%E7%B4%AF%E8%AE%A1%E4%B8%8B%E8%BD%BD)](https://github.com/luvcamor/NanoPic/releases)
[![License](https://img.shields.io/github/license/luvcamor/NanoPic?label=%E8%AE%B8%E5%8F%AF%E8%AF%81)](LICENSE)

[下载正式版](https://github.com/luvcamor/NanoPic/releases/latest) | [问题反馈](https://github.com/luvcamor/NanoPic/issues)

</div>

NanoPic 是一款面向 Windows 的图形化批量图片压缩工具，适合日常整理图片、控制文件体积、统一输出格式，以及快速处理大量图片素材。

它提供清晰直观的操作界面，支持拖拽导入、批量压缩、尺寸调整、格式转换、命名规则设置和多线程处理，适用于个人用户、内容创作者以及轻量办公场景。

## NanoPic 3.0

NanoPic 使用 .NET Framework 4.8 与 WPF，图像后端为 Windows WIC + 内嵌 libwebp 1.6.0，发布为轻量单 EXE。源码入口为 `NanoPic.sln`。

```powershell
dotnet restore NanoPic.sln --locked-mode --configfile NuGet.config
dotnet build NanoPic.sln -c Release --no-restore
dotnet test NanoPic.sln -c Release --no-build --no-restore
./build/Publish-NanoPic.ps1
```

NanoPic 3.1（`3.1.0`）为当前正式版本。

## 界面预览

![NanoPic 界面预览](docs/ui.jpg)

## 为什么选择 NanoPic

- 打开即可使用，无需手动构建
- 支持批量导入和批量压缩，适合整理大量图片
- 支持 `JPG`、`PNG`、`WEBP`、`GIF`、`BMP`、`TIFF`、`ICO` 输入与当前 3.0 输出能力
- 支持按压缩强度或目标体积两种方式压缩
- 支持尺寸调整、背景填充、亮度处理和水印
- 支持覆盖原文件、保留目录结构或输出到指定文件夹
- 支持命名规则、序号控制和日志记录

## 下载与使用

正式版本请前往 GitHub Releases 下载：

- 最新版本：https://github.com/luvcamor/NanoPic/releases/latest

使用步骤：

1. 下载并解压发布包
2. 运行 `NanoPic.exe`
3. 拖入图片，或点击“添加文件 / 打开文件夹”
4. 根据需要设置压缩方式、输出格式、尺寸和命名规则
5. 点击“开始压缩”

## 主要功能

### 批量导入

- 支持导入单张图片、多个文件或整个文件夹
- 支持拖拽到主界面快速加入列表

### 压缩与格式输出

- 支持输出 `JPG`、`PNG`、`WEBP`、`GIF`、`BMP`、`TIFF`、`ICO`
- 支持按压缩强度输出
- 支持按目标体积压缩
- 支持允许或限制超出目标大小

### 图像处理

- 支持保持原尺寸或按规则调整尺寸
- 支持背景填充
- 支持亮度调整
- 支持添加文字水印

### 输出控制

- 支持覆盖原文件
- 支持保留原文件夹结构
- 支持输出到指定独立文件夹
- 支持自定义命名方式与起始序号

## 命名规则

NanoPic 支持以下占位符：

- `{index}`：输出序号
- `{name}`：原文件名，不包含扩展名
- `{ext}`：目标扩展名，不包含点号

示例：

- `{index}` -> `1.jpg`
- `{name}_{index}` -> `sample_1.jpg`
- `{name}` -> `sample.jpg`

## 配置与日志

- 程序在 `%LOCALAPPDATA%\NanoPic\settings.json` 保存当前配置
- 日志默认保存在 `%LOCALAPPDATA%\NanoPic\logs\` 目录
- 日志会记录启动、退出、批处理过程以及失败图片信息

## 适用环境

- 操作系统：Windows 10/11 x64
- 系统组件：.NET Framework 4.8、Windows Imaging Component（无需安装 .NET 8）
- 运行方式：下载后直接运行单个 `NanoPic.exe`

## 问题反馈

如果在使用过程中遇到问题，欢迎通过以下方式反馈：

- GitHub Issues：https://github.com/luvcamor/NanoPic/issues
- 程序“关于”页面中的“联系作者”入口

## 第三方依赖与许可

- NanoPic 3.0：[THIRD-PARTY-NOTICES.txt](src/NanoPic.App/THIRD-PARTY-NOTICES.txt)
- libwebp 1.6.0：[BSD-3-Clause](build/release-assets/licenses/BSD-3-CLAUSE-LIBWEBP.txt)
- [NanoPic LICENSE](LICENSE)
