# X-Tool 开发交接文档

更新时间：2026-08-11

项目目录：`D:\Claude Code\X-Tool`

技术栈：WPF / C# / .NET 6（`net6.0-windows`）

当前分支：`main`

当前最新功能代码基线：`2c795ed 保留 GIF 合成开始通知`，其前依次为 `8d8e953 移除 GIF 合成过程进度通知`、`1d9474b 修复 GIF 采集收尾延迟与进度通知`、`71355f3 后台合成 GIF 并增加系统通知进度`、`3812f06 修复 GIF 收尾卡顿与录像菜单跳位`、`e2abcb2 新增截图区域 GIF 录制功能`、`3a6832a 调整截图工具栏居中并更新录像图标`、`ccc6354 拦截截图快捷键避免前台应用响应`、`782008d 避免截图捕获前台瞬态菜单`、`cf88468 加速长截图拼接与剪贴板写入`、`3648b99 修复截图标注面板与工具栏布局抖动`、`e2165f2 修复截图菜单定位与工具栏边界抖动`、`122cc68 稳定截图菜单定位并自动收起标注面板`、`5ea3236 修复截图工具栏定位与形状菜单跳动`、`b172131 截图形状工具默认矩形并扩展标注形状`、`b43be13 录像停止不再卡界面并支持OCR翻译复制后自动退出截图`、`b21cff9 协作文件卡列表框体与拖拽区阴影优化`。开发者工具、系统工具、转换器二维码、协作中心等既有模块均以历史提交为基线；文件工作台仍有未提交待确认改动。本次交接文档提交会位于功能基线之上，开始工作时仍须以最新本地提交和 `git log --oneline` 为准。

> 自旧基线 `8703c04` 之后，已经完成转换器 PDF/编码转换、离线语音输入、文件工作台、开机自启动、首页重设计和系统工具等多轮功能开发；开始下一轮前请以本文件与 `git log --oneline` 为准，切勿误以为只有交接文档发生变化。

> 本文档以当前代码为准。旧名称“截影 / JieYing”只可能残留在部分内部命名和本机配置目录中，不再代表当前产品定位。

> **当前工作区状态（2026-08-11）**：文件工作台的 Windows Search、完整扫描、重复文件与勾选式永久删除仍处于未提交待确认状态；相关 `FileWorkbench` 文件、`ScreenshotApp.csproj` 和新增搜索/重复文件服务必须继续保留。当前工作区还保留 `Translation\TranslationEngineProvider.cs`、`VoiceInput\VoiceInputService.cs` 的既有未提交改动；保护边界仍包括 `ScreenshotApp\App.xaml.cs`，这些文件下一轮不得顺带暂存、覆盖或提交。`ClipboardDiagnostics/` 仍是禁止修改、删除或提交的本地临时目录；`ScreenshotApp/VoiceInput/Runtime/` 的 GPU 试验材料已经用户确认删除，不再存在。屏幕工具最近的 `b172131`、`5ea3236`、`122cc68`、`e2165f2`、`3648b99`、`cf88468`、`782008d`、`ccc6354`、`3a6832a`、`e2abcb2`、`3812f06`、`71355f3`、`1d9474b`、`8d8e953`、`2c795ed` 已创建中文本地提交，包含形状扩展、工具栏定位防抖、菜单稳定展开/收起、落笔自动收起菜单、菜单锚点稳定、菜单自然尺寸缓存、工具栏自然尺寸缓存、长截图后台拼接、相邻帧像素缓存复用、WPF 原生剪贴板写入、截图前清理前台瞬态菜单、截图快捷键低级拦截、工具栏按选区水平居中、录像摄像机图标、GIF 录像模式选择、无音频帧采集、FFmpeg 调色板编码、取消清理、GIF 历史扫描、GIF 逐帧调色板收尾、录制面板稳定尺寸缓存、GIF 原始帧落盘与后台合成、FFmpeg 进度读取、后台收尾、仅合成完成通知以及合成开始通知。另需说明：`System.Data.OleDb` 包引用随体积优化提交 `5fe6188` 一并进入历史，该行本属文件工作台任务，重写历史风险较大故未回退，请知悉。

## 一、必须遵守的开发约定

### 文件与代码

- 所有文件读写保持 UTF-8；PowerShell 读取中文前先执行 `chcp 65001`，并使用 `Get-Content -Encoding UTF8`。
- 不使用 `sed` / `awk` 修改含中文文件，优先使用 `apply_patch`。
- 代码注释使用中文。
- 工作区可能存在用户自己的改动；只修改任务相关文件，不覆盖或清理无关内容。

### 构建与启动

正式项目：`ScreenshotApp\ScreenshotApp.csproj`。

```powershell
Set-Location 'D:\Claude Code\X-Tool'
Get-Process XTool,JieYing -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\ScreenshotApp\ScreenshotApp.csproj -c Release
Start-Process '.\ScreenshotApp\bin\Release\net6.0-windows\win-x64\XTool.exe'
```

- 当前程序集名与正式可执行文件均为 `XTool` / `XTool.exe`。
- 项目已固定 `RuntimeIdentifier=win-x64`（非自包含），Release 输出位于 `bin\Release\net6.0-windows\win-x64\`，原生库直接复制到该目录根；onnxruntime/OpenCvSharp 等 NuGet 包不再复制 win-x86、macOS、Linux、iOS、Android 等平台原生库，输出体积由约 1.50 GB 降至约 1.08 GB（减少约 420 MB）。启动与回归路径均以 `win-x64` 子目录为准。
- 构建前必须关闭正在运行的新版或旧版进程，否则 Release 文件可能被锁定。
- 完成功能后应关闭旧进程、Release 构建并启动新版；除非用户明确要求代理测试，实际 UI/功能验证由用户完成。较大且已验证的改动创建中文本地 Git 提交。
- 未经用户明确要求，不得推送 GitHub。
- 当前未跟踪目录 `ClipboardDiagnostics/` 是临时诊断程序，不属于正式产品，必须保持未提交状态。
- 当前未跟踪目录 `ScreenshotApp/VoiceInput/Runtime/` 是此前 GPU 语音加速试验留下的本地运行时材料；GPU 方案已回退到 CPU 版，不得提交、删除或重新接入该目录，除非用户明确要求。

### Git 检查

开始与结束时都执行：

```powershell
git status --short
git log -5 --oneline
```

提交时只暂存本次任务文件，禁止使用会误收 `ClipboardDiagnostics/` 的宽泛暂存方式。

### Markdown 文档状态

- `HANDOFF.md` 是当前唯一的开发交接基线；每轮较大功能结束后应更新日期、最新提交和对应模块状态。
- `THIRD-PARTY-NOTICES.md` 与各模型目录中的 README/UPSTREAM-README 属于许可证、模型来源或分发说明，必须保留。`ScreenshotApp/Models/Translation/zh-en/UPSTREAM-README.md` 当前由 `.gitignore` 明确忽略，仍作为本地上游来源说明保留，不要当作垃圾文件删除。
- 根目录 `design-qa.md` 与 `ScreenshotApp/design-qa.md` 是已完成界面迭代的一次性核验记录，包含临时截图路径和旧版界面结论，当前运行、构建和许可均不依赖；可在用户明确确认清理文档时删除。
- `NETWORK-WORKBENCH-PHASE3.md` 是已完成第三阶段的历史设计与验收记录，仍被本文网络章节引用，其中 ETW 精确流量描述属于历史实现且当前入口已移除；如需精简，应先把仍有价值的验收边界合并回本文，再删除该文件和引用。
- `ScreenshotApp/README.md` 仍使用旧产品名“截影”且只描述早期截图功能，内容已过时；它适合后续重写为当前 X-Tool 项目 README，不建议直接删除。

## 二、当前产品定位与主导航

产品已由单一截图工具扩展为桌面效率工具箱 **X-Tool**。

当前一级导航：

1. 首页
2. 屏幕工作台
3. 转换器工作台
4. 文件工作台
5. 网络工作台
6. 资源管理
7. 系统工具
8. 开发者工具
9. 协作中心
10. 设置

剪贴板不再是一级侧栏项，而是屏幕工作台的组成部分；首页与屏幕工作台均可进入剪贴板。快捷键不再是一级导航项，改为设置页中的可点击条目，点击后弹出毛玻璃快捷键设置窗口（`ShortcutSettingsWindow`）。

### 文件工作台（Windows Search 优先，完整扫描兜底）

- 入口：一级侧栏“文件工作台”。
- 指定文件夹后可按文件名关键词、类型和修改时间搜索；不再提供容易误解的最小/最大大小输入框。搜索只列出文件，会跳过无权限目录与链接目录。
- 可对选中文件（未选择时为当前结果全部）生成批量重命名、修改后缀、按类型分类、批量移动预览；确认后才执行。
- 重命名采用“前缀_编号”格式，自动避让已存在文件；分类目录为图片、视频、音频、文档、压缩包、其他。
- 默认“开始搜索”通过 Windows 公开只读 `Search.CollatorDSO.1` / `SystemIndex` 查询系统索引，不建立 X-Tool 常驻索引，也不会在 X-Tool 退出后保留自己的全盘目录库；因此已被 Windows 建立索引的 C 盘查询可瞬间返回。
- 该后端依赖 `ScreenshotApp.csproj` 中的 `System.Data.OleDb` 6.0.1；除非整体替换查询方案，不要移除该包引用。
- Windows Search 的查询必须读取 `System.ItemUrl`，再用 `Uri.LocalPath` 转回真实文件系统路径。**不得重新使用 `System.ItemPathDisplay` 做 `File.Exists` 或 `FileInfo`**：中文 Windows 会返回诸如 `C:\用户\...` 的本地化展示路径，导致索引明明命中、UI 却显示 `0 / 0`。
- 系统索引服务不可用、查询失败、指定目录未被索引或索引项不可直接读取时，自动回退到异步本地递归扫描；状态标签会显示“Windows Search”或“本地递归扫描”及原因。Windows Search 只覆盖系统已索引内容，不能替代未索引目录的完整直接扫描。
- 顶部“完整扫描”会明确绕过 Windows Search，对当前目录执行异步递归扫描；使用 `EnumerationOptions.IgnoreInaccessible=true` 并跳过重解析点。它扫描覆盖更完整，但会占用临时 CPU/磁盘时间，且仍受当前用户权限约束；不是常驻后台任务。
- 为避免百万结果拖慢 WPF，后端最多保留 5,000 项，超出时显示“至少 5,001 个文件”；界面按每页 200 项渲染。类型、时间筛选和多列排序只重排当前结果池，不重新访问磁盘；再次点击搜索才会重新查询索引或扫描。
- 文件搜索支持关键词、类型、修改时间和多列排序；表头可按“升序 → 降序 → 重置”循环，数字标记排序优先级。
- 普通搜索的批处理已将“移至回收站”替换为“删除文件”：选择该操作后，左侧每个结果前显示毛玻璃复选框，勾选状态随结果对象保存，可跨 200 项分页、筛选和排序保留；每次勾选都会立即同步到右侧永久删除预览，不需要再点“生成操作预览”。只有明确勾选的文件会进入计划，未勾选时绝不回退为处理当前结果池；执行前显示文件数与合计大小，并明确警告文件不会进入回收站且不可恢复。
- 文件结果区新增“文件搜索 / 重复文件”内部模式。重复文件模式独立递归枚举当前目录，不使用 Windows Search，也不受普通搜索 5,000 项结果池限制；先按大小分组，再做首/中/尾快速 SHA-256 摘要，最后对候选执行完整 SHA-256 校验。扫描跳过零字节文件、无权限目录、重解析点、扫描期间发生变化的文件和同一物理文件的硬链接别名，支持最小文件大小筛选、进度显示与取消。
- 重复查找可安全复用最近 5 分钟内同一目录的递归扫描文件清单，但仅限该次扫描无关键词/类型/时间筛选、结果计数精确且未被 5,000 项上限截断；Windows Search 结果不保证目录全覆盖，因此不会复用。复用只省略第二次目录枚举，候选文件仍必须读取内容并完成哈希校验。
- 重复结果按内容完全相同的分组展示，默认不自动选择任何文件；每项可勾选清理，每组可点“保留此项”并选择其他副本。重复模式隐藏普通搜索右侧批处理卡，结果区横向铺满，底部右侧提供红色“永久删除所选”按钮。每组必须至少保留一个文件；执行前明确显示数量、大小与不可从回收站恢复的强确认，确认后使用 `File.Delete` 永久删除，不进入回收站。普通文件搜索的“删除文件”也使用同一永久删除语义，但无需满足每组保留一项的重复文件约束。首版只判断二进制内容完全一致，不包含相似照片或近似视频。
- 重复结果组与组内文件行均使用放大的分层浅色毛玻璃卡片：组头显示内容校验说明与可释放空间胶囊，文件行分别显示文件图标、名称、完整路径、大小、日期、自定义蓝底白勾毛玻璃选择框和“设为保留”按钮。不要恢复为缺少层级的纯文本行或系统默认方框。
- 重复文件子项通过 Windows Shell `SHGetFileInfoW` 按扩展名读取实际关联图标，并由 `FileTypeIconProvider` 按后缀冻结缓存；ZIP、JAR、图片、视频、文档等不再共用统一文件图标。图标读取是装饰能力，Shell DLL、入口点或图像转换异常时必须回退通用图标，禁止让异常中断重复扫描。不得改为对每一行重复提取图标，以免大量结果时产生句柄和性能问题。
- 重复模式的最小文件大小筛选位于顶部目录操作栏；列表上方不再保留单独控制行。蓝色主按钮在空闲时显示“查找重复”，扫描期间原位变为“取消扫描”，再次点击即取消。结果组默认按单个文件大小从大到小排列，同大小时再按可释放空间降序。
- 普通文件搜索与重复文件子项的文件名均为可点击链接，悬停或键盘聚焦时显示蓝色强调。单击调用 `explorer.exe /select` 打开所在目录并选中文件，不直接执行文件，避免误启动 EXE、MSI、脚本或关联程序；文件不存在或 Explorer 启动失败时在当前页面显示提示。
- 重复校验对不超过 512 KB 的小文件直接执行一次完整 SHA-256，不再先快速摘要后第二次完整读取；大文件仍采用抽样摘要过滤后再完整哈希。该优化保持精确内容校验，同时显著减少大量小文件场景的重复打开开销。
- 当前验证：Windows Search 实际返回的 `file:C:/Users/...` 可经 `Uri.LocalPath` 转为 `C:\Users\...` 且 `File.Exists` 成功；Release 构建 0 错误。若构建时只出现 `NU1900` 漏洞数据下载中断警告，属于 NuGet 审计网络请求，不是项目编译错误。
- 首页已移除“最近使用”占位区域，改为当前主能力入口卡片。

### 资源管理与系统工具（已拆分）

- 左侧原“系统工具”已更名为“资源管理”，图标改为资源/性能样式，承载端口查看器、进程管理、服务管理、关联关系四个页面。
- 新增的“系统工具”保留原名称与芯片图标，承载“设备信息”“存储”“环境变量”“系统诊断”四个页面；入口默认打开设备信息页，不再与资源管理功能混在一起。
- 两个入口复用 `ScreenshotApp\SystemTools\SystemToolsView.xaml(.cs)`；资源管理使用默认模式，系统工具使用 `EnvironmentOnly=True`。修改时不要破坏两种入口各自的可见标签和默认页面。
- 设备信息页使用 Windows 公开只读接口显示静态系统与硬件规格（系统、处理器、显卡、主板、硬盘、显示器、内存、电池），无需管理员权限；下方独立列出 Windows 已识别的 PnP 驱动。驱动数据来自 `Win32_PnPSignedDriver` 与 `Win32_PnPEntity`，显示设备类别、提供商、版本、日期、INF、签名和设备状态，默认“关键设备”，可切换“全部设备”或“异常项”并搜索。驱动按类别收纳为默认折叠、可展开的毛玻璃卡片；搜索或查看异常项时，匹配类别会自动展开。蓝牙、USB、虚拟设备等不强行映射到上方硬件摘要。该清单不等同于所有内核/服务/筛选驱动或固件。
- “存储”页面已提供只读的容量初步概览：用 `DriveInfo` 展示已挂载固定磁盘的已用、可用与总容量，用 `Win32_DiskDrive` 展示物理磁盘型号、接口、介质类型与容量。它不读取 SMART、温度、寿命、健康度或任何实时传感器数据，也不尝试把卷强行映射到单块磁盘。
- “系统诊断”页面按用户点击读取 Windows `Win32_ReliabilityRecords` 可靠性记录，并用 `System` 与 `Application` 事件日志补充关机、蓝屏、WHEA、存储、驱动和服务细节；不读取 `Security`，不订阅实时事件，也不会自动修复或修改系统。时间范围可选最近 24 小时、7 天、14 天、30 天。应用崩溃、应用无响应、Windows 停止故障与 LiveKernelEvent 按可靠性监视器口径归为关键事件，不再要求底层事件日志必须是原生 Level 1；Windows 更新失败归为可靠性警告。
- 系统诊断规则覆盖异常关机、蓝屏、WHEA、存储与文件系统、驱动与设备、应用崩溃/无响应、服务失败和 Windows 更新异常。规则文案只提供排查线索，不把单条日志断言为硬件损坏；未命中规则的事件保留 Windows 原始级别和描述。扫描在后台执行，蓝色按钮原位切换“开始诊断/取消诊断”，切换标签或页面隐藏时取消；访问拒绝、日志缺失、事件描述资源缺失均不得中断其他日志结果。
- 系统诊断顶部使用参考 Windows 可靠性监视器的“异常事件时间格”：24 小时范围显示 24 个小时格，7 天、14 天与 30 天范围分别显示 7、14、30 个日期格，严格保持一列对应一小时或一天；相邻列使用交替浅色玻璃底纹，时间标签位于格网下方。格网、级别筛选与结果卡片统一使用规则修正后的最终等级，红、橙、蓝分别表示关键、错误和警告；点击列内任意位置都会选中整列时间段的全部事件，下方结果按关键、错误、警告排列，同级再按发生次数和最近时间降序。事件日志按最多 15 个时间段均匀读取、每段最多 400 条，总计最多 12,000 条，避免 30 天范围只保留最新一小段；可靠性记录最多读取 10,000 条，最终最多显示 300 个问题组。图表复用同一次诊断扫描保留的轻量时间点，筛选只在内存中重新分桶。
- “所选时间格事件”按关键、错误、警告分组显示，组头保留对应颜色、说明和组数；每条记录使用对应等级颜色的圆角描边和图标，但不再重复显示“关键/错误/警告”标签，只保留事件标题、身份信息、最近时间与发生次数。左右区域当前采用左侧 `0.88*`、右侧 `1.12*` 的比例，详情面板比事件列表更宽；不要恢复为左宽右窄布局。
- 右侧详情面板固定为标题、概览、可滚动内容和底部操作四个层级，使用较大的标题、说明、指标和正文尺寸。固定且价值较低的“建议处理”与“为何归类为当前等级”卡片已经移除；当前内容为事件摘要、可展开的 Windows 原始事件和当前范围次数。原始事件展开区标题栏最小高度 42，左右图标槽和内边距已加宽；正文框高度控制在 150–240，并在框体内部滚动，长路径或大量原始文本不得再次撑开外层面板、覆盖下方内容或溢出圆角框。
- 系统诊断复用 `System.Management` 6.0.0 读取 `Win32_ReliabilityRecords`，并依赖 `System.Diagnostics.EventLog` 6.0.0 使用 `EventLogQuery`/`EventLogReader` 分段补充日志；可靠性数据不可用时仍可退回事件日志规则识别。底部左侧按钮复制完整诊断详情；右侧主操作按事件类型变化：应用程序故障显示“打开程序位置”，从 Windows 原始事件的错误/故障应用程序路径调用资源管理器定位，路径缺失时明确提示；其他类别继续打开对应的 Windows 事件查看器日志。
- 应用程序故障在完成聚合后通过 `SystemProgramIdentityResolver` 解析程序身份，不对每条原始事件重复访问磁盘。解析综合可执行文件版本信息、公司名称、数字签名发布者、`Win32_Service` 服务显示名与常见 Windows 组件目录；例如 `MsMpEng.exe` 会显示为“Microsoft Defender 防病毒服务”，同时保留原始 EXE、发布者、事件来源和事件 ID。身份详情通过悬停与复制诊断信息提供；程序路径按路径缓存，服务目录缓存 10 分钟，识别失败时明确标注为仅依据事件名称，不得把文件名推测伪装成已验证身份。
- 当前系统诊断已连续通过 Release 构建（0 警告、0 错误）并启动新版验证；可靠性时间格、事件等级视觉、分组结果与详情布局已按实际 UI 截图迭代。仍需在不同窗口尺寸、长原始事件、四种时间范围以及普通权限日志访问失败场景继续人工回归。设备信息验证样本仍为“关键设备”10 类 / 149 项驱动，存储页读取到 3 个固定本地卷和 1 块物理 NVMe 磁盘；这些数量与容量仅代表本机样本。
- 实时温度、电压、频率、负载、功耗，以及创建或调用管理员传感器助手、计划任务的代码和 LibreHardwareMonitor 依赖均已移除；除非用户明确重新立项，不得恢复该链路。历史版本若已注册按需任务，新版本不会启动它；可在管理员权限下从任务计划程序删除该旧任务。
- 启动项管理页面，以及其启动项扫描、图标提取、启停、定位和管理员启动项操作代码均已移除。设置页中“X-Tool 自身开机启动”是独立功能，仍保留。
- 端口查看器：解析 `netstat -ano`，显示协议、本地地址、端口、占用进程、PID、状态；可按各表头点击排序，支持进程目录/结束进程右键操作。
- 端口页有关键词搜索、“显示系统进程”、“显示 IPv6”筛选；本地地址会截断显示并保留悬停文本，避免与端口列粘连。
- 进程管理：按同名进程折叠为应用组，展开后显示子进程；单进程应用会显示真实 PID；支持 CPU、内存、磁盘、网络和启动时间展示与表头排序。CPU 使用连续采样差分，不应长期固定为 0；进程结束操作会按需请求管理员权限。
- 服务管理：读取 Windows 服务，每行根据当前状态只显示“启动”或“停止”一个操作按钮；执行前确认，普通权限不足时通过一次性管理员子进程操作并返回真实结果。
- 关联关系：以运行进程为中心汇总 PID、端口、服务、网络活动、CPU 和内存；点击行后在相邻位置展开毛玻璃关系气泡，不替代端口、进程或服务专业页面。
- 环境变量：支持用户/系统范围查看、搜索与真实保存；`Path` 进入独立的分项编辑器，可新增、选择文件夹、移动、删除和保存。环境变量写入现已在后台等待广播与管理员子进程，不再同步阻塞 WPF 界面；系统变量仍只在用户明确确认后按需请求管理员权限。
- “自动刷新”已提升到系统工具页签右侧，是端口、进程、服务三个列表的共用开关，可选 3/5/10/30 秒；仅刷新当前可见页面。环境变量页不自动刷新，避免覆盖正在编辑的内容。
- 进程路径、部分启动时间或受保护进程的精确信息可能受 Windows 权限限制；列表仍应显示可获取的 PID、内存等基础信息。

### 开发者工具（扫描诊断与受控环境配置）

- 一级导航“开发者工具”进入独立的“开发环境中心”，首页原“设置”能力卡已替换为开发者工具入口；设置仍保留在左侧底部一级导航。
- 当前包含“开发环境总览”“SDK 与工具链”“托管安装”“环境诊断”四个页面，扫描支持 Java、Python、Node.js、.NET SDK、Git、Maven、Gradle、MySQL 与 Docker。项目环境、全局版本切换和 Shim 尚未开放；托管安装目前开放 Eclipse Temurin JDK、uv CPython、Volta Node.js 缓存、MySQL 与 Docker（Desktop 安装器 + docker CLI 静态包）。
- 页面首次进入后异步扫描，蓝色按钮原位切换“重新扫描/取消扫描”；离开页面时取消未完成任务。扫描失败按单个工具隔离，不阻断其他结果，也不会申请管理员权限。
- 发现来源限定为用户/系统持久 PATH、相关环境变量、Python/Git 注册表、常用安装目录，以及固定磁盘顶层名称匹配的有限目录；不会递归扫描整块磁盘、建立常驻索引或后台持续监测。不得改回直接使用 X-Tool 继承的进程 PATH 作为全局环境依据，因为 Codex、IDE 等启动宿主可能注入私有工具路径。
- Java、Python、Node.js、.NET 与 Git 的版本验证只运行已经解析出的绝对 EXE 路径，统一限制为 8 秒、64 KB 输出，并支持取消与终止进程树。WindowsApps 下的 Python 应用执行别名不会被自动运行，避免触发商店或安装流程。
- Maven 与 Gradle 的 Windows 入口通常为 CMD/BAT；当前只读取其 PATH/环境变量来源，并从安装目录 JAR 文件名识别版本，不通过 `cmd.exe` 自动执行脚本。
- 扫描通过文件句柄解析 Junction 与符号链接的最终路径，避免把 uv 的无补丁版本 Junction 和具体补丁目录重复计数；Java 读取版本命令返回的 `java.home`，并只从标准 `java/openjdk version` 行或 `java.version` 属性解析版本，不能把 `OpenJDK 64-Bit Server VM` 中的“64-Bit”误判为版本。扫描会合并 Oracle `javapath` 别名及 JDK 内置 JRE。固定盘有限发现会排除项目 `.venv`，也会排除 Codex 私有运行时。
- `.NET SDK` 的“当前”版本来自 `dotnet --version`，`--list-sdks` 仅用于列出并行 SDK；不得把列表第一项误判为当前版本，也不得将 SDK 选择解释为 PATH 顺序。
- 环境诊断当前覆盖用户/系统 PATH 重复项和失效目录、多版本命令入口、`JAVA_HOME` 与当前 `java.exe` 不一致、`python.exe` 与 `pip.exe` 可能不属于同一环境，以及 Node 目录名称与真实版本不一致。Node 版本不一致证据明确显示目录名称标示的主版本、命令实际版本和完整可执行文件路径；所有结论保留路径或命令输出证据。只有用户明确点击并确认后才会执行下述环境配置。
- 总览中的“未加入 PATH”可直接点击；SDK 清单在每个工具链标题的安装数量右侧统一提供一个“配置环境”按钮，并与明细行“打开位置”的右边界对齐，各安装项不再重复显示该按钮，具体版本在配置面板的“目标安装”中选择。配置面板留在开发者工具当前页面，默认写入系统变量并在确认后请求一次 UAC，也可切换为当前用户范围；写入完成后自动重新扫描，新环境只对之后启动的终端和程序生效。
- 配置环境弹层使用覆盖整个开发者工具主内容面板的圆角半透明遮罩，不遮挡左侧一级导航；目标安装与作用范围使用毛玻璃 ComboBox 及圆角下拉列表，下拉列表禁用无意义的水平滚动区域；SDK 与工具链、环境诊断等滚动区域统一使用细圆角毛玻璃滚动条。不得恢复为带内容边距的矩形局部遮罩或 Windows 原生下拉框/滚动条。
- 自动配置按工具链生成明确计划：Node/Git/.NET 使用已验证命令所在目录，Maven/Gradle 使用 `bin` 并设置对应 HOME，Java 只允许含 `bin\java.exe` 与 `bin\javac.exe` 的 JDK 并设置 `JAVA_HOME`，Python 添加解释器目录与存在的 `Scripts`，但拒绝 WindowsApps 别名和项目虚拟环境。检测到 Volta、nvm 或 fnm 时不把具体 Node 版本目录加入 PATH。
- 环境写入在管理员子进程中重新读取最新 PATH，跨用户/系统范围规范化去重，并用命名互斥量避免并发覆盖；失败时尝试恢复写入前的 PATH 与配套变量。不得退回把页面加载时取得的整段 PATH 直接交给管理员进程覆盖的实现。
- “托管安装”从 Adoptium API v3 动态读取当前仍提供 Windows x64 HotSpot JDK ZIP 的全部 Java 主版本；“推荐版本”包含官方 LTS 与官方最新特性版，“历史兼容版本”单独列出已经结束维护的短期版本，不能再退回只硬编码 8/11/17/21/25。“最新特性版”只表示 Adoptium 当前最新非 LTS 发布线，不代表本机正在使用；本机实际生效版本仍由开发环境总览和 SDK 扫描按 PATH 命令解析展示。官方 API 返回的 GitHub 发行地址和 64 位 SHA-256 仍是可信元数据基线；用户可选择 Adoptium 官方源或固定的清华 TUNA Adoptium HTTPS 镜像，镜像不可用时自动回退官方源，任何来源下载完成后都必须用 Adoptium API 的 SHA-256 校验，再进行防目录穿越解压并以绝对路径执行 `java -version`。JDK 下载进度显示已接收大小、平均速度和预计剩余时间；下载、校验、解压和验证均异步且可取消，不会自动修改 PATH 或 `JAVA_HOME`。
- uv Python 读取本机绝对路径 `uv.exe` 的官方可下载目录，当前查询 CPython 3.8–3.14 Windows x64 每个次版本的最新补丁：3.10–3.14 按 2026-08 的 Python 官方支持状态列入“推荐版本”，uv 仍提供的 3.8/3.9 列入带停止安全维护警告的“历史兼容版本”。历史版本不等于本机当前生效版本，安装确认框还会再次提示风险；未来调整支持分区时必须同步核对 Python 官方版本状态。只接受 uv 返回的 `astral-sh/python-build-standalone` GitHub HTTPS 发行地址与严格的 CPython Windows x64 key。安装通过 `uv python install` 写入 X-Tool 独立目录，并使用 `--no-bin --no-registry --no-config`，不会接管用户原有 uv Python、注册系统 Python 或创建全局命令入口；完成后仍以绝对 `python.exe --version` 验证。
- 托管根目录为 `%LocalAppData%\X-Tool\Dev`，下载临时文件、各工具安装、JSON 所有权清单和操作日志分别位于其 `Downloads`、`Java`、`Python`、`MySQL`、`Docker`、`managed-tools.json` 和 `Logs`。清单损坏时必须停止安装/卸载，不能猜测目录所有权；只有清单明确标记为 X-Tool 托管、位于对应托管根目录内且未被 PATH、配套环境变量（`JAVA_HOME`/`MYSQL_HOME` 等）或运行中进程引用的安装才允许永久删除，外部/MSI/手动安装一律不得直接删除。
- uv CPython 位于 `%LocalAppData%\X-Tool\Dev\Python\uv`；卸载必须再次调用已验证的 uv，并且同样要求清单所有权、目录边界、PATH 引用和运行进程检查全部通过。安装在清单写入前失败或取消时会调用 uv 回滚，已有但未记入清单的目录不得被自动接管。
- Node.js 版本目录由 `nodejs.org/dist/index.json` 提供，并与 Node.js 官方 Release 仓库的 `schedule.json` 交叉筛选，只显示当前日期已经发布且仍在支持期内的 Windows x64 主版本。页面不使用硬编码的长期版本表，因此当前样本为 Node.js 26、24 LTS、22 LTS，未来会随官方生命周期变化。
- Node.js 管理只调用已验证绝对路径的 Volta：未安装 Volta 时显示禁用的“需要 Volta”和独立“通过 WinGet 安装 Volta”入口，用户明确确认后才执行官方 `winget install --id Volta.Volta`，主程序保持普通权限；缓存 Node 只执行 `volta fetch node@版本`，不会调用会改变全局默认版本的 `volta install`，也不会修改 PATH。扫描发现 Volta 的 `node.exe` 中转入口时会通过同目录绝对路径 `volta.exe which node` 解析实际执行目标，并在 SDK 清单与报告中显示“中转入口 → 实际 node.exe”，不能再把 `C:\Program Files\Volta\node.exe` 误报为真实 Node 安装目录；由 `VOLTA_HOME` 确定的共享缓存版本也纳入有限目录发现。Volta 缓存可能被多个项目共享，X-Tool 只读标记“已缓存”，不直接删除 Volta 内部目录。
- Temurin、uv CPython、MySQL 或 docker CLI 静态包托管操作成功后会立即重新扫描；扫描确认对应绝对路径后自动切到“SDK 与工具链”，可直接查看并按现有受控规则配置环境。Docker Desktop 安装器由用户完成系统级安装，扫描确认后同样生效。若扫描尚未确认路径，页面必须明确提示，不能把安装成功等同于环境已生效。
- JDK 推荐/历史卡片中的 `Run.Text` 对只读展示属性必须显式使用 `Mode=OneWay`；否则官方目录返回后实例化历史卡片会抛出 `XamlParseException` 并终止进程。该问题已修复，后续新增内联绑定时不得恢复默认双向绑定。
- “打开位置”只打开已发现安装目录；“复制报告”输出当前工具链与诊断文本，不读取项目文件，也不包含密码、Token 或凭据。
- MySQL 托管版本与维护状态来自 endoflife.date，下载地址为 MySQL 官方 CDN 归档（`cdn.mysql.com/archives/`）；官方只提供 MD5 与 PGP 签名，安装按官方 MD5 校验并记录 `HashAlgorithm`。推荐区为 8.4 LTS 与 9.7 LTS，历史兼容区仅保留 8.0（已停止官方安全维护，安装前有警告）。MySQL 托管安装不初始化数据目录、不注册 Windows 服务、不修改 PATH；卸载同样要求清单所有权、目录边界、`MYSQL_HOME`/PATH/运行进程检查。
- Docker 托管分两种模式：“Docker Desktop 安装器”从官方 `appcast.xml` 读取最新版本，下载后做 Authenticode 数字签名校验并启动安装向导；它是系统级安装，需要管理员权限与 WSL2，X-Tool 不静默安装、不写入托管清单，已安装时只读展示本机版本。docker CLI 静态包从官方目录取最新稳定版，解压到 `%LocalAppData%\X-Tool\Dev\Docker` 并写清单，仅提供 CLI（无守护进程），容器引擎仍需 Docker Desktop 或远程 `DOCKER_HOST`。
- 环境诊断新增 MySQL 规则（`MYSQL_HOME` 与当前 `mysql.exe` 一致性、mysql/mysqld 是否同安装、EOL 警告）与 Docker 规则（守护进程可用性、Desktop 已安装但 PATH 缺失、WSL2 状态）；扫描版本解析兼容 MySQL 5.x 的 `Distrib 5.7.19` 与 8.x/9.x 的 `Ver 8.4.6` 格式，以及 Docker 的 `Docker version` 格式。
- 托管安装“查看日志”打开毛玻璃日志弹框（`ManagedLogWindow`），按成功/警告/错误级别展示最近 2000 条记录，支持刷新、复制、打开日志文件与日志目录。

### 协作中心（一期已实现，原生手机 App 为独立阶段）

- 一级导航新增“协作中心”，页面为 `ScreenshotApp\Collaboration\CollaborationView.xaml(.cs)`，服务为 `CollaborationService.cs`，手机端网页为内嵌资源 `CollaborationPage.html`。
- 电脑端以普通权限 `TcpListener` 自建极简 HTTP 服务（默认端口 18120，无 URL ACL 需求，零新依赖），同一局域网（同一 WiFi 或电脑热点）下手机可访问。
- 配对：服务启动生成 6 位 PIN 与配对地址，页面用 ZXing 生成二维码；手机扫码进入 `/pair?pin=xxx` 校验后获得 24 小时会话令牌，后续 API 均需令牌。
- 剪贴板桥：电脑外部复制（文本/图片）经现有捕获链路转发到服务（`PushClipboardText`/`PushClipboardImage`），手机网页每 3 秒轮询拉取并展示（文本可复制、图片可长按保存）；手机页面“发送到电脑”经 JSON/PNG 推送，由电脑写入系统剪贴板并记入剪贴板历史。页面提供“电脑→手机同步”玻璃开关；手机网页受浏览器限制无法后台监听剪贴板，读取手机剪贴板在支持 `navigator.clipboard` 的浏览器可用，否则降级为长按粘贴。
- 文件传输：手机网页 `PUT` 上传到 `%LocalAppData%\X-Tool\Transfer\Incoming`；电脑端“发送文件到手机…”复制到 `Outgoing` 目录，手机页面列出发送目录并下载；同名自动避让。
- 防火墙：服务运行中可点击“放行防火墙…”按需 UAC 添加 `netsh advfirewall` 入站规则；不放行时手机无法连接但服务仍可本机自测。
- 已知边界：`System.Text.Json` 默认把非 ASCII 字符转义为 `\uXXXX`（手机 JS 正常解码）；图片以 PNG base64 传输；网页端不支持后台自动读取手机剪贴板；原生 Android App（无障碍服务 + 前台服务实现后台剪贴板同步）与 iOS 能力边界分析已完成，作为独立阶段待开发。
- 当前验证：冒烟端到端 7 项全通过（配对页面令牌注入、状态、剪贴板拉取、手机推送、文件上传、列表、下载）；已修复局域网 IP 选择（排除 VMware/Hyper-V/VPN 等虚拟网卡，本机 WiFi 场景正确选中 `192.168.31.x`）并确认防火墙放行后手机可连接；人工验证手机端与电脑端双向剪贴板（文本）正常；图片同步与文件传输的人工验证待补。

### 网络工作台（第三阶段已完成）

- 一级导航“网络工作台”现包含网络总览、网卡与路由、代理与 DNS、Wi-Fi、网络方案五个页面；整体沿用浅色毛玻璃设计。网络总览内另保留独立弹出的“连接诊断”窗口。
- 网络总览使用 Windows 默认路由选择主用物理网卡，以默认网关、DNS、HTTP 三层探测判断联网状态；HTTP 会并行尝试直连与当前用户代理/系统网络路径，任一路径成功即视为可联网，并在详情中标明实际成功路径，避免启用代理时把“直连失败、代理可用”误判为互联网受限。网络变化事件会使缓存失效并刷新。曲线支持实时、15 分钟、1 小时、24 小时、7 天、30 天和 90 天，显示坐标、平均值、峰值和累计流量；实时曲线在新峰值出现时立即扩展纵轴，旧峰值移出窗口后平滑收缩，避免整图跳变。
- `NetworkMonitorCoordinator` 统一管理自动刷新、网络变化事件、即时刷新、速率差分、持久化和告警；监测器在网络工作台首次加载后贯穿应用生命周期持续运行，切走页面不会暂停采集、历史落盘或事件记录，返回时直接渲染已累积的最新状态与曲线。手动与自动刷新共用单飞门，避免重复探测或 UI 卡死。
- 最新两项网络修复：`137b912` 取消切页时停止监测器、隐藏页面时跳过样本累积的问题；`0588227` 为实时曲线采用“新峰值立即扩展、旧峰值平滑回落”的纵轴策略，避免 60 秒窗口内峰值移出时整图突变。后续改动不得恢复“每次按当前窗口最大值硬重标尺”的实现。
- `NetworkHistoryStore` 使用本地 SQLite、WAL 和单写入队列保存分钟流量、网关/DNS/HTTP 探测及网络事件；数据库位于 `%LocalAppData%\X-Tool\Network\network-history.db`。保留期可选 1、7、30、90 天，支持二次确认清除和 CSV 历史导出。
- 网络事件覆盖监测启动、断网/恢复、物理链路与主用网卡变化、代理/DNS 变化、Wi-Fi 漫游、DNS 连续失败、HTTP 高延迟、持续高上传和每日流量预算；阈值、预算与静默时段可配置，并采用连续命中与 10 分钟去重。
- 网络总览新增毛玻璃“连接路径”示意：按本机、默认网关、已配置代理路径、DNS/互联网、目标服务展示状态、速率与延迟；代理不可作为有效路径时会保留节点并标注直连/代理实际探测结果，不将代理配置本身误当作流量必经路径。右上“异常记录”弹出独立毛玻璃窗口，汇总中断、连续探测失败和高延迟等记录。
- 网络总览的“连接诊断”按钮会独立打开 `NetworkDiagnosticsWindow`，支持 Ping、DNS、TCP、HTTP、路由追踪和可取消的完整诊断；完整诊断覆盖默认路由、DNS、目标 TCP/HTTP 与代理，可导出 UTF-8 报告。耗时操作不得在 UI 线程同步等待。
- 顶部旧“连接诊断”和“流量与连接”页面已移除；旧 ETW 精确流量入口不再在主程序 UI 中触发，相关代码保留为禁用状态。除非用户明确要求，不要重新启用 GPU 语音运行时或恢复 ETW 入口。
- 代理与 DNS 支持当前用户代理、PAC、WinHTTP 与主用网卡 DNS 的读取和管理；机器级操作通过一次性 UAC 子进程完成，不要求主程序长期管理员运行。
- “网卡与路由”页面在顶部展示物理/虚拟网络接口、地址、网关、DNS、MTU 与接口跃点；下方保持 IPv4/IPv6 路由表和 Windows 防火墙配置文件左右各半的只读布局。网卡写操作均有影响提示、确认和错误反馈。
- Wi-Fi 已作为独立页面，展示当前摘要、按信号排序的可用 Wi-Fi 列表、所选网络的可读取属性，以及全宽的 2.4 GHz/5 GHz 信道分布图。未连接网络只展示扫描可得的 SSID、BSSID、信号、协议/安全、频段与信道；IP、DNS、网关、链路速率等仅连接后可读取。信道图可切换频段，显示名称、图例和坐标，避免恢复为拥挤的星型图。
- 网络方案支持代理、DNS、DHCP/静态 IPv4、前缀、MTU 和接口跃点快照；字段可选应用，网卡不匹配时阻止执行，应用后自动探测并可立即回滚。恢复点跨重启保留 24 小时，支持不含凭据的 UTF-8 JSON 导入导出。
- ETW 依赖 NuGet `Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.21，以兼容当前 .NET 6 目标。完整实现与验收边界见根目录 `NETWORK-WORKBENCH-PHASE3.md`。

全局快捷键：

- `Ctrl + Shift + A`：启动普通截图。
- `Ctrl + Shift + V`：不唤起主窗口，打开剪贴板快速选择浮窗。
- `右 Alt`：启动/结束本地离线语音输入；不会因短暂停顿自动结束，单次录音最长 90 秒。

全局快捷键可在“快捷键”页点击后重新录入，支持截图、剪贴板选择粘贴和语音输入。保存前检查 X-Tool 内部重复、已知 Windows 保留组合及 Windows 已注册的外部全局热键；Windows 无法枚举其他软件未注册的低级键盘钩子。语音默认仍为右 Alt，也可改为标准组合键。

## 三、屏幕工作台现状

### 截图与标注

- 普通截图支持框选、八方向调整、涂鸦、形状、箭头、直线、颜色与线宽共享设置、取色、文字提取、翻译、长截图、录像和贴图。
- 标注工具栏与各级面板采用当前浅色毛玻璃设计。
- 形状按钮点击后默认进入矩形绘制；形状菜单图标尺寸统一，支持矩形、圆角矩形、圆形、菱形、三角形、五边形、六边形、星形、箭头和直线。实时预览与最终截图共用同一套几何规则。
- 工具栏定位优先跟随选区下边界；下方有空间时始终位于选区下方，空间不足时移动到下边界上方，并通过稳定窗口尺寸、缓存工具栏自然尺寸与迟滞避免拖动上下手柄时闪到左上角或来回跳动。顶部手柄调整不改变工具栏的纵向位置。
- 涂鸦/形状菜单按当前空间优先向下展开，无空间时向上展开；菜单可重复展开/收起，开始在选区内落笔后自动收起。菜单在选区调整期间会跟随工具栏重新定位。
- 普通截图、长截图、文字提取、翻译和屏幕录制均可在剪贴板页面分类查看。

### 贴图

- 普通截图工具栏已有“贴图”按钮，不再是待开发功能。
- 当前选区可生成独立、可拖动贴图窗口，支持同时存在多个窗口。
- 每个贴图右上角有关闭与图钉按钮。
- 设置页提供“贴图默认置顶”；代码默认值为 `true`，单个贴图仍可临时切换置顶状态。
- 贴图及截图/录像辅助窗口使用 `WdaExcludeFromCapture`，不进入后续截图或录像画面。
- 关键文件：`ScreenshotApp\Sticker\StickerWindow.cs`、`ScreenshotApp\Capture\SelectionOverlayWindow.xaml(.cs)`。

### OCR、翻译与录像

- OCR：本地 ONNX 推理。
- 翻译：本地 ONNX 英译中模型。
- 录像：Media Foundation H.264 MP4，WASAPI 音频采集；包含倒计时、区域边框、控制条与视频封面。工具栏录像按钮使用摄像机图标，普通录像路径保持原有音频与停止逻辑。
- GIF 录像已接入录像按钮二级选择（“普通录像 / 录制 GIF”）：GIF 无音频，默认 12 FPS、最长 10 秒、最大宽度 960 像素，并通过 FFmpeg rawvideo 管道与逐帧 palettegen/paletteuse 编码；支持停止、Esc 取消、编码超时、失败清理和历史缩略图扫描。逐帧调色板模式避免等待整段输入后再合成，收尾期间控制条会显示“正在合成 GIF…”，最长录制时会将显示补到 10 秒。项目已经随发布包内置 FFmpeg（当前 `ffmpeg.exe` 与 `ffprobe.exe` 合计约 194 MiB），本次不新增运行时依赖。GIF 文件本身会显著大于 MP4，后续如需开放时长、帧率或尺寸设置，必须继续保留上限。
- 相关辅助窗口均需继续保持排除捕获能力。

### 本地语音输入

- 使用 `org.k2fsa.sherpa.onnx` 1.13.4 与固定版 SenseVoice int8 模型，在本机完成录音与识别，不上传音频或文本。
- 录音使用 NAudio 的 16 kHz 单声道输入；录音期间约每 1.2 秒对当前音频快照进行一次本地识别，彩虹气泡直接显示最新片段，不再提前写入目标程序；再次按快捷键或录音达到 90 秒时结束，再将完整识别结果一次性粘贴到原输入窗口。若最终解码暂时没有结果但气泡已有文本，会使用预览文本兜底粘贴。按 Esc 会取消当前语音输入且不写入任何内容；也可在设置中改为仅复制。
- 模型位置：`ScreenshotApp\Models\VoiceInput\default\`。权重单文件超过普通 GitHub 限制，已被 `.gitignore` 排除；发布安装包/Release 时必须带上 `model.int8.onnx`、`tokens.txt`、`LICENSE` 与 `XTOOL-MODEL-MANIFEST.txt`。
- 关键文件：`ScreenshotApp\VoiceInput\VoiceInputService.cs`、`VoiceInputOverlayWindow.xaml(.cs)`、`MainWindow.xaml(.cs)`。

## 四、剪贴板现状

### 保存与分类

- 监听 Windows 剪贴板更新，将非 X-Tool 产生的文字或图片保存为“外部复制”。
- 应用写入剪贴板时附加 `X-Tool.InternalClipboard` 标记，避免重复归类为外部复制。
- 剪贴板页面分类包括：全部、普通截图、长截图、文字提取、翻译、屏幕录制、外部复制。
- 快速浮窗分类包括：全部、图片、文字、翻译、文本提取、外部复制；按时间倒序展示。
- 浮窗使用非激活显示以尽量保留原输入窗口焦点，选择记录后写入系统剪贴板并向原窗口发送粘贴操作。

### 已解决的粘贴问题

- 最终仍采用标准 Windows 系统剪贴板链路，没有改用高风险的进程注入方案。
- `ClipboardService` 使用 WinForms `SetDataObject(..., true, 3, 25)`：最多 3 次、每次间隔 25 ms，用于避开 Explorer 等程序的正常短锁。
- 文字和图片均已实际验证可以粘贴。
- `ClipboardDiagnostics/` 仅用于当时定位占用窗口；诊断代码不进入正式提交。

关键文件：

- `ScreenshotApp\Clipboard\ClipboardService.cs`
- `ScreenshotApp\Clipboard\ClipboardPickerWindow.xaml(.cs)`
- `ScreenshotApp\History\ScreenshotHistoryStore.cs`
- `ScreenshotApp\MainWindow.xaml(.cs)`

## 五、设置与存储位置

设置页当前提供：

- 截图后自动复制
- 保存截图历史
- 贴图默认置顶
- 本地语音输入开关与识别后自动粘贴开关
- 可展开的分类存储位置

默认目录：

| 分类 | 默认位置 |
| --- | --- |
| 截图 | `E:\截影\Screenshots` |
| 长截图 | `E:\截影\LongScreenshots` |
| 文字提取 | `E:\截影\History\文字提取` |
| 翻译 | `E:\截影\History\翻译` |
| 屏幕录制 | `E:\截影\Recordings` |
| 外部复制 | `E:\截影\Clipboard` |

- 转换器不保存固定目录；图片、音频和视频任务都由用户每次手动选择输出位置。
- 偏好文件当前仍保存在 `%LocalAppData%\JieYing\preferences.json`。这是兼容旧版本的内部遗留命名，若以后迁移到 `X-Tool`，必须设计兼容迁移，不能直接改路径导致用户设置丢失。

## 六、转换器工作台现状

转换器工作台拥有独立的竖向二级工具栏，目前包含图片、音频、视频、转 PDF 和编码转换五个图标入口。顶部始终保留“转换器工作台”标题与介绍；选择工具只替换右侧工作区。

### 1. 图片处理（已可用）

支持输入/输出：PNG、JPEG、BMP、TIFF。

能力：

- 多文件选择与队列展示。
- 格式转换。
- JPEG 压缩质量调整。
- 按比例缩放。
- 每次任务手动选择输出目录。
- 转换前后预览。

预览不是静态占位图：

- 输出格式、质量、缩放滑杆变化都会进入 `UpdateImageConversionControls()`。
- 该方法调用 `UpdateImagePreviews()`。
- `ImageConversionService.CreatePreview()` 会按当前格式、缩放比例和 JPEG 质量重新生成“处理后”预览。

注意：预览框使用固定视口，改变尺寸时画面仍会填充相同区域；JPEG 高质量区间的视觉差异也可能很小，因此用户可能误以为没有刷新。后续可增加像素尺寸、预计体积或刷新状态，使变化更明确。

关键文件：

- `ScreenshotApp\MainWindow.xaml(.cs)`（图片处理页仍在主窗口内）
- `ScreenshotApp\Converters\ImageConversionService.cs`

### 2. 音频处理（已实现）

输入：MP3、WAV、M4A、AAC、FLAC、OGG、OPUS、WMA。

输出：MP3、WAV、M4A、FLAC、OGG、OPUS。

已实现：

- 拖放/多文件选择。
- 文件名、格式、大小、时长、编码、状态列。
- ffprobe 获取媒体信息。
- 音质预设：高质量 320 kbps、标准 192 kbps、小文件 128 kbps、语音 64 kbps。
- 预设切换会动态更新说明文字。
- 高级设置：比特率、采样率、声道、保留元数据。
- 异步队列、实时进度、取消、失败原因、打开输出目录。
- 最新音频页已统一毛玻璃下拉框、展开卡、复选框和主按钮样式。

关键文件：`ScreenshotApp\Converters\AudioConverterView.xaml(.cs)`。

### 3. 视频处理（已实现基础功能，UI 待继续统一）

输入：MP4、MOV、MKV、AVI、WebM、FLV、WMV。

输出：MP4、MOV、MKV、WebM、GIF；提取音频可输出 MP3、M4A、WAV。

已实现：

- 视频格式转换与压缩。
- 提取音频。
- 视频转 GIF，可设置时间范围、FPS 与宽度。
- 分辨率、FPS、编码、时长、大小探测。
- 编码、码率、宽度等参数。
- 异步队列、实时进度、取消、失败原因、打开输出目录。

关键文件：`ScreenshotApp\Converters\VideoConverterView.xaml(.cs)`。

当前视频页功能逻辑已接通，但视觉控件尚未全面达到最新音频页的统一程度。下一轮适合先按音频页的控件资源和排版继续打磨视频页。

### 4. FFmpeg 引擎

统一入口：`ScreenshotApp\Converters\MediaConversionService.cs`。

查找顺序覆盖应用目录下 `tools\ffmpeg`、应用基础目录、当前工作目录及系统 `PATH`。必须同时找到 `ffmpeg.exe` 与 `ffprobe.exe` 才视为可用。

已实现：

- ffprobe JSON 解析。
- FFmpeg 异步执行，不阻塞 UI。
- `-progress pipe:1` 实时进度解析。
- CancellationToken 取消并终止进程树。
- 转换失败原因回传。
- 日志：`%LocalAppData%\X-Tool\Logs\media-conversion.log`。
- 服务层与页面层分离，后续可扩展随包引擎、GPU 编码、批量队列和任务调度。

项目现已随 Release 包直接分发固定版本的 Windows x64 FFmpeg 引擎：

- 位置：`ScreenshotApp\tools\ffmpeg\ffmpeg.exe`、`ScreenshotApp\tools\ffmpeg\ffprobe.exe`。
- 版本：FFmpeg 8.1.2 Essentials Build（Gyan.Dev），GPLv3，归档 SHA-256 为 `E25B682664025D49034C981AFB4BAE36238A40F29A3CC1C713AD9A8B5B3528F6`。
- 构建文件约 194 MB，仅保留运行所需的 `ffmpeg.exe`、`ffprobe.exe`；许可证与构建配置位于 `tools\ffmpeg\LICENSES`。
- Release 构建会自动复制该目录。正常安装无需下载或解压；若文件被删除，页面提示修复或重新安装 X-Tool。
- 已用短 WAV/MP4 实测探测及 MP3、HEVC、Opus、GIF 输出。仍应在发布前补齐不同来源、较长媒体和取消流程的人工回归。

发布到 GitHub 前，必须将项目许可证确定为 GPLv3，并在 Release 中提供与内置二进制精确对应的 FFmpeg 及启用外部库源码、构建配置与许可证材料。详见根目录 `THIRD-PARTY-NOTICES.md`。

### 5. 转 PDF（已实现基础功能）

入口：转换器工作台左侧第四个“转 PDF”图标，页面为
`ScreenshotApp\\Converters\\PdfConverterView.xaml(.cs)`。

已实现：

- 输入：PNG/JPG/JPEG/BMP/GIF/TIFF/WebP、已有 PDF、Word、Excel、PowerPoint、ODF 文档。
- 图片与已有 PDF 由 X-Tool 在本地生成或合并，不依赖办公软件。
- 输出支持按队列合并为一个 PDF，或按文件分别输出；同名输出会自动避让。
- 办公文档依次尝试 Microsoft Office COM、WPS COM、LibreOffice 无界面转换。
- 页面显示三类引擎的实时检测状态；若三者均缺失，仍允许图片/PDF 工作，并明确提示安装 LibreOffice 后再转换办公文档，不提供下载入口。
- 失败原因显示在队列对应文件项，取消会停止后续项目；LibreOffice 运行中的进程可被取消。

依赖：NuGet `PDFsharp` 6.2.4（MIT），已记录在根目录 `THIRD-PARTY-NOTICES.md`。

当前本机验证：Release 构建通过，PDF 页面与 Office/WPS/LibreOffice 检测提示已实际显示；本机检测到 Office，未检测到 WPS、LibreOffice。尚未在本机以真实办公文件完成 Office/WPS/LibreOffice 端到端导出，后续应分别补测 Word、Excel、PowerPoint 与 LibreOffice 回退。

### 6. 编码转换（已实现）

入口：转换器工作台左侧第五个“编码转换”图标，页面为
`ScreenshotApp\Converters\EncodingConverterView.xaml(.cs)`。

全部能力均为离线本地处理：Base64、URL Encode/Decode、Unicode 编码/解码、JWT Header/Payload 只读解析、Unix 秒/毫秒时间戳与日期时间互转、UUID v4 批量生成（1-100 个）。JWT 不上传内容且不验证签名；解析结果会明确显示这一点。

### 7. 二维码（已实现）

入口：转换器工作台左侧第六个“二维码”图标，页面为
`ScreenshotApp\Converters\QrCodeConverterView.xaml(.cs)`，服务与历史为
`QrCodeService.cs`。

已实现：

- 生成类型：自由文本/网址、WiFi（WPA/WEP/无密码并转义特殊字符）、名片 vCard、邮件、电话/短信（SMSTO）、批量生成（每行一条，支持“名称|内容”指定文件名）。
- 参数：纠错等级 L/M/Q/H、像素尺寸 256–1024、留白 0–8、前景/背景色板与 HEX 输入；预览实时异步生成并取消旧任务，不阻塞界面。
- 识别：支持多选图片文件与剪贴板图片，后台线程逐张解码、可取消、单张失败隔离；自动尝试旋转并支持同图多个二维码；结果按网址/邮件/电话/WiFi/名片/文本分类展示，网址可复制并在用户明确确认后打开。
- 历史：保存到 `%LocalAppData%\X-Tool\QRCode\history.json`，最多 500 条，按生成/识别筛选与关键词搜索、单条删除与清空；“保存历史”开关默认开启，关闭后不再落库。
- 依赖：NuGet `ZXing.Net` 0.16.11（Apache-2.0，零传递依赖，实际仅一个约 0.5 MB 的托管 DLL），已登记在根目录 `THIRD-PARTY-NOTICES.md`。
- 验证：生成→保存→解码闭环已独立验证通过（中文与 URL 内容一致）；输入预览跨线程闪退已修复（后台生成的位图在创建线程冻结后再回 UI 线程赋值），连续输入冒烟测试通过；Release 构建 0 警告 0 错误并启动新版。

## 七、关键代码地图

| 模块 | 位置 |
| --- | --- |
| 主导航、快捷键、历史、图片转换 | `ScreenshotApp\MainWindow.xaml(.cs)` |
| 用户设置 | `ScreenshotApp\Settings\AppPreferences.cs` |
| 普通截图与标注工具栏 | `ScreenshotApp\Capture\SelectionOverlayWindow.xaml(.cs)` |
| 长截图 | `ScreenshotApp\Capture\ScrollCaptureService.cs` 及相关窗口 |
| 贴图 | `ScreenshotApp\Sticker\StickerWindow.cs` |
| 剪贴板服务与浮窗 | `ScreenshotApp\Clipboard\` |
| 历史/剪贴板持久化 | `ScreenshotApp\History\` |
| OCR | `ScreenshotApp\Ocr\` |
| 翻译 | `ScreenshotApp\Translation\` |
| 屏幕录像 | `ScreenshotApp\Recording\` |
| 转换器二级导航 | `ScreenshotApp\Converters\ConverterToolRail.xaml(.cs)` |
| 图片转换引擎 | `ScreenshotApp\Converters\ImageConversionService.cs` |
| 音频页面 | `ScreenshotApp\Converters\AudioConverterView.xaml(.cs)` |
| 视频页面 | `ScreenshotApp\Converters\VideoConverterView.xaml(.cs)` |
| FFmpeg/ffprobe 服务 | `ScreenshotApp\Converters\MediaConversionService.cs` |
| 音视频队列模型 | `ScreenshotApp\Converters\MediaQueueItem.cs` |
| PDF 页面与任务队列 | `ScreenshotApp\Converters\PdfConverterView.xaml(.cs)`、`PdfQueueItem.cs` |
| PDF 转换与引擎回退 | `ScreenshotApp\Converters\PdfConversionService.cs` |
| 编码转换 | `ScreenshotApp\Converters\EncodingConverterView.xaml(.cs)`、`EncodingConversionService.cs` |
| 二维码生成、识别与历史 | `ScreenshotApp\Converters\QrCodeConverterView.xaml(.cs)`、`QrCodeService.cs` |
| 文件工作台 UI 与批处理 | `ScreenshotApp\FileWorkbench\FileWorkbenchView.xaml(.cs)`、`FileWorkbenchService.cs` |
| Windows Search 查询后端 | `ScreenshotApp\FileWorkbench\WindowsSearchFileSearchBackend.cs`（`System.ItemUrl` + `Uri.LocalPath`；勿改回 `System.ItemPathDisplay`） |
| 重复文件内容校验 | `ScreenshotApp\FileWorkbench\DuplicateFileFinderService.cs` |
| Windows 后缀关联图标缓存 | `ScreenshotApp\FileWorkbench\FileTypeIconProvider.cs` |
| 普通搜索与重复文件永久删除 | `ScreenshotApp\FileWorkbench\FileWorkbenchService.cs`、`FileWorkbenchView.xaml(.cs)` |
| 开发环境扫描、工具链列表、托管安装与环境诊断 | `ScreenshotApp\DeveloperTools\DeveloperToolsView.xaml(.cs)`、`DeveloperEnvironmentScanner.cs`、`DeveloperEnvironmentModels.cs`、`SafeDeveloperCommandRunner.cs`、`ManagedToolchainModels.cs`、`ManagedToolchainService.cs` |
| 协作中心（配对、剪贴板桥、文件传输） | `ScreenshotApp\Collaboration\CollaborationView.xaml(.cs)`、`CollaborationService.cs`、`CollaborationPage.html` |
| 系统工具（设备信息、折叠 PnP 驱动清单、存储容量概览、环境变量、系统诊断） | `ScreenshotApp\SystemTools\SystemToolsView.xaml(.cs)`、`SystemToolsService.cs`、`SystemDiagnosticService.cs`、`SystemDiagnosticModels.cs`、`SystemDiagnosticRules.cs`、`SystemProgramIdentityResolver.cs` |
| 网络工作台主页面 | `ScreenshotApp\NetworkWorkbench\NetworkWorkbenchView.xaml(.cs)` |
| 网络状态与方案服务 | `ScreenshotApp\NetworkWorkbench\NetworkWorkbenchService.cs` |
| 统一监测与历史库 | `ScreenshotApp\NetworkWorkbench\NetworkMonitorCoordinator.cs`、`NetworkHistoryStore.cs` |
| ETW 精确流量 | `ScreenshotApp\NetworkWorkbench\NetworkEtwTrafficCollector.cs` |
| 网络告警配置 | `ScreenshotApp\NetworkWorkbench\NetworkAlertSettingsWindow.xaml(.cs)` |
| 网络深度诊断与 Wi-Fi | `ScreenshotApp\NetworkWorkbench\NetworkWorkbenchView.xaml(.cs)`、`NetworkDeepToolsService.cs` |
| Win32 接口 | `ScreenshotApp\Capture\NativeMethods.cs` |

## 八、当前已知限制与建议顺序

1. **网络第三阶段人工矩阵**：代码与普通权限 UI 已完成并通过 Release 启动验证；发布前仍应在真实 UAC 环境分别验证代理/DNS/网卡配置、网络方案应用失败后的回滚、局域网扫描取消，以及断网/切网/休眠恢复。顶部 ETW 入口当前已移除，不得通过自动化静默接受 UAC 或擅自恢复该入口。
2. **音视频人工回归与视频页统一**：已内置 FFmpeg；发布前用短 MP3/WAV/MP4/MOV 分别验证探测、进度、取消、输出与日志。视频功能逻辑已接通，视觉控件仍可继续向最新音频页统一。
3. **FFmpeg 源码发布材料**：GitHub Release 必须同步发布与内置二进制一致的源码、构建配置和许可证材料。
4. **图片预览反馈增强**：预览已实时生成，但尺寸/质量变化不总是肉眼明显；可显示实际输出像素与预计大小。
5. **配置目录迁移**：`%LocalAppData%\JieYing` 是遗留目录，只能在有兼容迁移方案时更名。
6. **回归保护**：继续确保贴图、截图辅助窗、录像边框和控制条不进入捕获画面。
7. **转 PDF 人工回归**：分别验证图片合并、已有 PDF 合并、Word/Excel/PowerPoint 的 Office 导出，以及未装 Office/WPS 时 LibreOffice 回退和缺失提示。
8. **资源管理与系统工具权限/性能回归**：受保护进程、服务启动/停止和系统环境变量写入需分别在普通权限与管理员权限下验证；端口自动刷新不应造成页面卡顿，设备与 PnP 驱动信息应能在普通权限下刷新，且需确认资源管理与系统工具入口没有串页。系统诊断需验证 24 小时/7 天/14 天/30 天范围、分类与级别筛选、扫描取消、重复事件聚合、时间格整列选择、详情滚动、长原始事件、复制详情、非应用事件的事件查看器跳转，以及应用故障的友好程序名、发布者、身份悬停和“打开程序位置”；普通权限下单个日志、服务目录或受保护程序文件访问失败不得弹 UAC、阻断扫描或丢弃其他日志结果。
9. **文件工作台索引覆盖回归**：分别验证已索引目录的“开始搜索”、Windows Search 关闭/未覆盖目录时的自动回退、以及“完整扫描”的取消与无权限目录跳过；不得为了追求秒级全盘搜索而恢复同步 UI 回填、常驻扫描或自建全盘实时索引。
10. **重复文件与删除回归**：使用包含同名不同内容、不同名相同内容、大文件、硬链接、被占用文件和无权限文件的目录验证重复分组、取消与失败提示；验证普通搜索选择“删除文件”后才显示玻璃复选框，勾选可跨分页、筛选和排序保留并实时同步右侧预览，未勾选时删除按钮不可用，确认后永久删除且不进入回收站；重复文件页必须每组至少保留一个文件，红色按钮永久删除明确勾选的副本且不会进入回收站，需重点验证强确认、部分失败反馈和删除后列表刷新。
11. **开发环境中心回归**：在未安装工具、同一工具多个版本、PATH 含中文/空格/重复/失效目录、WindowsApps Python 别名、项目虚拟环境、版本管理器入口、版本命令超时和普通权限目录不可读场景验证扫描、取消、证据与失败隔离；分别验证系统/用户 PATH 的确认、UAC 取消、去重、失败回滚、写入后自动重扫及界面不冻结。Temurin 托管安装还需人工验证大文件下载进度、取消清理、错误哈希拒绝、`java -version` 验证、清单损坏保护，以及被 PATH/JAVA_HOME/运行进程引用时拒绝卸载；uv Python 需验证本机无 uv 时的降级提示、3.10–3.14 目录、安装取消/回滚、与外部 uv 安装隔离、`python --version` 验证和 uv 安全卸载；Volta Node.js 需验证无 Volta 降级、WinGet/UAC 取消、安装后重检、受支持版本生命周期筛选、`volta fetch` 不改变默认版本、下载取消和缓存版本只读显示。不得擅自加入静默安装、外部安装目录删除、Volta 缓存目录删除、全局版本切换或 Shim。
12. **二维码人工回归**：分别验证自由文本/网址、WiFi、vCard、邮件、电话/短信与批量生成的预览、保存、复制与中文内容扫码；验证参数变化（纠错、尺寸、留白、前景/背景色）实时刷新且不卡界面；用多二维码图片、旋转图片、含中文的二维码、损坏图片和无二维码图片验证识别、取消、失败隔离与分类徽章；验证网址打开的确认拦截、历史保存开关、搜索筛选、单条删除与清空；确认“保存历史”关闭后生成/识别不落库。
13. **协作中心人工回归**：同一 WiFi 与热点两种网络下验证扫码配对、PIN 失效更换、24 小时会话、电脑→手机文本/图片同步、手机→电脑文本推送并写入系统剪贴板、双向文件传输与同名避让、停止服务后手机断连、防火墙未放行时的提示，以及“电脑→手机同步”开关关闭后仅手机→电脑可用；网页端需覆盖不支持 `navigator.clipboard` 的浏览器的降级路径。

## 九、最近关键提交

- `e2abcb2` 新增截图区域 GIF 录制功能
- `3a6832a` 调整截图工具栏居中并更新录像图标
- `ccc6354` 拦截截图快捷键避免前台应用响应
- `782008d` 避免截图捕获前台瞬态菜单
- `cf88468` 加速长截图拼接与剪贴板写入
- `3648b99` 修复截图标注面板与工具栏布局抖动
- `e2165f2` 修复截图菜单定位与工具栏边界抖动
- `122cc68` 稳定截图菜单定位并自动收起标注面板
- `5ea3236` 修复截图工具栏定位与形状菜单跳动
- `b172131` 截图形状工具默认矩形并扩展标注形状
- `b43be13` 录像停止不再卡界面并支持 OCR 翻译复制后自动退出截图
- `b21cff9` 协作文件卡列表框体与拖拽区阴影优化
- `3f1e2ab` 新增协作中心局域网配对剪贴板桥与文件传输
- `aa50fd3` 新增转换器二维码生成与识别工具
- `4135b2f` 开发者工具新增托管日志弹框与MySQL、Docker托管支持
- `27764a6` 快捷键设置移入设置页并改为弹窗
- `dde2e6d` 修复托管JDK版本识别
- `371085f` 区分工具链版本并扩展Python历史目录
- `5e7d6fb` 修复托管目录加载崩溃
- `a7c0a36` 完善托管环境联动与下载管理
- `9d14429` 调整Volta安装按钮宽度
- `11381b4` 接入Volta管理Node版本
- `1dd0246` 接入uv托管Python安装
- `5d96bf6` 新增Temurin托管安装基础能力
- `5e516e3` 对齐工具链操作并完善诊断证据
- `db3baeb` 统一工具链环境配置入口
- `26df675` 修复环境配置下拉占位区域
- `ba8a9c3` 统一开发环境弹层与滚动样式
- `817cbf0` 新增开发工具环境一键配置
- `8d04687` 修正开发环境识别与诊断依据
- `0ec0d87` 新增开发环境扫描与诊断中心
- `8de56d3` 完善系统诊断程序身份识别
- `413b25e` 完善应用故障程序定位操作
- `e9d2ebc` 精简系统诊断详情布局
- `da9b5d8` 恢复截图工具栏取色入口
- `e097dfb` 更新系统诊断开发交接文档
- `efdfbc8` 修正系统诊断原始事件框体
- `43790e3` 优化系统诊断详情可读性
- `b449c1a` 调整系统诊断结果与详情布局
- `aef3e0b` 重构系统诊断事件详情布局
- `1335755` 统一系统诊断事件等级视觉
- `e72387f` 对齐系统诊断与可靠性监视器
- `174021e` 完善系统诊断十四天视图与等级排序
- `a20771b` 完善系统诊断每日时间格交互
- `4ceb9ac` 重构系统诊断异常时间格
- `6f41950` 完善存储空间分析与文件搜索体验
- `8f6530f` 修复截图完成后主界面抢焦点
- `c8a56b3` 修复存储用量条与语音粘贴稳定性
- `056a0fa` 更新系统工具交接说明
- `35cccaa` 完善驱动分类与存储概览
- `eff0240` 新增设备驱动信息页面与存储占位
- `ae31d5a` 移除系统工具启动项管理
- `c74d231` 重构启动项管理界面
- `9cd7341` 放大硬件信息清单
- `7c7a3e4` 放大硬件信息标识
- `1891ebc` 移除硬件实时监控助手
- `bbfdff4` 拆分资源管理与系统工具入口
- `e54e456` 优化网卡区域与关联排序
- `0674edc` 合并网卡与路由页面
- `3e99d60` 重构网络工作台顶部页面
- `5df6b63` 在总览新增连接诊断弹窗
- `0588227` 平滑实时流量曲线缩放
- `137b912` 修复网络监测切页冻结
- `81cb4da` 回退 SenseVoice CUDA GPU 加速，当前语音输入为稳定 CPU 方案
- `c414fde` 完成网络工作台第三阶段与按钮样式优化
- `654a406` 完成网络工作台历史监测底座
- `4969e45` 修复网络曲线与完整诊断卡死
- `63f46c9` 完成网络工作台第二阶段并规划第三阶段
- `1c61ce6` 启动网络工作台第二阶段采集升级
- `bd1eb0f` 修复网络状态误判与刷新延迟
- `a099b0f` 完成网络工作台第一阶段
- `5d713f0` 统一系统工具表头排序与自动刷新
- `e049905` 优化端口列距与进程信息展示
- `2212bcf` 完善端口查看筛选与自动刷新
- `aee3829` 完善端口列表字段与表头排序
- `6943b75` 优化进程管理分组展示
- `d03e8b8` 新增 Path 环境变量分项编辑
- `e817c69` 修复系统工具排序方向图标
- `55a4c7d` 统一系统工具表头与滚动条样式
- `fea7212` 完善系统工具列表筛选与排序
- `73a952b` 新增系统工具模块
- `215376f` 重新设计首页功能入口
- `7eaf204` 新增开机自启动设置

- `8703c04` 优化音频转换页面样式与信息布局
- `afad7a7` 新增音视频转换工作台
- `99c0938` 更新 X-Tool 桌面图标
- `c45200b` 校正图片列表格式与大小表头位置
- `b6780ea` 修正图片处理表头与预览反馈
- `0a45acd` 实现图片处理实时预览与列对齐
- `8d7d64a` 新增转换器工作台与图片处理
- `6bf7d07` 修正剪贴板分类配色映射
- `d1e366a` 修复剪贴板粘贴与分类展示
- `cc788dd` 新增剪贴板管理与快捷粘贴
- `0453065` 新增普通截图贴图功能

## 十、新窗口开始前检查清单

1. 完整阅读本文件。
2. 执行 `git status --short` 与 `git log -5 --oneline`，确认基线和用户未提交内容。
3. 保留 `ClipboardDiagnostics/` 未跟踪且不提交。
4. 保留 `ScreenshotApp/VoiceInput/Runtime/` 未跟踪且不提交、不删除。
5. 检查是否已有 `XTool.exe` 进程；构建前关闭。
6. 只处理新任务涉及的文件。
7. Release 构建通过后启动新版；默认由用户进行实际 UI/功能验证，除非用户明确要求代理测试。
8. 用户确认较大改动已验证后，只暂存任务相关文件并创建中文本地提交；不推送远端，除非用户明确要求。

## 十一、可直接用于新窗口的提示词

```text
请继续开发 D:\Claude Code\X-Tool 的 X-Tool WPF 项目。

先完整阅读项目根目录 HANDOFF.md，并严格遵循其中记录的当前基线、UTF-8 文件处理要求、构建与启动方式、提交约定、工作区保护规则和各模块现状。开始前执行：

git status --short
git log -5 --oneline

当前 main 最新功能提交应以 `3812f06 修复 GIF 收尾卡顿与录像菜单跳位` 为首，后接 `e2abcb2 新增截图区域 GIF 录制功能`、`3a6832a 调整截图工具栏居中并更新录像图标`、`ccc6354 拦截截图快捷键避免前台应用响应`、`782008d 避免截图捕获前台瞬态菜单`、`cf88468 加速长截图拼接与剪贴板写入`、`3648b99 修复截图标注面板与工具栏布局抖动`、`e2165f2 修复截图菜单定位与工具栏边界抖动`、`122cc68 稳定截图菜单定位并自动收起标注面板`、`5ea3236 修复截图工具栏定位与形状菜单跳动`、`b172131`、`b43be13`、`b21cff9`；其上可能还有本次更新交接文档的中文本地提交。请以实际 git log 为准，不要假设工作区干净，不要 reset、checkout、清理或丢弃任何现有改动。

`ClipboardDiagnostics/` 是未跟踪的本地临时目录，必须保留，禁止提交、删除或修改；`ScreenshotApp/VoiceInput/Runtime/` 的 GPU 试验材料已经确认删除，不要重新下载或接入。`ScreenshotApp/App.xaml.cs`、`ScreenshotApp/Translation/TranslationEngineProvider.cs`、`ScreenshotApp/VoiceInput/VoiceInputService.cs` 是与后续任务无关的既有修改，禁止顺带暂存、覆盖或提交。文件工作台当前仍有未提交的 Windows Search、完整扫描、重复文件查找和永久删除功能；相关 `ScreenshotApp/FileWorkbench/` 文件、`ScreenshotApp/ScreenshotApp.csproj` 与新增服务必须完整保留，也禁止顺带提交。未经我明确要求不要推送 GitHub。

开发者工具现有“开发环境总览”“SDK 与工具链”“托管安装”“环境诊断”四页。扫描支持 Java、Python、Node.js、.NET SDK、Git、Maven、Gradle、MySQL 与 Docker，使用持久 PATH、环境变量、注册表、常用目录及固定盘顶层有限发现，不得改成全盘递归或常驻监控。版本验证只能运行已解析的绝对 EXE，带超时、输出上限、取消和失败隔离。Java 必须从标准 `java/openjdk version` 行或 `java.version` 解析版本，不能再次把 `OpenJDK 64-Bit Server VM` 中的“64-Bit”识别为版本；MySQL 需兼容 `Distrib 5.7.19` 与 `Ver 8.4.6` 两种输出。

总览“未加入 PATH”和 SDK 清单“配置环境”可在当前页应用受控配置，默认写系统变量并按需请求 UAC，也可切换用户范围。Java 配置 JDK `bin` 与 `JAVA_HOME`；Python 添加解释器目录及存在的 `Scripts`，不设置 `PYTHONHOME`；Node 在检测到 Volta/nvm/fnm 时不得把具体版本目录写入 PATH。写入管理员子进程必须重新读取最新 PATH、规范化去重、加命名互斥量并在失败时尝试恢复，不能用页面加载时的旧 PATH 覆盖系统变量。

“托管安装”已接入 Temurin JDK、uv CPython、Volta Node.js、MySQL 与 Docker。Temurin 版本来自 Adoptium API，支持官方源与固定清华 TUNA 镜像，下载后始终按官方 SHA-256 校验；“最新特性版”只是官方最新非 LTS 发布线，不代表本机当前生效。uv Python 显示 3.10–3.14 推荐版本以及 uv 仍提供的 3.8/3.9 历史兼容版本，历史版本安装前必须提示停止安全维护。Volta 只执行不改变默认版本的 `volta fetch`，不得改成 `volta install`；缓存目录可能被多个项目共享，禁止直接删除。MySQL 版本来自 endoflife.date 与官方 CDN 归档，按官方 MD5 校验，推荐 8.4/9.7 LTS、历史区仅 8.0。Docker Desktop 安装器来自官方 appcast 并做数字签名校验，只下载并启动安装向导，不得当作绿色安装写入清单或静默安装；docker CLI 静态包可托管到 X-Tool 目录。托管安装或卸载只允许操作 X-Tool 清单明确拥有且通过目录边界、PATH/环境变量及运行进程检查的内容，禁止删除外部安装。不得擅自加入静默安装、全局版本切换、项目绑定或 Shim。

文件工作台默认“开始搜索”使用 `Search.CollatorDSO.1` 查询 `SystemIndex`，必须读取 `System.ItemUrl` 并通过 `Uri.LocalPath` 获取真实路径，不能改回已本地化的 `System.ItemPathDisplay`。顶部“完整扫描”绕过系统索引并异步递归扫描；结果池最多 5,000 项、每页 200 项，筛选和排序只处理当前结果池。不要恢复同步 UI 回填、常驻扫描或自建全盘实时索引。

系统工具只保留静态设备信息、PnP 驱动、只读存储概览、环境变量和按需只读系统诊断。系统诊断按 Windows 可靠性监视器口径提供最近 24 小时、7 天、14 天和 30 天时间格，事件按关键、错误、警告分组；应用故障通过 `SystemProgramIdentityResolver` 解析友好程序名、原始 EXE、发布者、Windows 服务和身份依据，主操作为“打开程序位置”。不得恢复实时传感器监控、启动项管理、管理员助手、计划任务或 LibreHardwareMonitor。

网络工作台顶部 ETW 精确流量入口已经移除，不要恢复。语音输入保持 CPU 方案，不得重新接入 `ScreenshotApp/VoiceInput/Runtime/` 中的 GPU 运行时。屏幕截图标注工具栏的独立“取色”按钮位于“形状”之后，已接回选中状态和点击处理，不要再次隐藏。形状按钮默认选择矩形，形状菜单支持圆角矩形、五边形、六边形和星形等扩展形状；工具栏定位优先跟随选区下边界，菜单按空间优先向下、无空间向上展开，开始落笔后自动收起菜单。后续修改不得恢复使用瞬时布局尺寸导致的左上角闪跳或菜单重复开关异常。

若需要继续优化 UI，请保持现有浅色毛玻璃设计语言，不要破坏已实现的异步、取消、失败提示、日志、单飞刷新、历史持久化和权限确认逻辑。任何 ETW、系统网络配置、防火墙、服务、受保护进程或系统环境变量写操作都只能在用户明确点击后按需申请管理员权限，不得让主程序默认长期以管理员运行。

完成代码修改后关闭 XTool/JieYing，执行：

dotnet build .\ScreenshotApp\ScreenshotApp.csproj -c Release

Release 构建通过后启动：

.\ScreenshotApp\bin\Release\net6.0-windows\win-x64\XTool.exe

实际 UI 测试默认由我完成。修改完成后只暂存本次任务相关文件，使用恰当的中文名称创建本地 Git 提交。不要提交现有文件工作台改动、`ClipboardDiagnostics/` 或其他无关文件，不要推送远端。
```
