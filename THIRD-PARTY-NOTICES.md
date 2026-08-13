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

## LibreHardwareMonitorLib 0.9.6

- 用途：独立管理员传感器代理中的 CPU、GPU、内存、主板、存储与风扇只读实时采集。
- 引用方式：仅由 `XTool.HardwareSensorAgent` 通过 NuGet `LibreHardwareMonitorLib` 0.9.6 使用；普通权限主程序不直接加载该库。
- 许可证：Mozilla Public License 2.0（MPL-2.0）。
- 项目与许可证：<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>。

按 MPL-2.0 要求，修改过的受许可源文件仍须以 MPL-2.0 提供。本项目当前未修改
LibreHardwareMonitor 源文件，仅通过 NuGet 引用其已编译库；发布包仍应保留本声明和
上游许可证入口。

## PawnIO 2.2.0

- 用途：高级硬件实时监控的默认低层只读访问依赖，帮助兼容部分 CPU、内存和主板传感器。
- 发布文件：`ScreenshotApp/tools/pawnio/PawnIO_setup.exe`，版本 2.2.0，长度 3,410,960 字节，SHA-256 `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`。纳入前已验证 Authenticode 签名有效，签名主体为 `namazso.eu`。
- 安装方式：用户首次授权硬件实时监控时，经同一次 UAC 由管理员代理完成固定哈希验证后交互式运行官方安装器；不使用未验证的静默参数，不绕过 Windows 驱动签名或安全策略。取消 X-Tool 授权不卸载 PawnIO。
- 许可证与源码：PawnIO 采用 GPL-2.0 并含独立模块例外，固定源码为 [2.2.0 标签](https://github.com/namazso/PawnIO/tree/2.2.0)（提交 `5cdf470831fdfff3f7f1d06363ca6b230f3bf35a`），上游许可证与源码见 <https://github.com/namazso/PawnIO>。
- 随附源码归档：`ScreenshotApp/tools/pawnio/sources/PawnIO-2.2.0-source.zip`（123,386 字节，SHA-256 `93AA5D410B76C71E9004CAC406ED19D0550A735410A9ABE4D0C9A838B8B98EAC`）及其固定子模块 `PawnPP-e64e4c37-source.zip`（35,340 字节，SHA-256 `20AA0638B95C90D296935310F3F28CC22DE1FB24855C5BAA6721E2AE95EB6042`）。

任何 X-Tool 发布包或 GitHub Release 若包含该安装器，必须同时提供对应的完整 PawnIO 源码（含子模块）、GPL-2.0 许可证、版本/哈希清单与安装/卸载说明。变更 PawnIO 版本必须重新完成许可证、签名、安装、卸载、回滚和供应链审计。

## sherpa-onnx 1.13.4 与 SenseVoice int8 模型

- 用途：本地离线语音输入的麦克风语音识别。
- 运行时：NuGet `org.k2fsa.sherpa.onnx` 1.13.4，Apache License 2.0。
- 模型：`sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17`，适用 FunASR Model Open Source License Agreement 1.1；转换后的 `model.int8.onnx` 与同目录 `LICENSE` 一并随安装包分发，并保留模型名称、来源与作者信息。
- 模型来源：<https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models>。
- 运行所用权重的 SHA-256、固定下载地址位于 `ScreenshotApp/Models/VoiceInput/default/XTOOL-MODEL-MANIFEST.txt`。

该模型权重超过普通 GitHub 单文件限制，不纳入源码仓库；发布安装包或 GitHub Release 时必须包含模型、`tokens.txt`、许可证和上述清单，保证安装完成后可完全离线使用。
