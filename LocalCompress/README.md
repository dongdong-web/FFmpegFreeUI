# 轻压：本地视频压缩

这是 FFmpegFreeUI fork 中的独立小白入口，当前版本 0.1.0，面向 Windows 10/11 x64。
上游专业版源码保留；简易入口使用标准 WinForms 和本地 FFmpeg，不加载上游的 LakeUI、Agent、社区、更新器、插件或远程调用。

## 使用

解压完整体验包，双击 `LocalCompress.exe`。不用安装 .NET，不用配置 PATH，不用登录。

1. 添加视频，或把视频拖到窗口。
2. 选择画质优先、均衡（默认）或体积优先。
3. 点击开始压缩。默认在原视频旁生成 `文件名_压缩.mp4`，也可选择本机保存文件夹。

原文件不覆盖，已有结果不覆盖。成功后显示输出大小和减少比例。
结果没有变小时自动丢弃；失败或停止时清理本次临时输出。双击任务查看具体原因。

## 隐私范围

- 简易入口没有网络客户端、账号、遥测、AI、自动下载、自动更新或远程服务。
- 只接受本机磁盘文件。网址、UNC 路径、网络映射磁盘、符号链接、目录联接和云占位文件被拒绝。
- FFmpeg/ffprobe 仅允许 `file,pipe` 协议，本地播放列表不能间接获取 HTTP 视频。
- 输入、输出和任务详情均留在本机，任务列表仅保存在进程内存中。
- 输出不复制可选文件元数据或章节；视频画面、声音本身并不会被匿名化。
- .NET 单文件运行时可能在系统临时目录解压运行库；视频临时结果写入用户选择的输出目录。
- 应用不上传文件。操作系统、第三方安全软件和云盘客户端是独立程序；需要纯离线存储时，请选择非云同步的本机文件夹。
- 这些约束只覆盖 `LocalCompress.exe`，不覆盖仓库保留的上游专业版程序或自行替换的第三方可执行文件。

## 首版能力边界

输出 H.264/AAC MP4，保持尺寸（奇数边长最多缩小 1 像素），保留第一个视频流和第一个音轨。
字幕、额外音轨、章节和附件不保留。HDR 暂不处理，明确报错并保留原文件。
恒定质量压缩不承诺固定百分比、目标大小或视觉无损。采用 CPU 编码，批量任务串行执行。
正常退出和取消会清理临时结果；断电或强制结束进程可能留下 `.localcompress-*.mp4`，不会覆盖原文件。

## 开发和验证

需要 .NET 10 SDK；核心、界面和测试没有第三方 NuGet 依赖。

```powershell
dotnet build LocalCompress/App/LocalCompress.App.csproj -c Release
dotnet run --project LocalCompress/Tests/LocalCompress.Tests.csproj -c Release
```

集成测试需要可信的 `ffmpeg.exe` 和 `ffprobe.exe` 在 PATH，包含 libx264 和 AAC。
测试使用合成视频，覆盖原文件完整性、名称冲突、中文路径、无音轨、HDR、失败、取消和远程播放列表拒绝。

打包（只在开发时准备引擎，用户运行时不下载）：

```powershell
./LocalCompress/package.ps1 -FFmpegDirectory 'D:\tools\ffmpeg-build'
```

引擎目录必须包含 `bin/ffmpeg.exe`、`bin/ffprobe.exe`、`LICENSE` 和 `README.txt`。
产物位于 `artifacts/LocalCompress-win-x64`，包含自带运行时的应用、引擎、许可和使用说明。
发布给公众前须提供所选 FFmpeg 构建及其依赖的对应源代码，按其 GPL 等许可完成分发材料；本次只生成本地体验包，没有公开发布二进制 Release。

## 维护

主程序：`App/MainForm.cs`；本地处理与验证：`Core/CompressionEngine.cs`；测试：`Tests/Program.cs`。
上游基线：`65aec1ff4dcd62a54c4361fe1719520378750c36`。
本入口暂未复用上游 VB 任务引擎，避免把专业 UI 和联网初始化带入简易版。
上游 MIT 声明保留；FFmpeg 与 .NET 运行时使用各自许可。本入口不依赖 LakeUI。
