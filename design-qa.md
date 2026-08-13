# 高级硬件监控圆环与温度曲线设计核验

- Source visual truth path: `C:\Windows\TEMP\codex-clipboard-56130795-d1b3-45bd-9888-7d5ce3b082ea.png`
- Implementation screenshot path: `D:\Claude Code\X-Tool\hardware-monitor-redesign.png`
- Viewport: 1707 × 1026 logical pixels，最大化 X-Tool 窗口
- Source pixels: 1320 × 994
- Implementation pixels: 1707 × 1026
- Density normalization: 参考图是完整独立深色监控页，实现是 X-Tool 设备信息页中的监控区；对比时聚焦“五个温度仪表 + 下方 30 分钟曲线”的共同内容结构，不要求照搬外围导航和深色配色。
- State: 硬件代理旧安装与当前版本不一致，监控未启用；验证圆环与曲线的无数据状态。有效温度与曲线数据由用户重新授权后进行实际 UI 验证。

## Full-view comparison evidence

实现保留了参考图上方五个同权重温度仪表、下方温度趋势和同色图例的核心层级。页面采用现有 X-Tool 浅色毛玻璃、蓝灰文字、15–18 像素圆角和轻描边，没有复制参考图的黑色工业面板。最大化窗口下五列仪表与曲线可在同一监控卡内完整阅读。

## Focused region comparison evidence

- 圆环使用 270° 开口和圆角端点，CPU、显卡、内存、主板、磁盘依次使用蓝、紫、绿、橙、青强调色。
- 无有效读数时只绘制中性轨道，中心显示“不受支持 / 不可用”，没有彩色进度或 0°C 假值。
- 曲线区采用 20–100°C 固定温标，按真实采样时间落在最近 30 分钟轴上；缺失读数会断线。
- 图例与圆环颜色一一对应，最大化与 1480 × 900 普通窗口下均未发生重叠或截断。

## Findings

未发现阻断本轮交付的 P0、P1 或 P2 问题。

## Required fidelity surfaces

- Fonts and typography: 继续使用 Microsoft YaHei UI 与现有页面字号层级；温度值为 20 像素半粗体，状态与传感器原因保持次级层级。
- Spacing and layout rhythm: 五列等宽，圆环 112 × 112，卡片间距、圆角和上下区间距与现有 SystemCard 体系一致。
- Colors and visual tokens: 沿用 X-Tool 浅色玻璃背景、蓝灰前景和既有五类强调色；深色参考配色属于有意不采用。
- Image quality and asset fidelity: 页面没有需要栅格化的品牌或插画资产；圆环和曲线使用 WPF 矢量绘制，在窗口缩放下保持清晰。
- Copy and content: 保留代理状态、诊断摘要、只读采样说明；新增正常/温热/偏高/过热与不可用状态，缺失原因明确可读。

## Comparison history

1. 第一版恢复五路曲线并增加圆环后，发现无数据圆环中心仍写“实时”，与“不受支持”语义冲突。
2. 修正为有效温度显示正常/温热/偏高/过热，无数据显示不可用；重新启动最终 Release 后，可访问性树与截图均确认生效。
3. 最终复核发现早期样本按序号铺满曲线会误导 30 分钟跨度，已改为按真实采样时间定位。

## Follow-up polish

- P3：代理重新授权后，可根据真实五路曲线密度再评估线宽和颜色透明度。

final result: passed
