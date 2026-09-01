<div align="center">

# NanoPic

**一个小于 2 MB 的单文件工具，帮你批量压缩和转换图片。**

免安装 · 双击即用 · 图片不出电脑

[![Release](https://img.shields.io/github/v/release/luvcamor/NanoPic?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC)](https://github.com/luvcamor/NanoPic/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/luvcamor/NanoPic/total?label=%E7%B4%AF%E8%AE%A1%E4%B8%8B%E8%BD%BD)](https://github.com/luvcamor/NanoPic/releases)
<div align="center">

[![Give a Star](https://img.shields.io/badge/Star%20This%20Repo-⭐-yellow?style=for-the-badge&logo=github)](https://github.com/luvcamor/nanopic/stargazers)

</div>

[下载正式版](https://github.com/luvcamor/NanoPic/releases/latest) · [问题反馈](https://github.com/luvcamor/NanoPic/issues)

</div>

NanoPic 是一款 Windows 上的图片压缩和转换小工具。把图片拖进去，设好想要的格式和大小，点一下开始，它就能帮你把一堆图片一次性处理好。

## 它好在哪

- **体积小**：整个软件只有一个文件，小于 2 MB，不占地方
- **免安装**：下载解压就能用，不用装这装那，删掉也不留痕迹
- **省事**：一次能处理一大堆图片，多张同时进行，速度快
- **按质量或目标大小压缩**：JPEG/WebP 调整编码质量；PNG 优先在保持图片尺寸的情况下进行颜色量化优化；若目标体积仍无法达到，可由你选择是否缩小图片尺寸
- **智能防体积膨胀**：优化后若未减小体积将自动保留原图最佳数据，绝不越压越大
- **超大图片也能处理**：遇到超高像素的图片自动缩小，不会报错
- **放心**：图片全程在你电脑上处理，不用上传到任何网站

## 界面预览

![NanoPic 界面预览](docs/ui.jpg)

## 能做什么

- 支持 `JPG`、`PNG`、`WEBP`、`GIF`、`BMP`、`TIFF`、`ICO` 这些常见格式
- 批量压缩、转换格式、调整尺寸
- 手机竖拍的照片自动摆正
- 加文字水印、调亮度
- 自己定义输出的文件名和存放位置
- 可选把「添加到 NanoPic」放进图片的右键菜单
- 支持快捷键操作（如 `F5` 开始、`Esc` 取消）

## 怎么用

1. 去 [Releases](https://github.com/luvcamor/NanoPic/releases/latest) 下载 `NanoPic.exe`；如果下载的是便携压缩包，请先解压
2. 双击运行 `NanoPic.exe`
3. 把图片拖进窗口，或点「添加文件 / 打开文件夹」
4. 选好格式、大小等设置
5. 点「开始压缩」（或按 `F5`）

## 右键菜单集成（可选）

默认关闭。在「压缩参数 → 其他」里勾选**集成到右键菜单**后，选中图片点右键就能看到「添加到 NanoPic」，一次选多张也会整批进入同一个窗口的队列。

- 只对上面列出的图片格式生效（`.jpg` `.jpeg` `.jpe` `.jfif` `.png` `.webp` `.gif` `.bmp` `.tif` `.tiff` `.ico`）；选中的文件里混有其他类型时不显示这个菜单
- **Windows 11**：先点右键菜单里的「显示更多选项」，菜单在经典列表中；Windows 10 直接显示
- 只写入当前用户的设置，不需要管理员权限，也不会改变图片的默认打开方式
- NanoPic 已经打开时，图片加入最近使用的那个窗口；正在压缩时新加入的图片排在队列里等下一批
- 挪动了 `NanoPic.exe` 的位置：下次启动会自动把菜单指向新位置；如果旧位置的程序还在，会让你选择「设为当前版本」
- 取消勾选即彻底移除；只删除 NanoPic 自己写入的项，不会动其他软件的右键菜单
- 如果开关旁提示「需要修复」或「存在冲突」：前者点「修复」即可，后者说明同名项被其他程序改写，可点「复制诊断信息」后到 Issues 反馈

## 问题反馈

遇到问题欢迎到 [GitHub Issues](https://github.com/luvcamor/NanoPic/issues) 提出来，或通过程序「关于」页面里的「联系作者」入口反馈。

## ⭐️ 喜欢这个项目吗？
如果这个项目对你有帮助，欢迎给一个 **Star** 给予支持！你的支持是持续更新的动力 🚀

## 许可

本项目开源，详见 [LICENSE](LICENSE)。
