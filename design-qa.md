# Design QA

## Source visual truth

- 编辑详情叠灰与错误文案：`C:\Windows\TEMP\codex-clipboard-b1daddf2-f1c5-4bf6-ada4-487eb51b517d.png`（2448 × 1403）。
- 品牌图标清晰度与配色：`C:\Windows\TEMP\codex-clipboard-2a7b6cfc-6450-4d1e-89a5-ff7feae90169.png`（2323 × 1372）。
- 搜索输入起点：`C:\Windows\TEMP\codex-clipboard-bd588820-3a87-409d-b834-f8a6d3cc198e.png`（2221 × 1373）。
- 弹窗关闭按钮：`C:\Windows\TEMP\codex-clipboard-dc4cda40-372c-463f-8db1-6eeed079d341.png`（2372 × 1413）。
- 源截图中的红框用于标记缺陷，不属于目标界面元素；本轮目标是在既有 X-Tool 视觉体系内修正这些缺陷，而不是逐像素保留缺陷状态。

## Implementation evidence

- 完整信息库：`C:\Windows\TEMP\XToolInformationVaultVmVisualValidation2-20260827\information-vault-unlocked.png`（1480 × 900）。
- 无额外叠灰的详情：`C:\Windows\TEMP\XToolInformationVaultVmVisualValidation2-20260827\information-vault-detail.png`（1480 × 900）。
- 搜索输入状态：`C:\Windows\TEMP\XToolInformationVaultVmVisualValidation2-20260827\information-vault-search-input.png`（1480 × 900）。
- GitHub 编辑弹窗：`C:\Windows\TEMP\XToolInformationVaultVmVisualValidation2-20260827\information-vault-github-dialog.png`（720 × 680）。
- 虚拟机编辑弹窗：`C:\Windows\TEMP\XToolInformationVaultVmVisualValidation2-20260827\information-vault-virtual-machine-dialog.png`（720 × 680）。
- 同屏对照：同目录下 `qa-full.png`、`qa-detail.png`、`qa-search.png`、`qa-dialog.png`（均为 2048 × 720）。

## Viewport and normalization

- 主窗口实现以 1480 × 900 像素、96 DPI 渲染；编辑弹窗以 720 × 680 像素、96 DPI 渲染。
- 四组同屏对照均将源图和实现图分别等比缩放到 1000 × 720 的白色画布后左右并排，不拉伸内容；源图包含桌面环境时，仅用于判断被标注区域及整体层级。
- 状态覆盖：七卡列表、详情弹层、搜索框聚焦并输入、GitHub 长表单、虚拟机系统字段、空结果、自定义滚动条与关闭按钮。

## Full-view comparison evidence

- 详情弹层只保留自身卡片与阴影，不再给整个信息库内容区额外覆盖灰色遮罩；打开编辑弹窗前详情层会先隐藏，因此不会形成双重灰幕。
- 信息卡仍沿用三列网格和既有圆角、间距、标签布局，但图标换为 128 × 128 透明 PNG 品牌资产，放大后边缘明显比旧 favicon 清晰。
- 虚拟机表单把旧的单一“系统或用途”字段拆分为操作系统、系统版本（发行版）和版本号；Linux/CentOS/7.6 在 720 × 680 弹窗中比例正常，底部操作区保持固定。
- 搜索框改为图标列、文本列和尾部留白三列结构，输入“22”时从图标右侧立即开始，不再在框体中部起笔。

## Focused region comparison evidence

- 字体与排版：标题、标签、正文和辅助文字继续使用现有 Segoe UI 中文回退与既有层级；“秘密复制”已改为“敏感内容复制后 30 秒自动清除”，语义准确且未产生换行。
- 间距与布局：搜索图标与文本保持固定 36 像素前导区域；虚拟机三项系统字段等宽排列，主机/IP 与端口区域无溢出；长表单通过现有自定义滚动条承载。
- 颜色与视觉令牌：每个品牌/系统图标底板根据主色使用低饱和浅色，GitHub 使用中性灰、Ubuntu 使用浅橙、Windows 使用浅蓝，避免图标与底板色相冲突。
- 图片质量：GitHub、Steam、Ubisoft、Epic、DeepSeek、MySQL、Redis、微信、QQ 和各系统图标使用同一来源的高分辨率透明品牌资产；卡片内等比缩放，无拉伸、锯齿或透明边缘光晕。
- 图标：详情和编辑弹窗右上角均为纯“×”，无背景小方框；Windows 字形回退问题已通过使用普通文本乘号规避。
- 文案与内容：虚拟机的“版本”明确为发行版选择，“版本号”由用户输入；复制安全提示统一使用“敏感内容”，避免“秘密”这一不自然名词。
- 交互与可访问性：搜索框保留焦点蓝边；下拉框、勾选框、滚动条继续采用 X-Tool 自定义样式；关闭按钮维持 34 × 34 点击区域，但只显示“×”。

## Findings

- 没有剩余可执行的 P0、P1 或 P2 问题。
- 当前虚拟机弹窗的备注字段位于首屏下方，需要滚动查看；这是为保持系统三字段完整展示和固定底部操作区而保留的既有长表单行为，未造成控件裁切。
- 品牌图标用于服务识别，不替代任何操作按钮；配色差异属于有意的品牌语义强化。

## Interaction verification

- Release 构建成功：0 警告、0 错误。
- 隔离加密验证通过：密文不含账号和敏感内容明文、错误密码被拒绝、保存往返成功、旧版 GitHub 恢复码/推送密钥迁移成功、旧版虚拟机 `CentOS7.6` 成功迁移为 Linux/CentOS/7.6、测试库重置删除成功。
- 虚拟机操作系统和发行版依赖列表在初始化时正确显示 Linux/CentOS，未触发 WPF 初始化异常。
- 验证只使用 `C:\Windows\TEMP\XToolInformationVaultVmVisualValidation2-20260827` 的隔离数据；未读写用户真实信息库。
- 实际编辑保存、逐个切换所有系统发行版以及不同 DPI 下的主观观感仍交由用户验收。

## Comparison history

- 第一轮 P1：编辑详情时详情层与弹窗遮罩叠加，页面出现异常灰幕。修复为编辑前隐藏详情层，并移除详情层整页灰色填充；复拍详情后仅保留弹层自身层级。
- 第一轮 P2：搜索输入从框体中部开始。重构为独立图标列与左对齐文本列；复拍输入“22”后起点紧随图标。
- 第一轮 P2：关闭按钮存在小背景框且 Fluent 字形在验证环境中显示异常。改为透明无边框按钮和普通“×”；详情与编辑弹窗复拍均显示正确。
- 第一轮 P2：favicon 尺寸不一且部分模糊，底板色与图标品牌色不协调。替换为统一 128 × 128 透明品牌/系统图标并按主题色配置底板；复拍七卡页后清晰度和识别度一致。
- 第一轮 P2：虚拟机系统信息只有自由文本，无法提供结构化选择和动态图标。新增操作系统、发行版、版本号字段及旧数据迁移，卡片根据系统/发行版选择图标与配色；复拍 Linux/CentOS 表单和 Ubuntu 卡片后通过。
- 第二轮同屏对照未发现新的 P0、P1 或 P2 问题。

## Implementation checklist

- [x] 消除编辑时的双层灰色背景。
- [x] 将“秘密”统一替换为准确的“敏感内容/密钥”文案。
- [x] 虚拟机增加操作系统、发行版和版本号字段及旧数据迁移。
- [x] 根据虚拟机系统选择动态使用对应系统图标和主题底色。
- [x] 品牌图标升级为统一高分辨率透明资产。
- [x] 搜索输入固定从图标右侧开始。
- [x] 关闭按钮只保留“×”，不显示小背景框。
- [x] 完成 Release 构建、隔离数据回归、真实截图和同屏对照。

## Follow-up polish

- P3：若后续需要覆盖更多冷门系统，可继续扩展“其他系统”的图标与发行版清单，不影响当前 Linux、Windows、macOS 和 BSD 的使用。

final result: passed
