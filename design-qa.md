# Design QA

## Source visual truth

- DeepSeek API 输入问题：`C:\Windows\TEMP\codex-clipboard-d5ec2a51-fea2-416c-9ca3-69a0a4b3cca4.png`（1025 × 966）。
- 保存后详情自动弹出与单卡位置问题：`C:\Windows\TEMP\codex-clipboard-e5c169a4-a3d8-4298-ac7b-3defec7ae47d.png`（2293 × 1391）、`C:\Windows\TEMP\codex-clipboard-4b27d30d-e5b5-4908-b2ac-eb6a7ba73fbc.png`（2308 × 1380）。
- GitHub 字段结构问题：`C:\Windows\TEMP\codex-clipboard-9d4a169a-e794-4aae-b19c-5e389a442e93.png`（1029 × 988）。
- 源截图中的红框是问题标注，不属于目标界面元素。

## Implementation evidence

- 完整信息库：`C:\Windows\TEMP\XToolInformationVaultGitHubQA3-20260826\information-vault-unlocked.png`（1480 × 900）。
- 单条 GitHub 卡片：`C:\Windows\TEMP\XToolInformationVaultGitHubQA3-20260826\information-vault-single-entry.png`（1480 × 900）。
- DeepSeek 编辑页：`C:\Windows\TEMP\XToolInformationVaultGitHubQA3-20260826\information-vault-deepseek-dialog.png`（720 × 680）。
- GitHub 编辑页：`C:\Windows\TEMP\XToolInformationVaultGitHubQA3-20260826\information-vault-github-dialog.png`（720 × 680）。
- 详情页：`C:\Windows\TEMP\XToolInformationVaultGitHubQA3-20260826\information-vault-detail.png`（1480 × 900）。
- 同屏对照：`C:\Windows\TEMP\XToolInformationVaultGitHubQA2-20260826\compare-deepseek.png`、`compare-github.png`、`compare-single-entry.png`。

## Viewport and normalization

- 主窗口实现以 1480 × 900 像素、96 DPI 渲染；编辑弹窗以 720 × 680 像素、96 DPI 渲染。
- DeepSeek 和 GitHub 对照将源图与实现图等比缩放至 680 像素高后左右并排；单卡对照将两侧统一为 900 像素高。
- 状态覆盖：DeepSeek 已填 API Key、GitHub 账号密码及三个可选项、单卡列表、七卡列表、详情弹层、类型下拉框。

## Full-view comparison evidence

- DeepSeek 表单保持原有信息库样式，但 API Key 已使用普通文本框，输入和值回显均为明文。
- GitHub 表单从旧版“推送密钥 + 恢复码”扩展为账号、密码、可选推送密钥、二次验证、可选恢复码和备注；内容超过可视高度时使用现有自定义滚动条，固定底部操作区不被遮挡。
- 单条记录使用与多卡一致的三列网格宽度，并固定从内容区左上角排列，不再随可用高度垂直居中。
- 保存新增记录后刷新列表但不传入首选记录，因此不会自动打开详情弹层；编辑已有记录后仍返回详情，保持编辑反馈连续性。

## Focused region comparison evidence

- 字体与排版：字段标签、正文、辅助说明继续沿用 X-Tool 的 Segoe UI 中文回退与既有字号层级；API Key 明文未裁切，GitHub 密码和推送密钥仍按秘密字段隐藏。
- 间距与布局：GitHub 账号/密码保持等宽两列；推送密钥、恢复码和备注使用满宽卡片；二次验证选择器及辅助文案在 720 像素弹窗内无重叠。
- 颜色与控件：二次验证采用应用内自定义圆角勾选框；选中态使用主蓝，卡片外部使用暖色提醒标签，没有回退到 Windows 原生控件外观。
- 图片与图标：GitHub、DeepSeek、Steam、Ubisoft、Epic、MySQL、Redis、微信、QQ 使用对应站点或已安装客户端图标；虚拟机和自定义类型继续使用适合其通用语义的 Fluent 图标。图标按 42/46 像素容器内等比缩放，无拉伸或裁切。
- 文案与内容：GitHub 卡片在启用二次验证后明确显示“需要二次验证”；恢复码与推送密钥均标为可选，备注仍作为通用可编辑字段。

## Findings

- 没有剩余可执行的 P0、P1 或 P2 问题。
- 保存后不再自动弹详情属于交互修复，不改变用户点击卡片查看或编辑详情的路径。
- 旧版 GitHub 记录中的 `Secret` 原表示推送密钥；迁移时将其移动到新的推送密钥字段并把密码留空，避免把令牌误标为账号密码。

## Interaction verification

- Release 构建成功：0 警告、0 错误。
- 隔离验证通过：密文无账号和秘密明文、错误密码被拒绝、当前 GitHub 全字段加密往返成功、旧版独立恢复码迁移成功、旧版 GitHub 推送密钥迁移成功、测试库重置删除成功。
- 没有读写或重置用户真实信息库；验证只使用 `C:\Windows\TEMP\XToolInformationVaultGitHubQA3-20260826` 中的隔离测试库。
- 实际保存输入、真实品牌账号、一键填入客户端和不同 DPI 下的主观观感仍交由用户验收。

## Comparison history

- 第一轮 P1：DeepSeek API Key 使用 PasswordBox，输入被隐藏。修复为仅 DeepSeek 使用同风格 TextBox；复拍后值完整明文显示。
- 第一轮 P1：新增保存后调用 `RefreshEntries(entry.Id)`，导致详情立即弹出。修复为无首选项刷新；编辑路径继续保留详情回显。
- 第一轮 P2：单卡 UniformGrid 占满内容高度，卡片被垂直分配到错误位置。为 ItemsControl 和 UniformGrid 增加顶部对齐；复拍单卡后固定在左上角。
- 第一轮 P2：GitHub 缺少账号密码和二次验证语义。新增完整字段模型、自定义选择器及卡片外部提醒标签，并补充旧数据迁移。
- 第一轮 P2：卡片使用通用字体图标，品牌识别不足。替换为对应品牌图标并保留通用类型回退；复拍七卡页面后图标清晰且比例一致。
- 第二轮同屏对照未发现新的 P0、P1 或 P2 问题。

## Implementation checklist

- [x] DeepSeek API Key 输入改为明文。
- [x] 新增保存后不自动弹出详情。
- [x] 单卡固定从网格左上角排列。
- [x] GitHub 增加账号、密码、可选推送密钥、可选恢复码、可选二次验证与备注。
- [x] 二次验证在卡片外部显示提醒标签。
- [x] 信息卡和详情使用对应品牌图标。
- [x] 兼容旧版 GitHub 推送密钥与独立恢复码记录。
- [x] 完成 Release 构建、隔离加密回归、真实截图和同屏对照。

## Follow-up polish

- P3：GitHub 字段较多，当前通过自定义滚动条保持底部按钮常驻；用户若长期保存大量恢复码，可再评估折叠恢复码区域，但不影响本轮使用。

final result: passed
