# 第三方组件声明

## FFmpeg 8.1.2 Essentials Build（Windows x64）

- 位置：`ScreenshotApp/tools/ffmpeg`
- 用途：音频与视频的探测、转换、压缩、提取音频及 GIF 生成。
- 构建来源：Gyan.Dev Windows Builds。
- FFmpeg 源码版本：[38b88335f9](https://github.com/FFmpeg/FFmpeg/commit/38b88335f9)
- 构建版本：`8.1.2-essentials_build-www.gyan.dev`
- 许可证：GNU General Public License v3.0。
- 归档 SHA-256：`E25B682664025D49034C981AFB4BAE36238A40F29A3CC1C713AD9A8B5B3528F6`

随附二进制的许可证全文与构建配置位于
`ScreenshotApp/tools/ffmpeg/LICENSES/`。公开发布前，必须在 GitHub Release 中提供与
该二进制完全对应的 FFmpeg 及其启用外部库的源码、构建配置和许可证材料。

## PDFsharp 6.2.4

- 用途：转 PDF 功能中的图片 PDF 生成、已有 PDF 合并与页面导入。
- 引用方式：NuGet `PDFsharp` 6.2.4。
- 许可证：MIT License。
- 项目与许可证：<https://www.nuget.org/packages/PDFsharp/6.2.4>。

## ZXing.Net 0.16.11

- 用途：转换器工作台二维码功能的本地生成与图片识别，全程离线处理。
- 引用方式：NuGet `ZXing.Net` 0.16.11。
- 许可证：Apache License 2.0。
- 项目与许可证：<https://www.nuget.org/packages/ZXing.Net/0.16.11>。

## MahApps.Metro.IconPacks.Material 6.2.1

- 用途：智能家居房间导航、菜单以及没有专用设备插画时的矢量后备图标。
- 引用方式：NuGet `MahApps.Metro.IconPacks.Material` 6.2.1。
- 组件许可证：MIT License；Material Design Icons 图标数据遵循其上游许可证。
- 项目与许可证：<https://www.nuget.org/packages/MahApps.Metro.IconPacks.Material/6.2.1>。

## sherpa-onnx 1.13.4 与 SenseVoice int8 模型

- 用途：本地离线语音输入的麦克风语音识别。
- 运行时：NuGet `org.k2fsa.sherpa.onnx` 1.13.4，Apache License 2.0。
- 模型：`sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17`，适用 FunASR Model Open Source License Agreement 1.1；转换后的 `model.int8.onnx` 与同目录 `LICENSE` 一并随安装包分发，并保留模型名称、来源与作者信息。
- 模型来源：<https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models>。
- 运行所用权重的 SHA-256、固定下载地址位于 `ScreenshotApp/Models/VoiceInput/default/XTOOL-MODEL-MANIFEST.txt`。

该模型权重超过普通 GitHub 单文件限制，不纳入源码仓库；发布安装包或 GitHub Release 时必须包含模型、`tokens.txt`、许可证和上述清单，保证安装完成后可完全离线使用。
