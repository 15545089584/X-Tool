# X-Tool 开发交接文档

更新时间：2026-07-28

项目目录：`D:\Claude Code\X-Tool`

技术栈：WPF / C# / .NET 6（`net6.0-windows`）

当前分支：`main`

当前功能代码基线：`ae31d5a 移除系统工具启动项管理`；本轮“设备信息、驱动清单与存储占位页”修改已完成，以最新本地提交和 `git log --oneline` 为准（网络监测核心基线 `0588227` 与此前 `137b912` 均已包含在历史中）。

> 自旧基线 `8703c04` 之后，已经完成转换器 PDF/编码转换、离线语音输入、文件工作台、开机自启动、首页重设计和系统工具等多轮功能开发；开始下一轮前请以本文件与 `git log --oneline` 为准，切勿误以为只有交接文档发生变化。

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
- 当前未跟踪目录 `ScreenshotApp/VoiceInput/Runtime/` 是此前 GPU 语音加速试验留下的本地运行时材料；GPU 方案已回退到 CPU 版，不得提交、删除或重新接入该目录，除非用户明确要求。

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
4. 文件工作台
5. 网络工作台
6. 资源管理
7. 系统工具
8. 快捷键
9. 设置

剪贴板不再是一级侧栏项，而是屏幕工作台的组成部分；首页与屏幕工作台均可进入剪贴板。

### 文件工作台（已实现第一版）

- 入口：一级侧栏“文件工作台”。
- 指定文件夹后可按文件名、类型、最小/最大大小和修改时间搜索；搜索会跳过无权限目录与链接目录。
- 可对选中文件（未选择时为当前结果全部）生成批量重命名、修改后缀、按类型分类、批量移动预览；确认后才执行。
- 重命名采用“前缀_编号”格式，自动避让已存在文件；分类目录为图片、视频、音频、文档、压缩包、其他。
- 当前为文件夹内的异步本地扫描，不是 Windows/Everything 的全盘常驻索引；全盘秒级搜索需后续接入 Windows Search 或 Everything 索引。
- 文件搜索已改为关键词、类型、修改时间和单一排序规则（名称、大小、修改时间；可切换升/降序），不再使用容易误解的最小/最大文件大小输入框。
- 首页已移除“最近使用”占位区域，改为当前主能力入口卡片。

### 资源管理与系统工具（已拆分）

- 左侧原“系统工具”已更名为“资源管理”，图标改为资源/性能样式，承载端口查看器、进程管理、服务管理、关联关系四个页面。
- 新增的“系统工具”保留原名称与芯片图标，承载“设备信息”“存储”“环境变量”三个页面；入口默认打开设备信息页，不再与资源管理功能混在一起。
- 两个入口复用 `ScreenshotApp\SystemTools\SystemToolsView.xaml(.cs)`；资源管理使用默认模式，系统工具使用 `EnvironmentOnly=True`。修改时不要破坏两种入口各自的可见标签和默认页面。
- 设备信息页使用 Windows 公开只读接口显示静态系统与硬件规格（系统、处理器、显卡、主板、硬盘、显示器、内存、电池），无需管理员权限；下方独立列出 Windows 已识别的 PnP 驱动。驱动数据来自 `Win32_PnPSignedDriver` 与 `Win32_PnPEntity`，显示设备类别、提供商、版本、日期、INF、签名和设备状态，默认“关键设备”，可切换“全部设备”或“异常项”并搜索。蓝牙、USB、虚拟设备等不强行映射到上方硬件摘要。该清单不等同于所有内核/服务/筛选驱动或固件。
- “存储”页面当前仅保留浅色毛玻璃占位状态，尚未接入分区、可用空间或存储健康数据。
- 实时温度、电压、频率、负载、功耗，以及创建或调用管理员传感器助手、计划任务的代码和 LibreHardwareMonitor 依赖均已移除；除非用户明确重新立项，不得恢复该链路。历史版本若已注册按需任务，新版本不会启动它；可在管理员权限下从任务计划程序删除该旧任务。
- 启动项管理页面，以及其启动项扫描、图标提取、启停、定位和管理员启动项操作代码均已移除。设置页中“X-Tool 自身开机启动”是独立功能，仍保留。
- 端口查看器：解析 `netstat -ano`，显示协议、本地地址、端口、占用进程、PID、状态；可按各表头点击排序，支持进程目录/结束进程右键操作。
- 端口页有关键词搜索、“显示系统进程”、“显示 IPv6”筛选；本地地址会截断显示并保留悬停文本，避免与端口列粘连。
- 进程管理：按同名进程折叠为应用组，展开后显示子进程；单进程应用会显示真实 PID；支持 CPU、内存、磁盘、网络和启动时间展示与表头排序。CPU 使用连续采样差分，不应长期固定为 0；进程结束操作会按需请求管理员权限。
- 服务管理：读取 Windows 服务，每行根据当前状态只显示“启动”或“停止”一个操作按钮；执行前确认，普通权限不足时通过一次性管理员子进程操作并返回真实结果。
- 关联关系：以运行进程为中心汇总 PID、端口、服务、网络活动、CPU 和内存；点击行后在相邻位置展开毛玻璃关系气泡，不替代端口、进程或服务专业页面。
- 环境变量：支持用户/系统范围查看、搜索与真实保存；`Path` 进入独立的分项编辑器，可新增、选择文件夹、移动、删除和保存。系统变量写入按需请求管理员权限。
- “自动刷新”已提升到系统工具页签右侧，是端口、进程、服务三个列表的共用开关，可选 3/5/10/30 秒；仅刷新当前可见页面。环境变量页不自动刷新，避免覆盖正在编辑的内容。
- 进程路径、部分启动时间或受保护进程的精确信息可能受 Windows 权限限制；列表仍应显示可获取的 PID、内存等基础信息。

### 网络工作台（第三阶段已完成）

- 一级导航“网络工作台”现包含网络总览、网卡与路由、代理与 DNS、Wi-Fi、网络方案五个页面；整体沿用浅色毛玻璃设计。网络总览内另保留独立弹出的“连接诊断”窗口。
- 网络总览使用 Windows 默认路由选择主用物理网卡，以默认网关、DNS、HTTP 三层探测判断联网状态；HTTP 会并行尝试直连与当前用户代理/系统网络路径，任一路径成功即视为可联网，并在详情中标明实际成功路径，避免启用代理时把“直连失败、代理可用”误判为互联网受限。网络变化事件会使缓存失效并刷新。曲线支持实时、15 分钟、1 小时、24 小时、7 天、30 天和 90 天，显示坐标、平均值、峰值和累计流量；实时曲线在新峰值出现时立即扩展纵轴，旧峰值移出窗口后平滑收缩，避免整图跳变。
- `NetworkMonitorCoordinator` 统一管理自动刷新、网络变化事件、即时刷新、速率差分、持久化和告警；监测器在网络工作台首次加载后贯穿应用生命周期持续运行，切走页面不会暂停采集、历史落盘或事件记录，返回时直接渲染已累积的最新状态与曲线。手动与自动刷新共用单飞门，避免重复探测或 UI 卡死。
- 最新两项网络修复：`137b912` 取消切页时停止监测器、隐藏页面时跳过样本累积的问题；`0588227` 为实时曲线采用“新峰值立即扩展、旧峰值平滑回落”的纵轴策略，避免 60 秒窗口内峰值移出时整图突变。后续改动不得恢复“每次按当前窗口最大值硬重标尺”的实现。
- `NetworkHistoryStore` 使用本地 SQLite、WAL 和单写入队列保存分钟流量、网关/DNS/HTTP 探测及网络事件；数据库位于 `%LocalAppData%\X-Tool\Network\network-history.db`。保留期可选 1、7、30、90 天，支持二次确认清除和 CSV 历史导出。
- 网络事件覆盖监测启动、断网/恢复、物理链路与主用网卡变化、代理/DNS 变化、Wi-Fi 漫游、DNS 连续失败、HTTP 高延迟、持续高上传和每日流量预算；阈值、预算与静默时段可配置，并采用连续命中与 10 分钟去重。
- 网络总览新增毛玻璃“连接路径”示意：按本机、默认网关、已配置代理路径、DNS/互联网、目标服务展示状态、速率与延迟；代理不可作为有效路径时会保留节点并标注直连/代理实际探测结果，不将代理配置本身误当作流量必经路径。右上“异常记录”弹出独立毛玻璃窗口，汇总中断、连续探测失败和高延迟等记录。
- 网络总览的“连接诊断”按钮会独立打开 `NetworkDiagnosticsWindow`，支持 Ping、DNS、TCP、HTTP、路由追踪和可取消的完整诊断；完整诊断覆盖默认路由、DNS、目标 TCP/HTTP 与代理，可导出 UTF-8 报告。耗时操作不得在 UI 线程同步等待。
- 顶部旧“连接诊断”和“流量与连接”页面已移除；旧 ETW 精确流量入口不再在主程序 UI 中触发，相关代码保留为禁用状态。除非用户明确要求，不要重新启用 GPU 语音运行时或恢复 ETW 入口。
- 代理与 DNS 支持当前用户代理、PAC、WinHTTP 与主用网卡 DNS 的读取和管理；机器级操作通过一次性 UAC 子进程完成，不要求主程序长期管理员运行。
- “网卡与路由”页面在顶部展示物理/虚拟网络接口、地址、网关、DNS、MTU 与接口跃点；下方保持 IPv4/IPv6 路由表和 Windows 防火墙配置文件左右各半的只读布局。网卡写操作均有影响提示、确认和错误反馈。
- Wi-Fi 已作为独立页面，展示当前摘要、按信号排序的可用 Wi-Fi 列表、所选网络的可读取属性，以及全宽的 2.4 GHz/5 GHz 信道分布图。未连接网络只展示扫描可得的 SSID、BSSID、信号、协议/安全、频段与信道；IP、DNS、网关、链路速率等仅连接后可读取。信道图可切换频段，显示名称、图例和坐标，避免恢复为拥挤的星型图。
- 网络方案支持代理、DNS、DHCP/静态 IPv4、前缀、MTU 和接口跃点快照；字段可选应用，网卡不匹配时阻止执行，应用后自动探测并可立即回滚。恢复点跨重启保留 24 小时，支持不含凭据的 UTF-8 JSON 导入导出。
- ETW 依赖 NuGet `Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.21，以兼容当前 .NET 6 目标。完整实现与验收边界见根目录 `NETWORK-WORKBENCH-PHASE3.md`。

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

转换器工作台拥有独立的竖向二级工具栏，目前包含图片、音频、视频、转 PDF 和编码转换五个图标入口。顶部始终保留“转换器工作台”标题与介绍；选择工具只替换右侧工作区。

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

### 6. 编码转换（已实现）

入口：转换器工作台左侧第五个“编码转换”图标，页面为
`ScreenshotApp\Converters\EncodingConverterView.xaml(.cs)`。

全部能力均为离线本地处理：Base64、URL Encode/Decode、Unicode 编码/解码、JWT Header/Payload 只读解析、Unix 秒/毫秒时间戳与日期时间互转、UUID v4 批量生成（1-100 个）。JWT 不上传内容且不验证签名；解析结果会明确显示这一点。

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
| 编码转换 | `ScreenshotApp\Converters\EncodingConverterView.xaml(.cs)`、`EncodingConversionService.cs` |
| 文件工作台 | `ScreenshotApp\FileWorkbench\FileWorkbenchView.xaml(.cs)`、`FileWorkbenchService.cs` |
| 系统工具（设备信息、PnP 驱动清单、存储占位、环境变量） | `ScreenshotApp\SystemTools\SystemToolsView.xaml(.cs)`、`SystemToolsService.cs` |
| 网络工作台主页面 | `ScreenshotApp\NetworkWorkbench\NetworkWorkbenchView.xaml(.cs)` |
| 网络状态与方案服务 | `ScreenshotApp\NetworkWorkbench\NetworkWorkbenchService.cs` |
| 统一监测与历史库 | `ScreenshotApp\NetworkWorkbench\NetworkMonitorCoordinator.cs`、`NetworkHistoryStore.cs` |
| ETW 精确流量 | `ScreenshotApp\NetworkWorkbench\NetworkEtwTrafficCollector.cs` |
| 网络告警配置 | `ScreenshotApp\NetworkWorkbench\NetworkAlertSettingsWindow.xaml(.cs)` |
| 网络深度诊断与 Wi-Fi | `ScreenshotApp\NetworkWorkbench\NetworkWorkbenchView.xaml(.cs)`、`NetworkDeepToolsService.cs` |
| Win32 接口 | `ScreenshotApp\Capture\NativeMethods.cs` |

## 八、当前已知限制与建议顺序

1. **网络第三阶段人工矩阵**：代码与普通权限 UI 已完成并通过 Release 启动验证；发布前仍应在真实 UAC 环境分别验证代理/DNS/网卡配置、网络方案应用失败后的回滚、局域网扫描取消，以及断网/切网/休眠恢复。顶部 ETW 入口当前已移除，不得通过自动化静默接受 UAC 或擅自恢复该入口。
2. **音视频人工回归与视频页统一**：已内置 FFmpeg；发布前用短 MP3/WAV/MP4/MOV 分别验证探测、进度、取消、输出与日志。视频功能逻辑已接通，视觉控件仍可继续向最新音频页统一。
3. **FFmpeg 源码发布材料**：GitHub Release 必须同步发布与内置二进制一致的源码、构建配置和许可证材料。
4. **图片预览反馈增强**：预览已实时生成，但尺寸/质量变化不总是肉眼明显；可显示实际输出像素与预计大小。
5. **配置目录迁移**：`%LocalAppData%\JieYing` 是遗留目录，只能在有兼容迁移方案时更名。
6. **回归保护**：继续确保贴图、截图辅助窗、录像边框和控制条不进入捕获画面。
7. **转 PDF 人工回归**：分别验证图片合并、已有 PDF 合并、Word/Excel/PowerPoint 的 Office 导出，以及未装 Office/WPS 时 LibreOffice 回退和缺失提示。
8. **资源管理与系统工具权限/性能回归**：受保护进程、服务启动/停止和系统环境变量写入需分别在普通权限与管理员权限下验证；端口自动刷新不应造成页面卡顿，设备与 PnP 驱动信息应能在普通权限下刷新，且需确认资源管理与系统工具入口没有串页。

## 九、最近关键提交

- `ae31d5a` 移除系统工具启动项管理
- `c74d231` 重构启动项管理界面
- `9cd7341` 放大硬件信息清单
- `7c7a3e4` 放大硬件信息标识
- `1891ebc` 移除硬件实时监控助手
- `bbfdff4` 拆分资源管理与系统工具入口
- `e54e456` 优化网卡区域与关联排序
- `0674edc` 合并网卡与路由页面
- `3e99d60` 重构网络工作台顶部页面
- `5df6b63` 在总览新增连接诊断弹窗
- `0588227` 平滑实时流量曲线缩放
- `137b912` 修复网络监测切页冻结
- `81cb4da` 回退 SenseVoice CUDA GPU 加速，当前语音输入为稳定 CPU 方案
- `c414fde` 完成网络工作台第三阶段与按钮样式优化
- `654a406` 完成网络工作台历史监测底座
- `4969e45` 修复网络曲线与完整诊断卡死
- `63f46c9` 完成网络工作台第二阶段并规划第三阶段
- `1c61ce6` 启动网络工作台第二阶段采集升级
- `bd1eb0f` 修复网络状态误判与刷新延迟
- `a099b0f` 完成网络工作台第一阶段
- `5d713f0` 统一系统工具表头排序与自动刷新
- `e049905` 优化端口列距与进程信息展示
- `2212bcf` 完善端口查看筛选与自动刷新
- `aee3829` 完善端口列表字段与表头排序
- `6943b75` 优化进程管理分组展示
- `d03e8b8` 新增 Path 环境变量分项编辑
- `e817c69` 修复系统工具排序方向图标
- `55a4c7d` 统一系统工具表头与滚动条样式
- `fea7212` 完善系统工具列表筛选与排序
- `73a952b` 新增系统工具模块
- `215376f` 重新设计首页功能入口
- `7eaf204` 新增开机自启动设置

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
4. 保留 `ScreenshotApp/VoiceInput/Runtime/` 未跟踪且不提交、不删除。
5. 检查是否已有 `XTool.exe` 进程；构建前关闭。
6. 只处理新任务涉及的文件。
7. Release 构建通过后启动新版，进行实际 UI/功能验证。
8. 创建中文本地提交；不推送远端，除非用户明确要求。

## 十一、可直接用于新窗口的提示词

```text
请继续开发 D:\Claude Code\X-Tool 的 X-Tool WPF 项目。

先完整阅读项目根目录 HANDOFF.md，并严格遵循其中记录的当前基线、UTF-8 文件处理要求、构建与启动方式、提交约定、工作区保护规则和各模块现状。当前 main 分支应包含功能代码提交 bbfdff4（以及 e54e456、0674edc、3e99d60、5df6b63）和随后新增的交接文档提交。开始前先执行 git status --short 和 git log -5 --oneline；ClipboardDiagnostics/ 与 ScreenshotApp/VoiceInput/Runtime/ 都是未跟踪的本地临时目录，必须保留且不得提交或删除。未经我明确要求不要推送 GitHub。

当前已完成：首页、屏幕工作台、转换器工作台（图片/音频/视频/PDF/编码）、文件工作台、网络工作台第三阶段、资源管理、系统工具、快捷键和设置。网络工作台当前顶栏为网络总览、网卡与路由、代理与 DNS、Wi-Fi、网络方案；总览内保留独立弹出的连接诊断窗口和连接路径/异常记录。实时曲线在切页期间仍持续采样，并使用“新峰值立即扩展、旧峰值平滑回落”的纵轴缩放，禁止回退成切页停监测或每次硬重标尺。Wi-Fi 页已具备可用网络选择、所选网络属性和可切换 2.4/5 GHz 的信道分布图。资源管理包含端口、进程、服务、关联关系；系统工具包含静态设备信息、Windows PnP 驱动清单、存储占位页与环境变量，且不再包含实时传感器监控、启动项管理或管理员助手。当前语音输入已回退为稳定 CPU 方案，不要重新启用 GPU 运行时；顶部 ETW 精确流量入口已移除，不要擅自恢复。下一项功能由我在后续消息指定。

若需要继续优化 UI，请保持现有浅色毛玻璃设计语言，不要破坏已实现的异步、取消、失败提示、日志、单飞刷新、历史持久化和权限确认逻辑。任何 ETW、系统网络配置、防火墙、服务、受保护进程或系统环境变量写操作都只能在用户明确点击后按需申请管理员权限，不得让主程序默认长期以管理员运行。

完成任务后关闭旧进程，构建 Release，启动 ScreenshotApp\bin\Release\net6.0-windows\XTool.exe 验证新版，然后创建中文本地 Git 提交。不要提交 ClipboardDiagnostics/ 或 ScreenshotApp/VoiceInput/Runtime/，不要推送远端。
```
