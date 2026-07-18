# 截影（X-Tool）开发交接文档

更新时间：2026-07-18  
项目目录：`D:\Claude Code\X-Tool`  
当前分支：`main`  
当前基线提交：`507cec9 为录制历史生成视频封面`

## 一、运行与提交约定

- 程序入口：`ScreenshotApp\bin\Release\net6.0-windows\JieYing.exe`。
- 构建命令：`dotnet build ScreenshotApp\ScreenshotApp.csproj -c Release`。
- 当前程序通常会锁定 Release 可执行文件；重新构建前先关闭 `JieYing.exe` 进程。
- 大范围功能改动完成并通过构建后，创建中文本地 Git 提交；只有用户明确要求时才推送远端。
- 源文件使用 UTF-8，代码注释使用中文。

## 二、当前已完成能力

### 截图与长截图

- 唯一全局热键：`Ctrl + Shift + A`。
- 普通截图支持框选、八方向调整选区、涂鸦、矩形、取色器、OCR、离线英译中、长截图入口、录像入口。
- 长截图由普通截图工具栏的“长截图”进入，使用当前已框选区域，手动滚动并处理重复帧与回滚。
- 截图和长截图默认复制到剪贴板并保存至 `E:\截影\Screenshots`。

### OCR 与离线翻译

- OCR：PaddleOCR ONNX + ONNX Runtime CPU，本机处理。
- 翻译：本地 ONNX 英译中模型。
- OCR 与翻译结果会分别保存至：
  - `E:\截影\History\文字提取`
  - `E:\截影\History\翻译`

### 屏幕录像

- 入口：普通截图框选后，工具栏“录像”。
- 二级面板位于工具栏下方，横向显示“电脑声音”“麦克风”“开始录像”。
- 视频：Media Foundation H.264 MP4；声音：WASAPI 采集并混音为 AAC。
- 文件目录：`E:\截影\Recordings`。
- 每次新录像会写入首帧封面：`E:\截影\Recordings\Covers\同名视频.png`，历史记录优先读取该封面。
- 开始录制前有 3 秒倒计时；录制期间显示红色区域边框和控制条，辅助窗口通过 `WdaExcludeFromCapture` 排除在录制画面外。
- 最近已修复：视频画面上下颠倒、区域边框高 DPI 裁切、控制条时间对齐。建议后续仍手动录制一段短视频，确认边框完整且不写入 MP4。

### 历史记录与主界面

- 左侧一级导航：截图、首页、历史记录、快捷键、设置。
- 已移除冗余的固定二级侧栏和主内容顶部无功能按钮。
- 历史记录支持筛选：普通截图、长截图、文字提取、翻译、屏幕录制。
- 图片和录像使用缩略图卡片；文字提取、翻译采用满宽横条，展示摘要、时间、字符数，点击使用系统默认程序打开完整文本。
- 旧录像没有首帧封面时使用图标兜底；新录像自动生成封面。

## 三、关键代码位置

| 模块 | 文件 |
| --- | --- |
| 主流程、热键、历史筛选 | `ScreenshotApp\MainWindow.xaml.cs` |
| 主界面与历史页样式 | `ScreenshotApp\MainWindow.xaml` |
| 普通截图选区与工具栏 | `ScreenshotApp\Capture\SelectionOverlayWindow.xaml(.cs)` |
| 长截图逻辑 | `ScreenshotApp\Capture\ScrollCaptureService.cs` |
| 历史保存与加载 | `ScreenshotApp\History\ScreenshotHistoryStore.cs` |
| 录像服务/编码/音频/封面 | `ScreenshotApp\Recording\ScreenRecordingService.cs` |
| 录像区域框、倒计时、控制条 | `ScreenshotApp\Recording\ScreenRecording*Window.cs` |

## 四、下一步：贴图功能（待实现）

### 目标交互

1. 用户完成普通截图框选后，在工具栏新增“贴图”。
2. 点击后，将当前选区图像作为独立的可拖动图片显示在桌面上。
3. 图片右上角提供：
   - `×`：关闭该贴图。
   - `图钉`：切换是否置顶；置顶时始终位于其他窗口之上。
4. 支持同时存在多个贴图。

### 推荐技术方案

- 新建 `ScreenshotApp\Sticker\StickerWindow.cs`，使用无边框、透明背景的 WPF `Window`。
- 输入图片直接复用 `SelectionOverlayWindow.CreateSelectionBitmap(...)` 或在普通截图确认前传递已经冻结的 `BitmapSource`，避免二次截屏。
- 窗口主体使用 `Border + Image + 顶部悬浮操作区`：
  - 图片区域拖动时调用 `DragMove()` 或使用 Pointer/Mouse 逻辑移动窗口。
  - 关闭按钮调用 `Close()`。
  - 图钉按钮切换 `Topmost` 并切换视觉状态。
- 建议默认 `Topmost = false`，只有用户点图钉后才置顶。
- 对贴图窗口调用 `NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture)`，避免之后截图或录像把贴图本身录进去。
- 第一版不要持久化贴图；后续可扩展缩放、透明度、锁定位置、贴图列表与重启恢复。

### 边界说明

- “窗口始终最上层”可由标准 `Topmost` 稳定实现。
- 如果需求变为“固定在桌面底层、位于所有应用窗口下方”，则需与 Windows `WorkerW` 桌面层交互，兼容性和多显示器行为明显更复杂，不建议作为第一版目标。

## 五、最近提交

- `507cec9` 为录制历史生成视频封面
- `faaff9c` 修正录制区域标识与控制条对齐
- `d258441` 修复录制画面方向并增加倒计时提示
- `6207da6` 优化文本历史记录展示
- `396533c` 扩展统一历史记录分类
- `6194622` 优化录像设置面板并接入本地音频录制

