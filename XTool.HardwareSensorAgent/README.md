# XTool.HardwareSensorAgent

这是 X-Tool 的独立只读管理员硬件传感器代理，目标框架为 .NET 8 x64，使用
LibreHardwareMonitor 采集 CPU、GPU、内存、主板、存储、控制器与电源传感器。

代理不开放网络端口、不接受任意命令或文件路径，也不会修改硬件配置。主程序通过固定计划任务按需启动
`--serve --protocol 2 --sid <当前用户 SID>`；代理读取 30 秒内有效的单次启动请求，验证 SID、主程序 PID、启动时间、完整路径与会话，
进程用户、随机数与管道前缀后，只向主程序创建的命名管道写入 `hello`、`status`、`snapshot` 和 `error`
消息。主程序退出或管道断开后代理立即退出。

安装入口为
`--install --source <代理包目录> --manifest <UTF-8 JSON 清单> --requesting-sid <当前用户 SID>`。
清单采用共享协议中的 `HardwareAgentBundleManifest`，逐项记录文件名、长度与 SHA-256；管理员进程会在复制前后校验，并把同一清单写入受保护版本目录。清单用于保证本次提升前后的文件一致，正式发布仍应对主程序和代理进行 Authenticode 签名以建立发行来源信任。

取消授权入口为 `--uninstall --requesting-sid <当前用户 SID>`，只结束并删除当前用户的最高权限任务；受保护组件可能仍被其他 Windows 用户使用，因此保留到产品卸载时统一清理。退出码：`0` 成功、`1` 系统或任务计划错误、`2` 参数或协议错误、`3` 用户或路径授权不匹配、`4` 清单或文件完整性失败。

安装器只接受以下固定边界：

- 计划任务：`\X-Tool\HardwareSensors.v2.<当前用户 SID 哈希>`
- 安装目录：`%ProgramFiles%\X-Tool\Agents\HardwareSensors\<代理版本>`
- 任务无登录或开机触发器，仅允许按需启动，并以最高权限运行
- 安装包只复制明确列入白名单的运行文件，拒绝重解析点

该代理必须由 X-Tool 主程序完成安装、请求文件原子写入、当前用户专属管道创建和任务启动；不面向用户
直接运行。
