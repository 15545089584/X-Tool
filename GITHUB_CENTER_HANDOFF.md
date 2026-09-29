# GitHub 仓库中心第一版交接

日期：2026-09-29。实际仓库：`D:\Claude Code\X-Tool`。

## 最新增量：仓库身份与扫描验证（2026-09-30）

- 核对用户截图：`C:\Users\MRSW\Documents\ChatGPT\X-Tool` 确有甘特图成果等十个未跟踪文件，和 `D:\Claude Code\X-Tool` 是不同路径，不能按名称合并。未移动、删除或提交这些文件。
- 左侧仓库显示名称、完整路径和状态，按可用性、名称、路径排序；历史提交标题与作者日期分行呈现。
- 自动扫描候选及已登记目录用 `git rev-parse --show-toplevel` 验证根目录，拒绝空 .git 目录及父仓库误识别。旧失效记录保留但标记不可用，选择时不再弹出 Git 原始错误。
- 清空/切换仓库时取消旧差异请求，避免旧结果回填。
- 构建：`ScreenshotApp/bin/github-center-identity/Release/XTool.exe`。Release 零警告零错误，64 项核心测试和 55 项 WPF 检查通过，未退出旧实例或更改快捷方式。

## 上一增量：本地扫描与远程提交（2026-09-30）

- 新构建：`ScreenshotApp/bin/github-center-discovery/Release/XTool.exe`；polish 版本正在运行导致原目录构建复制失败，改用独立目录，未结束用户进程。
- 仓库中心打开后异步扫描用户 Documents/Desktop/source/repos/Projects 和固定磁盘根目录下 Claude Code/Code/Projects/Repos/Git；左侧文件夹搜索按钮可扫描指定目录。
- 扫描最多六层、一万个目录，跳过目录链接、依赖及构建目录；识别 `.git` 目录和 worktree 文件，去重登记并保存，不修改仓库、不自动信任；结果标明限制和跳过数量。裸仓库暂不自动发现。
- GitHub 仓库页改为仓库列表和提交列表两栏；选择仓库自动读取默认分支提交，每页 30 条，可继续加载，展示作者、时间、SHA 和完整说明，支持在浏览器查看提交详情。尚无远程分支选择和内嵌远程文件差异。
- 请求支持取消及选择切换隔离，令牌仍只发送给 GitHub API；私有仓库提交需要 Contents 只读权限。未自动联网扫描或克隆本地发现的仓库。
- 核心 61 项、WPF 55 项回归通过；独立 Release 零警告零错误。测试使用临时仓库和模拟 API，没有使用用户凭据或远程推送。

## 上一增量：弹窗、空状态与宠物轮盘（2026-09-30）

- 新构建：`ScreenshotApp/bin/github-center-polish/Release/XTool.exe`。未关闭用户实例，未更改快捷方式或自启动。
- 连接及确认弹窗采用自绘圆角渐变边框、独立关闭按钮与并排操作按钮；主按钮内容显式白字，防止全局 TextBlock 样式覆盖；密码框统一圆角与高度。
- 未选仓库时隐藏空工具栏及无效操作区域，显示添加/克隆入口；开发工具页仓库中心入口加宽。
- 宠物轮盘原静默闹钟位置改为 GitHub 仓库中心，移除闹钟计数角标；保留闹钟服务及其他闹钟代码。
- Release 构建零警告零错误；55 项核心测试、49 项 WPF 检查通过，包括真实连接弹窗和动画结束后的轮盘非空像素检查。截图在 `artifacts/github-center-validation/`。
- 本轮未实现 OAuth。后续可将浏览器授权作为主入口、PAT 作为高级备用；GitHub App 提供更细粒度仓库授权，OAuth App 设备流可用于无后端桌面登录。API 登录与 Git 传输凭据仍须分别处理，不能在客户端分发应用私钥或 Client Secret。

## 上一增量：样式与绑定帮助

- 新构建：`ScreenshotApp/bin/github-center-help/Release/XTool.exe`。未关闭用户实例或切换快捷方式。
- GitButton 改为模块自有模板，显式定义颜色、圆角、固定边框、禁用/悬停/焦点，不再继承容易受主程序全局样式影响的按钮基础样式。
- 左侧账户按钮加入 GitHub 图标和截断保护，旁边新增问号；增加侧栏分界与主区域 16 DIP 留白。右上统一 36 DIP 图标按钮，空仓库禁用操作，移除仓库收进更多菜单。
- 问号和连接对话框均可打开绑定帮助，包含细粒度 PAT、GitHub App、连接排查三页；教程按官方文档说明只读权限和组织审批，并提供官方链接。
- 当前仍只支持 PAT 绑定。GitHub App 页明确尚未实现授权回调、安装选择和续期；安装令牌、JWT、PEM 私钥在 HTTP 请求前拒绝，不能误导用户认为 App 已接入。
- Git API 账户与系统 Git 传输凭据分离的说明已放入教程，不要求 API 只读功能获取全部写入权限。
- Release 零警告零错误；55 项核心回归、41 项 WPF 检查，包括全局按钮黑色样式干扰、空仓库按钮状态、问号、帮助滚动及最小尺寸。
- UI 测试引用本次 github-center-help 输出；后续验证先构建此目录。帮助截图在 `artifacts/github-center-validation/help-*.png`。

## 入口与构建

- 主窗口 → 开发工具 → 顶部“GitHub 仓库中心”，独立窗口、同一进程内复用窗口。
- Release：`ScreenshotApp/bin/github-center/Release/XTool.exe`。
- 本轮不关闭旧 X-Tool，不改桌面、任务栏或开机启动链接，不连接真实 GitHub 账户，不向网络推送。
- 构建命令：`dotnet build ScreenshotApp/ScreenshotApp.csproj -c Release -p:OutputPath=bin/github-center/Release/ --no-restore`。

## 已实现

- 添加已有本地仓库、名称/路径搜索、逐仓库状态检查、打开文件夹/终端，以及从列表移除（不删除文件）。
- GitHub HTTPS 克隆，要求新的子目录，不覆盖既有目录；私有 Git 传输沿用系统 Git Credential Manager 或既有 SSH 凭据。
- porcelain v2 + NUL 状态解析；已暂存/未暂存、重命名和冲突区分展示，纯文本 unified diff、新文件预览、二进制与大文件保护。
- 单文件暂存/取消暂存，提交说明在暂存/刷新期间保留；本地提交前确认全部暂存文件与身份，暂存差异哈希变化会阻止旧确认继续提交。
- 当前分支、上游、领先/落后计数、最近 100 条提交及详情；创建/切换本地分支要求工作区干净。
- 获取、仅快进拉取、显式推送；推送前确认远程 URL、分支和本地提交，不强推、不自动携带标签、不自动 stash。
- 首次写操作要求信任仓库，提醒 Git hooks、过滤器及凭据工具的执行边界；共享 Git 目录内写操作串行。
- Windows Job 对象统一结束 Git 及 hook 子进程，输出流并行排空并限制大小，取消/失败后重新读取状态，不声称取消等于回滚。
- 单个 GitHub API 账户、细粒度 PAT 验证、远程仓库分页、PR/Actions 只读摘要与网页入口；权限不足、过期和限流分别提示。
- 远程摘要离线缓存标注时间，API 令牌和远程缓存使用当前 Windows 用户 DPAPI 加密；设置显示缓存路径、大小、打开目录、清理缓存。
- 复用邮箱配色和细滚动条，列表保持紧凑布局，窗口尺寸记忆，长操作不占用 UI 线程等待。

## 存储与边界

- 根目录：`%LOCALAPPDATA%/X-Tool/GitHubCenter`。
- `repositories.json` 保存本地路径、信任选择、检查时间和窗口尺寸；`account.dat` 保存加密 API 凭据；`Cache/*.dat` 保存加密远程摘要。
- 首版使用原子替换 JSON 和加密缓存文件，不为少量登记数据引入新的数据库生命周期；未新增第三方运行依赖。
- API 登录与 Git 推送凭据是分开的。PAT 不写入 Git remote、不传入命令行、不改全局 Git 配置；交互式 Git 凭据提示关闭，凭据缺失时失败并要求先配置系统 Git。
- 仅支持 github.com，首版不是多账户/Enterprise 产品；本地 Git 仓库可以没有 GitHub 账户。
- 远程获取与 API 刷新由用户触发，无后台高频轮询、通知订阅、自动提交/自动推送。
- 首版无 PR 写操作、内置冲突编辑、按块暂存、AI、Release 发布、组织权限管理；复杂操作交由外部工具。
- HTTP 返回错误不显示原始响应体，Git 错误中的常见令牌/URL 凭据会脱敏。
- 大输出安全上限 8 Mi 字符；文本预览最多 512 Ki 字符/新文件 512 KiB，历史和远程列表明确限定展示范围或分页。
- Git 写操作不是跨进程事务。操作前做状态和内容核对，但外部工具仍可能并发写入；Git 自身锁和错误必须保留，不能自动删除 index.lock。
- 未自动执行计划中的所有 P1/P2 功能，也未迁移既有邮件存储、宠物或协作中心逻辑。

## 验证

- 核心测试：`dotnet run --project GitHubCenter.Tests/GitHubCenter.Tests.csproj -c Release`。
- 52 项隔离回归覆盖中文/空格路径、暂存范围、初始提交、外部暂存变更、重命名、分支、快进与分叉拉取、本地裸仓库推送、敏感 URL 拒绝、取消、worktree、分离 HEAD、二进制、DPAPI、缓存、HTTP 授权及限流。
- 耗时断言验证：取消 20 秒 hook 无需等待脚本自然结束；测试发现原 Process.Kill(true) 路径不足后改用 Windows Job 对象。
- WPF 测试：`dotnet run --project GitHubCenter.UiTests/GitHubCenter.UiTests.csproj -c Release`（先完成上述 Release 构建）。
- UI 使用临时仓库和合成 PR/构建缓存，不使用真实账户或真实用户设置；34 项检查覆盖 1260×800 与 1040×680 的布局、文件选择、暂存、草稿保留、历史、缓存、空搜索状态、按钮颜色、输入框主题与滚动条。
- UI 截图位于 `artifacts/github-center-validation/`，属于本地验证产物，不随代码提交。
- 未验证真实私有仓库 PAT、组织审批和真实网络推送；API 结构/错误使用假 HTTP handler 验证，Git 推送仅使用临时本地裸仓库。不能宣称真实账户已接入成功。

## 代码位置

- `ScreenshotApp/GitHubCenter/GitRepositoryService.cs`：Git 进程、解析、安全操作。
- `ScreenshotApp/GitHubCenter/GitProcessJob.cs`：Windows 进程树取消。
- `ScreenshotApp/GitHubCenter/GitHubApi.cs`：GitHub REST 只读接口。
- `ScreenshotApp/GitHubCenter/GitHubStore.cs`：账户、仓库登记和缓存。
- `ScreenshotApp/GitHubCenter/GitHubCenterWindow.xaml(.cs)`：独立工作台与对话框。
- `ScreenshotApp/DeveloperTools/DeveloperToolsView.xaml(.cs)`：仅新增入口，不重构开发环境页。
