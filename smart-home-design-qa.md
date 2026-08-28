# 智能家居界面设计 QA

- Source visual truth:
  - `C:\Windows\TEMP\codex-clipboard-c8f2823a-b790-4ca7-befe-1158c4a5db2b.jpg`
  - `C:\Windows\TEMP\codex-clipboard-5d94c325-14e3-49d2-a87a-6b09d18d127a.jpg`
  - `C:\Windows\TEMP\codex-clipboard-9dd22207-8957-4749-9cf6-fe92a6429405.jpg`
  - `C:\Windows\TEMP\codex-clipboard-5f1dea52-23fe-48c6-99d4-9687e5b03cec.jpg`
- Implementation evidence:
  - `artifacts/smart-home-main-20260828.png`
  - `artifacts/smart-home-detail-20260828.png`
  - `artifacts/smart-home-insights-20260828.png`
- Combined comparisons:
  - `artifacts/smart-home-main-comparison-20260828.jpg`
  - `artifacts/smart-home-detail-comparison-20260828.jpg`
- Viewport: X-Tool 桌面窗口，1480 × 900 logical px
- State: Home Assistant 已连接；默认“在线”分类；6 台在线设备；空调正在制冷

## Full-view comparison evidence

- 保留米家参考中的“分类在上、设备卡片在下”信息层级，并按桌面宽屏改为四列自适应毛玻璃网格。
- “在线、离线、全部”与房间筛选位于同一行；默认选中“在线”，蓝色选中项文字为白色。
- 主卡片只保留图标、名称、状态、房间、快捷电源和详情入口；摄像机按要求不复刻大画面框体。
- 卡片使用本项目 Fluent 图标与设备语义匹配；未抓取或内置米家专有产品图片。

## Focused region comparison evidence

- 空调详情为 760 px 居中毛玻璃弹层，背景遮罩和层级清楚；长内容在弹层内部滚动，不挤压主页面。
- 温度统一转换为摄氏度；温湿度计主卡片显示 `25.8°C`，空调目标温度按 `0.5°C` 步进显示。
- 运行模式使用状态胶囊；风速、摆风和风向使用项目自定义圆角下拉模板，不再暴露 Windows 原生下拉外观。
- 扩展功能按 Home Assistant 实际实体动态呈现，包括灯光、节能、辅热、干燥、睡眠、防直吹、喜好与提示音；不可用能力不会伪造。
- 室内温度趋势使用 Home Assistant 最近 24 小时历史记录；用电区展示统计接口返回的今日和本月用电。

## Findings and iteration history

### Iteration 1

- [P0] 详情弹层首次打开导致进程退出。
  - Fix: 将只读属性绑定改为单向绑定，并验证打开、滚动和关闭均不再退出。
- [P1] 调节温度通过 REST 路径时显示“无法访问 Home Assistant”。
  - Fix: 控制调用优先复用已认证 WebSocket `call_service`，REST 仅作为回退，并保留具体服务端错误。
- [P1] Home Assistant 使用华氏单位，界面直接显示 `79°F`。
  - Fix: 展示层统一转换摄氏度，发送控制时再转换回 Home Assistant 源单位。

### Iteration 2

- [P1] 原生 ComboBox 将选项对象显示成类型字符串。
  - Fix: 增加显示值转换器，自定义下拉框稳定显示“二档”“关闭摆风”等业务文案。
- [P1] 能耗实体 REST 历史为空，日/月统计无数据。
  - Fix: 优先读取 Recorder 长期统计 `change`，回退到普通历史；实机读取到今日 `1.79 kWh`、本月 `1.79 kWh`。
- [P2] 温湿度传感器卡片仍显示华氏度。
  - Fix: 识别温度设备类和单位，并在卡片层转换为摄氏度。
- [P2] 选中筛选项的文字仍为深色。
  - Fix: 显式绑定到 `ListBoxItem.Foreground`，实机确认蓝色“在线”筛选项文字为白色。

## Verification

- Release build: 0 warnings / 0 errors。
- Primary interactions: 进入智能家居、打开空调详情、展开自定义风速下拉、滚动查看扩展功能、温度趋势和用电统计。
- Real control path: 将目标温度从 `26°C` 调到 `26.5°C`，界面与 Home Assistant 同步成功且无错误；随后恢复到 `26°C`。
- Stability: 上述操作后 X-Tool 进程保持运行，未复现卡片详情闪退。
- Visual QA: 已将参考图与实现截图组合在同一画面逐项比较，未发现裁切、重叠、错误单位或原生下拉样式回退。

## Remaining P3 polish

- 官方 Xiaomi Home 集成当前未为这些实体提供 `entity_picture`，因此继续使用本地语义图标；未来若 Home Assistant 正式提供可鉴权的设备图片，可在不调用私有米家 API 的前提下作为优先来源。
- 日/月用电取决于 Home Assistant Recorder 是否为对应能耗实体生成长期统计；没有统计时界面会明确显示“暂无记录”。

final result: passed
