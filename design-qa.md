# Design QA

## Source visual truth

- 用户标注截图：`C:\Windows\TEMP\codex-clipboard-ba1be12e-8995-4c41-8acb-a64abf378b58.png`（2228 × 1362）。
- 红框指出卡片右上角能力标签偏小，以及 GitHub 的“剩余 16 个”提示需要移除；红框本身不属于目标界面。

## Implementation evidence

- 调整后七卡页面：`C:\Windows\TEMP\XToolInformationVaultBadgeValidation-20260827\information-vault-unlocked.png`（1480 × 900，96 DPI）。
- 同屏对照：`C:\Windows\TEMP\XToolInformationVaultBadgeValidation-20260827\qa-badges.png`（2048 × 720）。
- 实现使用隔离验证数据，账号内容与源截图不同，但卡片网格、能力标签、GitHub 二次验证状态和目标区域一致。

## Viewport and normalization

- 源图与实现图分别等比缩放到 1000 × 720 白色画布后左右并排，没有拉伸。
- 重点状态：三列卡片网格、自动填入/安全复制标签、GitHub 二次验证标签、带恢复码的 GitHub 卡片。

## Full-view comparison evidence

- 能力标签由 10 像素常规文字和 9 × 5 内边距，调整为 11 像素半粗文字和 12 × 7 内边距；在三列卡片内仍未挤压标题、摘要、时间或右侧箭头。
- GitHub 卡片不再显示恢复码剩余数量，右上角只保留与登录流程有关的“需要二次验证”。恢复码本身仍可在详情和编辑页面查看、复制和维护。
- 放大后的标签继续使用各类型原有主题色和圆角体系，没有改变卡片尺寸、品牌图标或页面密度。

## Focused region comparison evidence

- 字体与排版：标签字号和字重提升后更易读，长文本“支持一键填入”和“需要二次验证”均保持单行完整显示。
- 间距与布局：12 × 7 内边距在 360 像素级卡片宽度内无越界；GitHub 去掉数量标签后只剩一行状态，不再形成上下堆叠。
- 颜色与视觉令牌：安全复制、自动填入和二次验证继续使用现有品牌/语义浅色底板，放大后对比度稳定。
- 图片与图标：本轮没有更改品牌图片资产；现有高分辨率透明图标未发生缩放或裁切回归。
- 文案与内容：“剩余 N 个”不再作为卡片级摘要；“需要二次验证”保留，符合用户指定的信息优先级。
- 交互与可访问性：标签不是操作控件，不改变点击命中区域；卡片整体仍可点击进入详情。

## Findings

- 没有剩余可执行的 P0、P1 或 P2 问题。
- 验证截图使用 1480 × 900、96 DPI；用户实际分辨率和缩放下的最终主观大小仍由用户验收。

## Comparison history

- 第一轮 P2：卡片右上角标签字号和内边距偏小。放大字号、字重和内边距后复拍，所有标签均未越界或压缩卡片内容。
- 第一轮 P2：GitHub 同时显示恢复码数量和二次验证，右上角信息过密。隐藏 GitHub 的通用能力标签后复拍，只保留二次验证提示。
- 修正后的同屏对照未发现新的 P0、P1 或 P2 问题。

## Implementation checklist

- [x] 放大卡片右上角能力标签。
- [x] 移除 GitHub 恢复码剩余数量提示。
- [x] 保留 GitHub 二次验证提示。
- [x] 完成 Release 构建、隔离回归、实拍和同屏对照。

## Follow-up polish

- 无必须跟进的 P3 项。

final result: passed
