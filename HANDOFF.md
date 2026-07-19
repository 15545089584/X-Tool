# X-Tool 开发交接文档

更新时间：2026-07-19

项目目录：`D:\Claude Code\X-Tool`

技术栈：WPF / C# / .NET 6（`net6.0-windows`）

当前分支：`main`

当前功能代码基线：`8703c04 优化音频转换页面样式与信息布局`（其后仅有本交接文档更新）

> 本文档以当前代码为准。旧名称“截影 / JieYing”只可能残留在部分内部命名和本机配置目录中，不再代表当前产品定位。

## 一、必须遵守的开发约定

### 文件与代码

- 所有文件读写保持 UTF-8；PowerShell 读取中文前先执行 `chcp 65001`，并使用 `Get-Content -Encoding UTF8`。
- 不使用 `sed` / `awk` 修改含中文文件，优先使用 `apply_patch`。
- 代码注释使用中文。
- 工作区可能存在用户自己的改动；只修改任务相关文件，不覆盖或清理无关内容。

### 构建与启动

正式项目：`ScreenshotApp\ScreenshotApp.csproj`。

```powershell
Set-Location 'D:\Claude Code\X-Tool'
Get-Process XTool,JieYing -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\ScreenshotApp\ScreenshotApp.csproj -c Release
Start-Process '.\ScreenshotApp\bin\Release\net6.0-windows\XTool.exe'
```

- 当前程序集名与正式可执行文件均为 `XTool` / `XTool.exe`。
- 构建前必须关闭正在运行的新版或旧版进程，否则 Release 文件可能被锁定。
- 完成功能后应做与风险相称的手动验证；较大且已验证的改动创建中文本地 Git 提交。
- 未经用户明确要求，不得推送 GitHub。
- 当前未跟踪目录 `ClipboardDiagnostics/` 是临时诊断程序，不属于正式产品，必须保持未提交状态。

### Git 检查

开始与结束时都执行：

```powershell
git status --short
git log -5 --oneline
```

提交时只暂存本次任务文件，禁止使用会误收 `ClipboardDiagnostics/` 的宽泛暂存方式。

## 二、当前产品定位与主导航

产品已由单一截图工具扩展为桌面效率工具箱 **X-Tool**。

当前一级导航：

1. 首页
2. 屏幕工作台
3. 转换器工作台
4. 快捷键
5. 设置

剪贴板不再是一级侧栏项，而是屏幕工作台的组成部分；首页与屏幕工作台均可进入剪贴板。

全局快捷键：

- `Ctrl + Shift + A`：启动普通截图。
- `Ctrl + Shift + V`：不唤起主窗口，打开剪贴板快速选择浮窗。
- `右 Alt`：启动/结束本地离线语音输入；不会因短暂停顿自动结束，单次录音最长 90 秒。

全局快捷键可在“快捷键”页点击后重新录入，支持截图、剪贴板选择粘贴和语音输入。保存前检查 X-Tool 内部重复、已知 Windows 保留组合及 Windows 已注册的外部全局热键；Windows 无法枚举其他软件未注册的低级键盘钩子。语音默认仍为右 Alt，也可改为标准组合键。

## 三、屏幕工作台现状

### 截图与标注

- 普通截图支持框选、八方向调整、涂鸦、形状、箭头、直线、颜色与线宽共享设置、取色、文字提取、翻译、长截图、录像和贴图。
- 标注工具栏与各级面板采用当前浅色毛玻璃设计。
- 普通截图、长截图、文字提取、翻译和屏幕录制均可在剪贴板页面分类查看。

### 贴图

- 普通截图工具栏已有“贴图”按钮，不再是待开发功能。
- 当前选区可生成独立、可拖动贴图窗口，支持同时存在多个窗口。
- 每个贴图右上角有关闭与图钉按钮。
- 设置页提供“贴图默认置顶”；代码默认值为 `true`，单个贴图仍可临时切换置顶状态。
- 贴图及截图/录像辅助窗口使用 `WdaExcludeFromCapture`，不进入后续截图或录像画面。
- 关键文件：`ScreenshotApp\Sticker\StickerWindow.cs`、`ScreenshotApp\Capture\SelectionOverlayWindow.xaml(.cs)`。

### OCR、翻译与录像

- OCR：本地 ONNX 推理。
- 翻译：本地 ONNX 英译中模型。
- 录像：Media Foundation H.264 MP4，WASAPI 音频采集；包含倒计时、区域边框、控制条与视频封面。
- 相关辅助窗口均需继续保持排除捕获能力。

### 本地语音输入

- 使用 `org.k2fsa.sherpa.onnx` 1.13.4 与固定版 SenseVoice int8 模型，在本机完成录音与识别，不上传音频或文本。
- 录音使用 NAudio 的 16 kHz 单声道输入；录音期间约每 1.2 秒对当前音频快照进行一次本地识别，彩虹气泡直接显示最新片段，不再提前写入目标程序；再次按快捷键或录音达到 90 秒时结束，再将完整识别结果一次性粘贴到原输入窗口。若最终解码暂时没有结果但气泡已有文本，会使用预览文本兜底粘贴。按 Esc 会取消当前语音输入且不写入任何内容；也可在设置中改为仅复制。
- 模型位置：`ScreenshotApp\Models\VoiceInput\default\`。权重单文件超过普通 GitHub 限制，已被 `.gitignore` 排除；发布安装包/Release 时必须带上 `model.int8.onnx`、`tokens.txt`、`LICENSE` 与 `XTOOL-MODEL-MANIFEST.txt`。
- 关键文件：`ScreenshotApp\VoiceInput\VoiceInputService.cs`、`VoiceInputOverlayWindow.xaml(.cs)`、`MainWindow.xaml(.cs)`。

## 四、剪贴板现状

### 保存与分类

- 监听 Windows 剪贴板更新，将非 X-Tool 产生的文字或图片保存为“外部复制”。
- 应用写入剪贴板时附加 `X-Tool.InternalClipboard` 标记，避免重复归类为外部复制。
- 剪贴板页面分类包括：全部、普通截图、长截图、文字提取、翻译、屏幕录制、外部复制。
- 快速浮窗分类包括：全部、图片、文字、翻译、文本提取、外部复制；按时间倒序展示。
- 浮窗使用非激活显示以尽量保留原输入窗口焦点，选择记录后写入系统剪贴板并向原窗口发送粘贴操作。

### 已解决的粘贴问题

- 最终仍采用标准 Windows 系统剪贴板链路，没有改用高风险的进程注入方案。
- `ClipboardService` 使用 WinForms `SetDataObject(..., true, 3, 25)`：最多 3 次、每次间隔 25 ms，用于避开 Explorer 等程序的正常短锁。
- 文字和图片均已实际验证可以粘贴。
- `ClipboardDiagnostics/` 仅用于当时定位占用窗口；诊断代码不进入正式提交。

关键文件：

- `ScreenshotApp\Clipboard\ClipboardService.cs`
- `ScreenshotApp\Clipboard\ClipboardPickerWindow.xaml(.cs)`
- `ScreenshotApp\History\ScreenshotHistoryStore.cs`
- `ScreenshotApp\MainWindow.xaml(.cs)`

## 五、设置与存储位置

设置页当前提供：

- 截图后自动复制
- 保存截图历史
- 贴图默认置顶
- 本地语音输入开关与识别后自动粘贴开关
- 可展开的分类存储位置

默认目录：

| 分类 | 默认位置 |
| --- | --- |
| 截图 | `E:\截影\Screenshots` |
| 长截图 | `E:\截影\LongScreenshots` |
| 文字提取 | `E:\截影\History\文字提取` |
| 翻译 | `E:\截影\History\翻译` |
| 屏幕录制 | `E:\截影\Recordings` |
| 外部复制 | `E:\截影\Clipboard` |

- 转换器不保存固定目录；图片、音频和视频任务都由用户每次手动选择输出位置。
- 偏好文件当前仍保存在 `%LocalAppData%\JieYing\preferences.json`。这是兼容旧版本的内部遗留命名，若以后迁移到 `X-Tool`，必须设计兼容迁移，不能直接改路径导致用户设置丢失。

## 六、转换器工作台现状

转换器工作台拥有独立的竖向二级工具栏，目前包含图片、音频和视频三个图标入口。顶部始终保留“转换器工作台”标题与介绍；选择工具只替换右侧工作区。

### 1. 图片处理（已可用）

支持输入/输出：PNG、JPEG、BMP、TIFF。

能力：

- 多文件选择与队列展示。
- 格式转换。
- JPEG 压缩质量调整。
- 按比例缩放。
- 每次任务手动选择输出目录。
- 转换前后预览。

预览不是静态占位图：

- 输出格式、质量、缩放滑杆变化都会进入 `UpdateImageConversionControls()`。
- 该方法调用 `UpdateImagePreviews()`。
- `ImageConversionService.CreatePreview()` 会按当前格式、缩放比例和 JPEG 质量重新生成“处理后”预览。

注意：预览框使用固定视口，改变尺寸时画面仍会填充相同区域；JPEG 高质量区间的视觉差异也可能很小，因此用户可能误以为没有刷新。后续可增加像素尺寸、预计体积或刷新状态，使变化更明确。

关键文件：

- `ScreenshotApp\MainWindow.xaml(.cs)`（图片处理页仍在主窗口内）
- `ScreenshotApp\Converters\ImageConversionService.cs`

### 2. 音频处理（已实现）

输入：MP3、WAV、M4A、AAC、FLAC、OGG、OPUS、WMA。

输出：MP3、WAV、M4A、FLAC、OGG、OPUS。

已实现：

- 拖放/多文件选择。
- 文件名、格式、大小、时长、编码、状态列。
- ffprobe 获取媒体信息。
- 音质预设：高质量 320 kbps、标准 192 kbps、小文件 128 kbps、语音 64 kbps。
- 预设切换会动态更新说明文字。
- 高级设置：比特率、采样率、声道、保留元数据。
- 异步队列、实时进度、取消、失败原因、打开输出目录。
- 最新音频页已统一毛玻璃下拉框、展开卡、复选框和主按钮样式。

关键文件：`ScreenshotApp\Converters\AudioConverterView.xaml(.cs)`。

### 3. 视频处理（已实现基础功能，UI 待继续统一）

输入：MP4、MOV、MKV、AVI、WebM、FLV、WMV。

输出：MP4、MOV、MKV、WebM、GIF；提取音频可输出 MP3、M4A、WAV。

已实现：

- 视频格式转换与压缩。
- 提取音频。
- 视频转 GIF，可设置时间范围、FPS 与宽度。
- 分辨率、FPS、编码、时长、大小探测。
- 编码、码率、宽度等参数。
- 异步队列、实时进度、取消、失败原因、打开输出目录。

关键文件：`ScreenshotApp\Converters\VideoConverterView.xaml(.cs)`。

当前视频页功能逻辑已接通，但视觉控件尚未全面达到最新音频页的统一程度。下一轮适合先按音频页的控件资源和排版继续打磨视频页。

### 4. FFmpeg 引擎

统一入口：`ScreenshotApp\Converters\MediaConversionService.cs`。

查找顺序覆盖应用目录下 `tools\ffmpeg`、应用基础目录、当前工作目录及系统 `PATH`。必须同时找到 `ffmpeg.exe` 与 `ffprobe.exe` 才视为可用。

已实现：

- ffprobe JSON 解析。
- FFmpeg 异步执行，不阻塞 UI。
- `-progress pipe:1` 实时进度解析。
- CancellationToken 取消并终止进程树。
- 转换失败原因回传。
- 日志：`%LocalAppData%\X-Tool\Logs\media-conversion.log`。
- 服务层与页面层分离，后续可扩展随包引擎、GPU 编码、批量队列和任务调度。

项目现已随 Release 包直接分发固定版本的 Windows x64 FFmpeg 引擎：

- 位置：`ScreenshotApp\tools\ffmpeg\ffmpeg.exe`、`ScreenshotApp\tools\ffmpeg\ffprobe.exe`。
- 版本：FFmpeg 8.1.2 Essentials Build（Gyan.Dev），GPLv3，归档 SHA-256 为 `E25B682664025D49034C981AFB4BAE36238A40F29A3CC1C713AD9A8B5B3528F6`。
- 构建文件约 194 MB，仅保留运行所需的 `ffmpeg.exe`、`ffprobe.exe`；许可证与构建配置位于 `tools\ffmpeg\LICENSES`。
- Release 构建会自动复制该目录。正常安装无需下载或解压；若文件被删除，页面提示修复或重新安装 X-Tool。
- 已用短 WAV/MP4 实测探测及 MP3、HEVC、Opus、GIF 输出。仍应在发布前补齐不同来源、较长媒体和取消流程的人工回归。

发布到 GitHub 前，必须将项目许可证确定为 GPLv3，并在 Release 中提供与内置二进制精确对应的 FFmpeg 及启用外部库源码、构建配置与许可证材料。详见根目录 `THIRD-PARTY-NOTICES.md`。

### 5. 转 PDF（已实现基础功能）

入口：转换器工作台左侧第四个“转 PDF”图标，页面为
`ScreenshotApp\\Converters\\PdfConverterView.xaml(.cs)`。

已实现：

- 输入：PNG/JPG/JPEG/BMP/GIF/TIFF/WebP、已有 PDF、Word、Excel、PowerPoint、ODF 文档。
- 图片与已有 PDF 由 X-Tool 在本地生成或合并，不依赖办公软件。
- 输出支持按队列合并为一个 PDF，或按文件分别输出；同名输出会自动避让。
- 办公文档依次尝试 Microsoft Office COM、WPS COM、LibreOffice 无界面转换。
- 页面显示三类引擎的实时检测状态；若三者均缺失，仍允许图片/PDF 工作，并明确提示安装 LibreOffice 后再转换办公文档，不提供下载入口。
- 失败原因显示在队列对应文件项，取消会停止后续项目；LibreOffice 运行中的进程可被取消。

依赖：NuGet `PDFsharp` 6.2.4（MIT），已记录在根目录 `THIRD-PARTY-NOTICES.md`。

当前本机验证：Release 构建通过，PDF 页面与 Office/WPS/LibreOffice 检测提示已实际显示；本机检测到 Office，未检测到 WPS、LibreOffice。尚未在本机以真实办公文件完成 Office/WPS/LibreOffice 端到端导出，后续应分别补测 Word、Excel、PowerPoint 与 LibreOffice 回退。

## 七、关键代码地图

| 模块 | 位置 |
| --- | --- |
| 主导航、快捷键、历史、图片转换 | `ScreenshotApp\MainWindow.xaml(.cs)` |
| 用户设置 | `ScreenshotApp\Settings\AppPreferences.cs` |
| 普通截图与标注工具栏 | `ScreenshotApp\Capture\SelectionOverlayWindow.xaml(.cs)` |
| 长截图 | `ScreenshotApp\Capture\ScrollCaptureService.cs` 及相关窗口 |
| 贴图 | `ScreenshotApp\Sticker\StickerWindow.cs` |
| 剪贴板服务与浮窗 | `ScreenshotApp\Clipboard\` |
| 历史/剪贴板持久化 | `ScreenshotApp\History\` |
| OCR | `ScreenshotApp\Ocr\` |
| 翻译 | `ScreenshotApp\Translation\` |
| 屏幕录像 | `ScreenshotApp\Recording\` |
| 转换器二级导航 | `ScreenshotApp\Converters\ConverterToolRail.xaml(.cs)` |
| 图片转换引擎 | `ScreenshotApp\Converters\ImageConversionService.cs` |
| 音频页面 | `ScreenshotApp\Converters\AudioConverterView.xaml(.cs)` |
| 视频页面 | `ScreenshotApp\Converters\VideoConverterView.xaml(.cs)` |
| FFmpeg/ffprobe 服务 | `ScreenshotApp\Converters\MediaConversionService.cs` |
| 音视频队列模型 | `ScreenshotApp\Converters\MediaQueueItem.cs` |
| PDF 页面与任务队列 | `ScreenshotApp\Converters\PdfConverterView.xaml(.cs)`、`PdfQueueItem.cs` |
| PDF 转换与引擎回退 | `ScreenshotApp\Converters\PdfConversionService.cs` |
| Win32 接口 | `ScreenshotApp\Capture\NativeMethods.cs` |

## 八、当前已知限制与建议顺序

1. **音视频人工回归**：已内置 FFmpeg；发布前用短 MP3/WAV/MP4/MOV 分别验证探测、进度、取消、输出与日志。
2. **FFmpeg 源码发布材料**：GitHub Release 必须同步发布与内置二进制一致的源码、构建配置和许可证材料。
3. **图片预览反馈增强**：预览已实时生成，但尺寸/质量变化不总是肉眼明显；可显示实际输出像素与预计大小。
4. **配置目录迁移**：`%LocalAppData%\JieYing` 是遗留目录，只能在有兼容迁移方案时更名。
5. **回归保护**：继续确保贴图、截图辅助窗、录像边框和控制条不进入捕获画面。
6. **转 PDF 人工回归**：分别验证图片合并、已有 PDF 合并、Word/Excel/PowerPoint 的 Office 导出，以及未装 Office/WPS 时 LibreOffice 回退和缺失提示。

## 九、最近关键提交

- `8703c04` 优化音频转换页面样式与信息布局
- `afad7a7` 新增音视频转换工作台
- `99c0938` 更新 X-Tool 桌面图标
- `c45200b` 校正图片列表格式与大小表头位置
- `b6780ea` 修正图片处理表头与预览反馈
- `0a45acd` 实现图片处理实时预览与列对齐
- `8d7d64a` 新增转换器工作台与图片处理
- `6bf7d07` 修正剪贴板分类配色映射
- `d1e366a` 修复剪贴板粘贴与分类展示
- `cc788dd` 新增剪贴板管理与快捷粘贴
- `0453065` 新增普通截图贴图功能

## 十、新窗口开始前检查清单

1. 完整阅读本文件。
2. 执行 `git status --short`，确认基线和用户未提交内容。
3. 保留 `ClipboardDiagnostics/` 未跟踪且不提交。
4. 检查是否已有 `XTool.exe` 进程；构建前关闭。
5. 只处理新任务涉及的文件。
6. Release 构建通过后启动新版，进行实际 UI/功能验证。
7. 创建中文本地提交；不推送远端，除非用户明确要求。

## 十一、可直接用于新窗口的提示词

```text
请继续开发 D:\Claude Code\X-Tool 的 X-Tool WPF 项目。

先完整阅读项目根目录 HANDOFF.md，并严格遵循其中记录的当前基线、UTF-8 文件处理要求、构建与启动方式、提交约定、工作区保护规则和各模块现状。当前 main 分支应至少包含功能代码基线 8703c04，其后可能只有交接文档提交。开始前先执行 git status --short；ClipboardDiagnostics/ 是未跟踪的临时诊断目录，必须保留且不得提交。未经我明确要求不要推送 GitHub。

当前任务：继续完善转换器工作台。先核对最新音频处理页的实际显示，再参照音频页和图片页现有毛玻璃设计语言，统一视频处理页的文件拖入区、文件列表列布局、模式选择、输出格式下拉框、质量/高级参数卡片、输出目录和底部操作按钮。不得破坏现有转换、压缩、提取音频、视频转 GIF、实时进度、取消、失败原因和日志逻辑。同时确认 FFmpeg 缺失时提示清晰；如果本机仍没有 ffmpeg.exe/ffprobe.exe，不要伪造端到端测试结果，只完成可验证的 UI 与非引擎逻辑测试，并在交付时明确说明。

完成后关闭旧进程，构建 Release，启动 ScreenshotApp\bin\Release\net6.0-windows\XTool.exe 验证新版，然后创建中文本地 Git 提交。不要提交 ClipboardDiagnostics/，不要推送远端。
```
