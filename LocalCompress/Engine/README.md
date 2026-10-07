# 轻压本地处理内核与对应源码

此构建使用 FFmpeg 8.1 官方发布源码、x264 提交 b35605ace3ddf7c1a5d67a2eb553f034aef41d55 和 dav1d 1.5.3 提交 b546257f770768b2c88258c533da38b91a06f737。不使用 Gyan.dev 二进制，不混用其构建源码声明。

FFmpeg 来源：https://ffmpeg.org/releases/ffmpeg-8.1.tar.xz

x264 原项目：https://code.videolan.org/videolan/x264

x264 对应提交的归档镜像：https://codeload.github.com/mirror/x264/tar.gz/b35605ace3ddf7c1a5d67a2eb553f034aef41d55

dav1d 官方镜像与对应源码：https://codeload.github.com/videolan/dav1d/tar.gz/b546257f770768b2c88258c533da38b91a06f737

对应源码包的 sources 文件夹包含上述完整源码归档和 SHA256SUMS.txt。build.sh 不下载源码，先验证归档哈希，再在输出目录解压构建。没有源码补丁。仅启用外部编码库 x264 和 AV1 解码库 dav1d；其他音视频解码、AAC 编码、滤镜、容器处理使用 FFmpeg 自带实现。网络协议关闭，保留本地文件和管道处理。

构建工具：Windows x64 的 MSYS2 UCRT64，安装 make、diffutils、mingw-w64-ucrt-x86_64-gcc、mingw-w64-ucrt-x86_64-nasm、mingw-w64-ucrt-x86_64-pkgconf、mingw-w64-ucrt-x86_64-meson、mingw-w64-ucrt-x86_64-ninja。构建示例（UCRT64 shell，选择新的空输出目录）：

```sh
bash build.sh /d/localcompress-engine-output
```

构建结果的 licenses/toolchain.txt 记录本次编译器和包版本；config.h、config_components.h、config.mak 与 FFmpeg-version-and-build.txt 记录配置。脚本使用静态链接和 Windows 原生线程，实际导入库应通过 objdump -p 检查。工具链用于开发，普通用户不需要安装。

FFmpeg 构建的许可为 GPLv3，x264 使用其 COPYING 所载许可，dav1d 使用 BSD-2-Clause；本包保留完整文本，以及 MinGW 运行时和 GCC 运行库许可及运行库例外。未添加限制用户修改、复制或再分发这些组件的条款。源码包供查阅、修改及构建，不是运行依赖，也不需要处理视频时联网。

维护者准备源码时可运行 prepare-sources.ps1 -Destination <源码目录>，固定下载地址并校验哈希。编译完成后运行 archive-sources.ps1 -SourceBundle <源码目录> -EngineDirectory <输出目录> -MsysDirectory <msys64目录> -Archive <新建ZIP路径>，将构建信息和对应二进制哈希归档。运行包的 package.ps1 必须提供此 ZIP，并在发布前校验其哈希与内核一致。工具脚本允许联网下载仅用于开发准备；build.sh 使用归档源码，用户入口不调用任何构建或下载脚本。

发布时须将本对应源码包与配套的轻压运行包一同提供下载，并保留版本对应说明和哈希。请勿用此源码包为旧版 Gyan.dev 内核声明对应源码已经齐备。
