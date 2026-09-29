# GitHub 仓库中心第一版交接

日期：2026-09-29。实际仓库：`D:\Claude Code\X-Tool`。

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
