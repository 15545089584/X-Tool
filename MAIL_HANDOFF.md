# X-Tool 邮箱开发接续交接

更新：2026-09-28。下一窗口先读本文件，再读 HANDOFF.md 顶部；旧记录保留的是当时状态，以本文件为准。

## 最新增量：翻译 CPU 占用与按钮焦点（2026-09-28）

- 当前运行版为 `ScreenshotApp/bin/mail-translation-responsive/Release/XTool.exe`；桌面、任务栏与原 Run 入口已切换并读回。旧构建保留，未暂存/提交/推送。
- 用户报告翻译期间鼠标卡顿。推理原本已有 Task.Run，但 ONNX 默认线程池无限额并允许空转。同一合成长邮件旧版累计 CPU 65.83s/墙钟 2.51s；限额后最终复测累计 CPU 1.25s/墙钟 1.65s，WPF 16ms 心跳最大间隔 48.4ms，持续响应。这是合成证据，不等同用户实际鼠标手感验收。
- TranslationEngineProvider.cs 在保留此前其他任务改动基础上加入共享异步推理信号量、最多 2 个 intra-op 线程、1 个 inter-op 线程、关闭两类空转，释放 SessionOptions。英译中/中译英共用限流；排队取消与取消后继续调用均已测试。邮件清洗与分段整个流程也改为 Task.Run。
- 翻译按钮专用 MailTranslationButton 样式移除 WPF 默认虚线 FocusVisualStyle，保留单层自定义焦点提示和键盘可达性，边框始终占固定 1px，避免焦点引发布局变化。
- 常见品牌、产品名称、字母数字账号标识及标题符号受到保护；品牌两侧常见短操作文案使用小型明确术语表。Docker 应保留英文；小模型仍有普通句子及专业术语误译，未宣称已解决整体准确度。
- Release 零警告/零错误；52 项 Mail.Tests、最小/常规布局、标题原文切换、发件人保持、真实长文本模型、品牌保护、排队取消、共享中译英 smoke 通过。
- 本轮 UI 确认企业邮 InboxTotal 已为 31，之前仅 2 封的问题已观察到恢复；未读取网页设置，不能断言用户具体改动。
- 用户询问 DeepSeek API 费用，本轮仅查官方报价，未接入、未上传邮件、未收集 API Key。2026-09-28 官方 deepseek-flash（V4.1 Flash）：每百万输入未命中缓存闲时 1 元/高峰 2 元，输出闲时 4 元/高峰 8 元；例如输入 3000+输出 2500 tokens 为 0.013–0.026 元/封。后续接入需另按用户需求进行。

## 上轮增量：编码、同步计数与邮件翻译（2026-09-28）

- 当前运行版已切换到 `ScreenshotApp/bin/mail-translation-segments/Release/XTool.exe`；桌面、任务栏和原有 Run 入口已更新并读回核对，旧构建保留。
- MailService 在 MimeKit 首次使用前注册 CodePagesEncodingProvider。合成邮件复现 GB2312 被按西文解码；修复后五种编码回归通过，真实企业邮登录提醒标题、发件人与正文已在运行界面确认恢复正常中文。
- 同步改为 SEARCH ALL 后按 UID 拉取最新 200 封；缺失摘要时保留旧列表。底部显示 IMAP 可见收件箱总数、已同步数和当前筛选数。真实企业邮 IMAP 仍只返回 2 封，尚未确认网页客户端收取范围；已请用户检查最近 30 天/全部设置。不要宣称已解决网页与 IMAP 的数量差异，不排查已解决的认证问题。
- 正文工具栏新增翻译图标，复用现有离线英译中 ONNX 模型，仅翻译阅读区标题和正文，发件人保持原样；再次点击恢复原文及原格式，翻译期间可取消，切信取消并清理译文。正文超 20,000 字符暂不整封翻译，不截断后伪装成功。
- 用户发现长营销邮件产生大量 `<unk>`。新 MailTranslation.cs 清理零宽/组合字素连接等预览填充，保留段落并将输入限制为短句；网址、邮箱、表情及中文段落保持，异常/重复输出逐段回退原文并提示。共享 TranslationEngineProvider.cs 的既有其他任务改动未修改。
- 真实模型合成测试：旧流程对不可见填充生成 575 字符含 `<unk>` 输出；新流程处理带填充、不同内容的多段长邮件和标题约 2.6 秒，末段与网址保留，无异常词元。仍有品牌名和专业措辞误译，未声称更换了模型。用户报告的那封真实长邮件尚待用户重试，因窗口存在用户输入停止自动点击。
- 验证：Release 0 警告/0 错误，Mail.Tests 49 项通过；`artifacts/mail-encoding-translation-check` 验证最小/常规布局、标题/正文切换、发件人保持、切信清理及真实本地模型。当前英译中资源约 238 MiB；替代模型仅查资料，未下载安装。
- 未清理、暂存、提交或推送。仍保留大量其他任务改动。

## 已确认结论

用户已确认腾讯企业邮箱连接成功。此前认证被拒绝是用户输入的邮箱地址不正确，不是授权码错误，也不能归因于认证前 ID 或密码空白处理。不要继续围绕已解决的故障修改认证机制。
QQ、Gmail、腾讯企业邮箱已实现；用户此前确认 Gmail 成功，本轮确认学校腾讯企业邮箱成功。未对所有新邮件通知、网络重连场景重新做端到端验收。

## 工作目录与运行版本

- 实际代码仓库：`D:\Claude Code\X-Tool`。聊天默认目录可能是 `C:\Users\MRSW\Documents\ChatGPT\X-Tool`，不要误改另一个目录。
- 当前运行：`D:\Claude Code\X-Tool\ScreenshotApp\bin\mail-enterprise-diagnostics\Release\XTool.exe`，交接时确认进程 PID 9760（PID 会变化）。
- 桌面 X-Tool.lnk、已固定任务栏快捷方式、既有 HKCU Run/X-Tool 均已指向此版本。旧产物保留。
- HEAD：`28a51f3`；大量混合已修改/未跟踪内容，邮箱目录和测试可能仍未跟踪，不能只看 git diff。
- 用户此前要求不提交代码，本轮未暂存、提交或推送。不要 git reset/clean、覆盖或清理其他任务改动，不要 git add .。
- 文件 UTF-8，保留原 BOM/编码；PowerShell 先 chcp 65001 并设置 UTF-8 输出，Get-Content -Encoding UTF8；注释中文。

## 当前功能与已接受界面

- WPF 三栏邮箱：账户、邮件列表、正文；蓝紫粉统一浅渐变、圆角裁剪、自定义标题栏。
- 账户卡片仅保留官方服务图标、显示名称、邮箱地址及状态光点，保持简洁。绿色连接正常，红色失败，琥珀色正在连接/等待。
- 备注可选，40 字以内单行。QQ/Gmail/TencentExmail 均可在添加时填写，也可选中后点左下角“账户设置”，或右键账户卡片“编辑备注与账户设置”。备注优先作为标题，清空后恢复服务名称；邮箱地址不变。单封邮件通知标题也使用备注。
- 账户/列表/正文栏可拖动调整，layout.json 保存布局。不要覆盖用户已存栏宽。
- 列表像素滚动+动画，底部渐隐提示到末尾撤除；保持虚拟化。此前仅合成滚轮验收，未宣称用户实际设备滚动手感完全验收。
- 官方发件人图标本地打包，按域名边界匹配，不依据显示名；没有远程头像请求，图标不等于身份认证。
- 文本与 HTML a 链接蓝色可点击，仅允许 http/https 且不含 userinfo；默认浏览器在用户点击后打开。无网页脚本和远程图片。
- 验证码/授权码基于上下文高亮，保留原文；不是任意数字均高亮，复杂间隔语句仍可能漏识别。

## 收信实现与安全边界

- MailKit/MimeKit 4.17.0，IMAP 993 SSL/TLS，保留系统证书校验。
- QQ: imap.qq.com；Gmail: imap.gmail.com；TencentExmail: imap.exmail.qq.com。
- 腾讯企业邮箱使用客户端专用密码，认证前发送 X-Tool ID；QQ 原认证后 ID。未改为扫码登录或 OAuth。
- Gmail/企业邮专用密码清除 Unicode 空白；QQ 沿用原处理。认证错误只显示预定义类别，不展示服务端完整原文或凭据。
- 默认直连（可沿用系统 TUN），可显式选择 Windows 系统代理；不能把浏览器可达当作 IMAP 端口可达。
- 每账号近期 200 封摘要，最多 10 账号；IDLE 或轮询，首次同步不追发历史提醒，UID 去重，断线退避。
- 正文查看不自动标已读；按钮标已读会写服务器。没有发送、回复、删除、完整文件夹同步，不要新增假按钮。
- DPAPI 当前用户加密：`%LOCALAPPDATA%\X-Tool\Mail\mail.bin`。不要读取/导出真实密码，不要删除账户缓存排障。
- 独立阅读连接复用，缓存最近 12 封/累计 32MB 原始大小/10 分钟，仅内存。首次仍下载完整 MIME（包括附件），单封20MB上限；未完成按正文部分优先下载。

## 文件导航

- `ScreenshotApp/Mail/MailModels.cs`：账户 Provider/Remark、Host、DisplayName 与兼容默认值。
- `MailService.cs`：持久化、认证/错误分类、收信、备注保存、正文缓存。
- `MailAccountDialog.cs`：添加和账户设置、三种服务切换、备注字段。
- `MailWindow.xaml/.cs`：三栏、账户卡片、右键入口、筛选及布局。
- `MailStyles.xaml`、`MailSmoothScroll.cs`：样式与平滑滚动。
- `MailIdentity.cs`、`Assets/Mail/NOTICE.md`：官方图标来源与匹配。腾讯企业邮图标来自 https://exmail.qq.com/exmail_logo.ico。
- `MailDocument.cs`、`MailCodeHighlighter.cs`：正文、安全链接、高亮。
- `ScreenshotApp/App.Mail.cs`：通知和宠物集成。
- `Mail.Tests/Program.cs`：核心回归；`artifacts/mail-ui-smoke`：WPF 合成布局与交互。

## 最近验证与复现命令

最近 Release 构建 0 警告/0 错误；34 项核心回归通过；WPF smoke 包含资源加载、企业邮切换及官方图标、最小布局、链接安全、验证码、平滑滚动与底部渐隐通过。真实学校邮箱登录由用户确认成功。

在实际仓库运行：
```powershell
dotnet build ScreenshotApp/ScreenshotApp.csproj -c Release --no-restore -p:OutputPath=bin/mail-enterprise-diagnostics/Release/ -v:minimal
dotnet run --project Mail.Tests -c Release
dotnet run --project artifacts/mail-ui-smoke/MailUiSmoke.csproj -c Release
```
运行版本占用文件时改用新的独立 OutputPath；smoke csproj 的 HintPath 需要同步指向待验证构建。不要覆盖正在运行的程序。
合成预览 `artifacts/mail-ui-smoke/enterprise-add.png`；其中没有用户凭据。
重启前若用户在添加/设置中输入，先让用户保存或关闭。只停止已核对路径的主进程，不停止 Path 为空的辅助进程；确认没有活动手机传输，再更新既有快捷方式及自启动入口，不擅自开启自启动。

## 尚未实施或仅讨论过的事项

1. 网易 163/126/Yeah 接入只做过可行性说明，尚未实现。若用户后续要求，可在现有提供商/备注模型上扩展，需核对 IMAP 与客户端 ID 要求。
2. 通用自定义学校邮箱/服务器与 Google OAuth 未实现。不是所有教育邮箱都用腾讯，不能仅凭 edu.cn 自动指定提供商。
3. 日程可视化月历/友好的甘特式排布是先前需求，本轮未实施；现有手机日程同步代码属于其他工作，开始前另读对应历史交接。
4. 手机电脑投屏控制方案被用户暂停，不要自行恢复。
5. 首次大型邮件按正文部分加载、真实滚动手感、完整布局恢复实测属于后续可选验证，不应与已通过的合成测试混淆。

下一窗口先确认目录、Git 状态、当前源码与运行路径，再根据用户下一条具体需求继续。不要把以上待办全部视为自动执行指令。
