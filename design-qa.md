# Design QA

## 视觉基准

- 用户截图：`C:\Windows\TEMP\codex-clipboard-8565f977-db4e-4072-bd10-ac2848efa0cb.png`（2280 × 1432）。
- 红框用于标注问题；目标是让 Steam、Ubisoft、Epic 与 GitHub 的右上角标签沿用 DeepSeek、Redis、MySQL 的浅色底板、主题色文字和无硬边框风格。

## 实现证据

- 实现截图：`C:\Windows\TEMP\XToolInformationVaultBadgePaletteValidation-20260827\information-vault-unlocked.png`（1480 × 900，96 DPI）。
- 同屏对比：`C:\Windows\TEMP\XToolInformationVaultBadgePaletteValidation-20260827\qa-badge-palette.png`（2048 × 720）。
- 两张截图使用的数据条目数量不同，因此只比较标签系统、卡片布局与文字边界，不比较卡片内容和排列顺序。

## 视口与归一化

- 对比图将原图与实现图分别等比缩放并置于 1000 × 720 的白色画板内，没有拉伸。
- 覆盖 Steam、Ubisoft、Epic 的“一键填入”标签，以及 GitHub 的“需要二次验证”标签。

## 全局对比

- Steam 与 Ubisoft 改为浅蓝底配品牌蓝文字，Epic 改为浅紫底配紫色文字。
- GitHub 二次验证标签改为浅紫底配紫色文字，并移除原有橙色描边。
- 标签继续使用 11px 半粗字体、水平 12px/垂直 7px 内边距和 9px 圆角，与现有安全复制标签保持同一视觉语言。
- 卡片尺寸、网格间距、图标与底部类型信息均未改变。

## 局部检查

- 字体：所有标签保持单行，粗细与相邻标签一致。
- 边界：`支持一键填入` 和 `需要二次验证` 均未裁切、换行或越出卡片。
- 配色：使用柔和主题色底板，不再出现黑字白底或橙色硬描边造成的突兀感。
- 图标：本次没有调整图标资产或图标底板。
- 文案：保留原有功能文案，没有改变含义。
- 交互：标签为非交互状态提示，本次未改变卡片点击行为。

## 问题记录

- 首轮发现 P2：Steam、Ubisoft、Epic 使用深色中性文字，GitHub 使用橙色描边，与安全复制标签体系割裂。
- 修复：统一改为浅色主题底板加主题色文字，移除 GitHub 标签硬边框。
- 修复后未发现 P0、P1 或 P2 级视觉问题。
- 实际设备 DPI、显示器色彩与用户主观观感仍由人工验收确认。

## 验收清单

- [x] 长标签单行完整显示
- [x] 标签未超出卡片边界
- [x] 四类标签均采用浅色扁平样式
- [x] GitHub 标签无硬描边
- [x] 卡片网格和内容位置无回归
- [x] Release 构建与信息库验证通过

最终结果：通过。
