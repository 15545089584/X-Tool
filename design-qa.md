# Design QA

## Source visual truth

- 锁定页问题标注：`C:\Windows\TEMP\codex-clipboard-ad636be8-a5d3-4c52-bce4-b32fc8bc3d92.png`（2236 × 1394）。
- 主页面问题标注：`C:\Windows\TEMP\codex-clipboard-84540c89-a26b-4a0c-8185-4b6021155f93.png`（2394 × 1394）。
- DeepSeek 表单问题标注：`C:\Windows\TEMP\codex-clipboard-dcdaf04e-2867-43e3-92d0-adcb9ae03c48.png`（1119 × 1062）。
- Steam 密码框问题标注：`C:\Windows\TEMP\codex-clipboard-58f31a51-c352-43b8-be68-c210342fa297.png`（1198 × 1033）。
- 这些截图中的红框是问题证据，不是需要保留的视觉元素。

## Implementation evidence

- 锁定页：`C:\Windows\TEMP\XToolInformationVaultFieldFix-20260826\validation-2\information-vault-locked.png`（1480 × 900）。
- 信息库主页：`C:\Windows\TEMP\XToolInformationVaultFieldFix-20260826\validation-2\information-vault-unlocked.png`（1480 × 900）。
- Steam 编辑页：`C:\Windows\TEMP\XToolInformationVaultFieldFix-20260826\validation-2\information-vault-entry-dialog.png`（720 × 680）。
- DeepSeek 编辑页：`C:\Windows\TEMP\XToolInformationVaultFieldFix-20260826\validation-2\information-vault-deepseek-dialog.png`（720 × 680）。
- GitHub 编辑页：`C:\Windows\TEMP\XToolInformationVaultFieldFix-20260826\validation-2\information-vault-github-dialog.png`（720 × 680）。
- 同屏对照：`compare-locked.png`、`compare-main.png`、`compare-deepseek.png`，均位于上述 `validation-2` 目录。

## Viewport and normalization

- 主窗口实现以 1480 × 900 DIP、96 DPI 渲染；弹窗以 720 × 680 DIP、96 DPI 渲染。
- 三张同屏对照分别把源截图和实现截图等比缩放至 1000 像素宽，左右并排；源截图包含桌面环境和标注，因此只比较对应的页面区域、控件比例和文本状态。
- 状态覆盖：已有信息库的锁定页、含 7 条模拟记录的解锁页、Steam/DeepSeek/GitHub 编辑页、类型下拉展开态。

## Full-view comparison evidence

- 锁定页已移除用户指定的小字，主密码输入、主按钮和带边框的重置入口形成清晰的单列层级；按钮、输入框均未越界。
- 主页面只保留“支持填入”统计，GitHub 恢复码改为 GitHub 卡片自己的剩余数量标签；顶部操作区不再出现独立恢复码统计。
- DeepSeek 页面由“类型、名称、用途、API Key”收敛为“类型、API Key、备注”，减少无意义字段；GitHub 页面在同一记录中容纳推送密钥与可选恢复码。

## Focused region comparison evidence

- 输入控件：TextBox/PasswordBox 的内容宿主改为独立内边距，密码圆点完整显示；搜索框的内容起点与图标后的 36 DIP 左边距对齐。
- 按钮：主按钮模板显式用白色文字；“立即锁定”使用浅蓝边框与浅色悬停，不再变成实心主蓝；忘记密码入口有淡红边框和独立悬停态。
- 表单：Steam 账号/密码保持两列；DeepSeek 密钥占满整行；GitHub 推送密钥和恢复码各自占满整行；MySQL、Redis、虚拟机和自定义记录继续保留可编辑名称与适用字段。
- 图片与素材：没有新增或替换图片资源；现有 Segoe Fluent Icons 在当前缩放下清晰，没有拉伸、裁切或模糊。

## Findings

- 没有剩余可执行的 P0、P1 或 P2 问题。
- 字体与排版：沿用 X-Tool 的 Segoe UI 中文回退与现有字重，主按钮文字为白色，输入内容和密码圆点垂直居中且不裁切。
- 间距与布局：单字段占满一行，双字段维持等宽两列；卡片圆角、边框、12 DIP 区块间距与信息库现有风格一致。
- 颜色与视觉令牌：主操作使用 X-Tool 蓝色和白字；次要操作使用浅色背景；危险入口使用淡红语义色，未引入 Windows 原生控件外观。
- 图片与素材：本轮没有新增图像资产，图标来源及清晰度与现有页面一致。
- 文案与内容：恢复码不再作为独立类型；DeepSeek、GitHub、账号类、数据库类分别显示与实际场景相符的字段。

## Interaction verification

- Release 构建：0 警告、0 错误。
- 回归测试命令退出码为 0；`git diff --check` 未发现空白错误。
- 隔离测试库验证通过：密文中无账号与秘密明文、错误密码被拒绝、保存后重新解锁可往返、旧版独立恢复码迁移为 GitHub 凭据、重置删除测试库。
- 主页面现有“编辑信息”路径保留；编辑弹窗加载原字段并保存回同一记录。
- 真实主密码、真实凭据、实际客户端一键填入及不同系统缩放继续由用户验收。

## Comparison history

- 第一轮 P1：蓝色按钮文字继承失败，视觉上为深色；主密码和普通密码内容宿主被双重内边距压缩。修复按钮内容模板和输入控件内容宿主后，复拍 `validation-2`，白字和完整密码圆点均可见。
- 第一轮 P2：锁定页冗余说明、无边框重置入口、主页面独立恢复码统计和 DeepSeek 冗余字段偏离需求。移除说明与统计、为重置入口增加边框、重组字段后，复拍三组同屏对照。
- 第一轮 P2：次要按钮继承主按钮的实心蓝色悬停。拆分主/次按钮模板，并为次要和危险操作设置各自的浅色悬停状态。
- 第二轮对照未发现新的 P0/P1/P2；GitHub 长表单使用窗口内自定义滚动条，底部保存操作始终固定可见，属于预期行为。

## Implementation checklist

- [x] 移除锁定页指定小字。
- [x] 修复文本框、密码框和搜索框内容位置与裁切。
- [x] 统一蓝色按钮白字，并拆分次要按钮悬停态。
- [x] 为忘记密码入口增加清晰边框。
- [x] 删除主页面独立恢复码统计。
- [x] 将恢复码归入 GitHub 凭据，兼容旧版已保存记录。
- [x] 按类型显示必要字段，保留所有记录的编辑路径。
- [x] 完成 Release 构建、隔离加密验证、回归检查和截图对照。

## Follow-up polish

- P3：GitHub 同时保存大量恢复码和长备注时需要滚动，这是为了保持保存按钮固定可见；如真实使用频率较高，可在后续单独评估弹窗高度或折叠恢复码区域。

final result: passed
