# 系统诊断 14 天范围与结果标签设计核验

- Source visual truth paths:
  - `C:\Windows\TEMP\codex-clipboard-f2f77747-12b1-4a7e-9ca1-bed1ee110308.png`
  - `C:\Windows\TEMP\codex-clipboard-e4395342-ec92-425e-bd40-357fa1c450eb.png`
- Implementation screenshot paths:
  - `D:\Claude Code\X-Tool\system-diagnostics-14day-dropdown.jpg`
  - `D:\Claude Code\X-Tool\system-diagnostics-14day-results.jpg`
- Viewport: 1480 × 900 logical pixels
- Source pixels: 246 × 260（下拉局部）、1261 × 933（结果区）
- Implementation pixels: 1480 × 900
- Density normalization: 参考图包含局部裁剪，实际图为完整 X-Tool 窗口；对照时分别聚焦顶部下拉菜单和下方结果标签区域，不比较裁剪外内容。
- State: 系统诊断页展开时间范围下拉框；随后选择“最近 14 天”并完成只读诊断，选中 7/30 整列。

## Full-view comparison evidence

时间范围下拉框现有最近 24 小时、7 天、14 天、30 天四项；运行 14 天诊断后，异常时间格从 7/17 到 7/30 正好呈现 14 个日期列。相邻列继续使用交替浅色玻璃底纹，日期标签与列中心对应。

## Focused region comparison evidence

结果卡片右侧等级标签统一为 86 × 25 的毛玻璃胶囊，描边从 1 提升到 1.5，文字字号从 9 提升到 10.5，并同时设置水平、垂直及文本居中。结果数据新增独立 `SeverityPriority`，实际可访问性树显示列表项携带明确优先级；当前 7/30 没有关键事件，因此错误事件从高频到低频排列在前，警告事件随后显示。

## Findings

未发现阻断本轮交付的 P0、P1 或 P2 问题。

## Required fidelity surfaces

- Fonts and typography: 等级标签字号、字重和居中状态均已增强，不再贴近边缘。
- Spacing and layout rhythm: 胶囊固定宽高，列表与详情头部使用同一尺寸；14 个日期格均匀分布。
- Colors and visual tokens: 延续现有浅色毛玻璃背景和各危险等级语义色，描边使用完整语义色提升对比度。
- Image quality and asset fidelity: 页面没有新增位图 UI 资产；现有 Segoe Fluent Icons 与代码绘制图表保持不变。
- Copy and content: 新增“最近 14 天”，排序说明继续明确为危险等级从高到低。

## Comparison history

1. 参考状态缺少 14 天选项，等级标签字号较小、描边偏浅且视觉居中不足。
2. 新增 14 天范围和统一等级胶囊，并把排序改为不依赖枚举数值的显式优先级。
3. Release 构建通过后实际展开下拉菜单并运行 14 天诊断，确认四个选项、14 个日期列、标签视觉和结果顺序。

## Implementation checklist

- [x] 新增最近 14 天选项
- [x] 24 小时、7 天、14 天、30 天分别生成 24、7、14、30 格
- [x] 结果按关键、错误、警告显式优先级排序
- [x] 同等级按发生次数与最近时间降序
- [x] 放大等级标签并增强描边
- [x] 列表与详情标签文字水平、垂直居中
- [x] Release 构建与实际 14 天 UI 验证通过

final result: passed
