# 智能家居界面设计 QA

- Source visual truth: `C:\Windows\TEMP\codex-clipboard-c8f2823a-b790-4ca7-befe-1158c4a5db2b.jpg`
- Implementation main screenshot: `C:\Windows\Temp\xtool-smart-home-redesign-final-main.png`
- Implementation detail screenshot: `C:\Windows\Temp\xtool-smart-home-redesign-final-detail.png`
- Viewport: X-Tool 桌面窗口，1480 × 900 logical px，100% 捕获比例
- Source pixels: 1260 × 4904；手机长屏参考，仅用于信息层级、卡片节奏和状态表达，不做桌面端 1:1 尺寸复刻
- Implementation pixels: 1480 × 900；桌面端原始窗口捕获，无缩放
- State: Home Assistant 已连接；默认“在线”分类；6 台在线设备；空调详情弹层

## Full-view comparison evidence

- 信息架构延续参考图的“分类在上、设备卡片在下”，并按桌面横向空间改为四列自适应毛玻璃网格。
- 在线、离线、全部与房间保持同一筛选层级；首次进入选中在线，离线设备不干扰日常控制。
- 摄像机大画面按用户要求不复刻；摄像机保留普通设备卡片，且不展示含义不明确的外层电源开关。
- 主卡片只展示图标、名称、状态、房间、快捷电源和详情入口；温度、风速、摆风等详细控制进入独立弹层。

## Focused region comparison evidence

- 设备网格：参考图中的大圆角、轻边界、状态化电源圆钮和设备图标已映射到 X-Tool 浅色毛玻璃语言；长设备名使用单行省略，未破坏卡片高度。
- 空调详情：居中 680 px 弹层完整展示目标温度、运行模式、空调风速和摆风方式；背景遮罩、层级和关闭路径清晰。
- 字体与排版：沿用项目 Segoe UI/系统中文字体，标题、设备名、状态和辅助信息形成 4 级层级；未发现裁切或重叠。
- 色彩与令牌：蓝色用于选择状态，绿色用于在线和已开启，灰色用于关闭或离线；对比度和禁用态可辨识。
- 图标与资产：使用 Windows Segoe Fluent Icons 图标库，并优先根据 Home Assistant 图标、设备类别、型号和名称匹配；没有使用表情、占位图或手工 SVG。
- 文案与内容：过滤了 Backup、Sun 等系统实体；卡片内容仅保留实际家居设备。摄像机状态改为“在线”，避免将指示灯实体误写成摄像机开关。

## Findings and iteration history

### Iteration 1

- [P0] 详情弹层首次打开导致进程退出。
  - Evidence: Windows `.NET Runtime` 事件显示 `Run.Text` 尝试对只读 `StatusText` 建立双向绑定。
  - Fix: `StatusText` 与 `AreaName` 显式改为 `Mode=OneWay`，并重新构建、打开、关闭弹层验证。
- [P2] “详情”文字点击目标不稳定。
  - Fix: 改为有自动化名称和键盘语义的真实按钮，同时保留卡片空白区域点击入口。
- [P2] Backup、Sun 等系统实体占用设备卡片；摄像机错误出现指示灯电源开关。
  - Fix: 按设备元数据和传感器类别过滤系统实体；摄像机语义优先，隐藏不明确的快捷控制。

### Iteration 2

- Post-fix evidence: `xtool-smart-home-redesign-final-main.png` 显示在线 6、离线 3、全部 9，主网格无系统实体；摄像机无电源圆钮。
- Post-fix evidence: `xtool-smart-home-redesign-final-detail.png` 显示空调详情稳定打开，温度、模式、风速与摆风控件完整。
- Primary interactions tested: 进入智能家居、切换在线/离线、打开空调详情、按 Esc 关闭详情。
- Device-control safety: 验收未点击电源、温度增减、模式、风速或摆风控件，未向真实设备发送控制命令。
- Console/runtime errors: 修复后未出现新的 `.NET Runtime` 未处理异常；Release 构建 0 warnings / 0 errors。

## Remaining P3 polish

- 当前设备图标来自 Fluent 图标库而非厂商提供的产品渲染图；若 Home Assistant 后续稳定提供可鉴权的本地图像资源，可再增加原生产品缩略图回退链。
- Home Assistant 当前温度单位为华氏度，因此空调与温湿度计按真实配置显示 °F。

final result: passed
