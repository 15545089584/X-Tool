# PawnIO 随高级硬件监控安装方案

更新时间：2026-08-13

## 目标与边界

目标是在部分笔记本的基础只读访问无法返回 CPU、内存或主板温度时，随 X-Tool 高级硬件监控授权安装官方 PawnIO，让 LibreHardwareMonitor 获得可使用的低层只读访问能力。它不是保证所有设备都能提供更多温度；EC 布局、固件权限与传感器本身仍可能限制结果。

本方案不包含风扇控制、SMBus 写入、超频、EC 写入或任何硬件设置变更。无有效传感器时继续显示“不受支持”，不以 0°C、估算值或插值替代。

## 已核验的现状

- `LibreHardwareMonitorLib 0.9.6` 已包含 `LibreHardwareMonitor.PawnIo.PawnIo` 的安装、加载与版本检测接口；现有高权限代理会继续使用同一只读采集链路。
- 固定随包版本：PawnIO 2.2.0，官方安装器 `PawnIO_setup.exe`，长度 3,410,960 字节，SHA-256 为 `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`。
- 已在纳入发布包前验证安装器的 Authenticode 签名有效，签名主体为 `namazso.eu`；运行时固定哈希校验防止被替换。
- 对应源码标签为 `2.2.0`，提交 `5cdf470831fdfff3f7f1d06363ca6b230f3bf35a`。发布包必须同时提供该标签及其子模块的完整对应源码、GPL-2.0 文本与本文件，不能只发布驱动安装器。
- 发布物随附 `tools/pawnio/sources/PawnIO-2.2.0-source.zip`（SHA-256 `93AA5D410B76C71E9004CAC406ED19D0550A735410A9ABE4D0C9A838B8B98EAC`）和 `PawnPP-e64e4c37-source.zip`（SHA-256 `20AA0638B95C90D296935310F3F28CC22DE1FB24855C5BAA6721E2AE95EB6042`）。
- GamePP 使用 HWiNFO 组件，奥创使用 ASUS 签名服务/驱动；两者均不是可由 X-Tool 复制或调用的公共依赖。

## 安装与权限方案

1. PawnIO 2.2.0 官方安装器随受 SHA-256 清单保护的高级传感器代理包发布。
2. 用户首次点击“授权并自动监控”后，X-Tool 只申请一次管理员权限；管理员代理验证固定长度、哈希和发布目录后，交互式启动官方安装器。不会传入未经文档验证的静默参数，也不会绕过 Windows 驱动签名或安全策略。
3. 已检测到 PawnIO 时不重复打开安装器。已授权的 `XTool.HardwareSensorAgent` 按原有当前用户 SID、命名管道和 SHA-256 清单边界启动；普通权限主程序不会直接打开驱动设备。
4. 代理只把受控的“未安装 / 已检测到官方组件，正在以传感器结果验证 / 无法判断”摘要发回 UI；不传递异常堆栈、驱动路径、令牌或底层设备细节。
5. 取消 X-Tool 的硬件监控授权只删除 X-Tool 自己的计划任务和代理文件，不会卸载 PawnIO；产品级卸载应调用官方卸载路径并在确认后完成回滚。

## 兼容性与风险控制

- 驱动必须由官方签名安装器处理 Windows 驱动签名、内存完整性和企业安全策略；安装器拒绝时不绕过、不关闭安全功能。
- PawnIO 作为高级硬件监控的默认依赖。安装失败、驱动未加载或与设备不兼容时，监控自动回退到当前 LibreHardwareMonitor 基础只读访问。
- 同一时间不要与 HWiNFO、GamePP 等通用底层监控工具同时做采样，避免 SuperIO/EC 并发访问；ASUS 奥创服务保持系统默认，不由 X-Tool 停止或替换。
- PawnIO 源码采用 GPL-2.0 及独立模块例外。X-Tool 分发其安装器时，发布渠道必须随附对应完整源码、GPL-2.0 许可证、版本/哈希清单和安装/卸载说明；未来变更版本必须重新完成签名、来源、安装、卸载和回滚审计。

## 验收与回滚

1. 首次授权：固定安装器的哈希验证通过、用户完成官方交互式安装且任务注册成功；取消安装时不得创建或更新 X-Tool 的高级监控任务。
2. 无 PawnIO 时：页面明确显示基础只读访问，现有 GPU/磁盘温度保持可用，缺失类别仍为“不受支持”。
3. 安装后：重新采样的诊断须显示已检测到 PawnIO；只有代理实际返回有效数值时才显示温度与曲线。
4. 卸载 PawnIO 或系统策略阻止加载后：重新采样回退基础只读访问，不影响 X-Tool 主程序、计划任务或静态设备信息。
5. 实际 UI 和不同机型覆盖由用户人工验证；若 CPU/内存/主板仍无读数，应以代理传感器数量和访问状态判断为固件/EC 不支持，而非伪造读数。

## 参考

- PawnIO 官方站点：<https://pawnio.eu/>
- PawnIO 源码与许可证：<https://github.com/namazso/PawnIO>
- PawnIO 2.2.0 固定源码标签：<https://github.com/namazso/PawnIO/tree/2.2.0>
- LibreHardwareMonitor：<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>
