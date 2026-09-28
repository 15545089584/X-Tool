# HA 连接专项回归

独立链接智能家居连接层源码，避免改动已有混合修改的全量回归入口。

```powershell
dotnet run --project SmartHome.ConnectionTests/SmartHome.ConnectionTests.csproj -c Release
```

自动回归只使用本机随机端口的模拟 HA 和测试令牌，不启动 XTool，不改变真实设备、系统代理、用户凭据或持久化设置。覆盖私网代理绕行边界、REST/WS 初始化、连续握手失败释放、取消、连接超时、刷新重试和认证失败停止重试。

需要验证真实 HA 时可显式运行：

```powershell
dotnet run --project SmartHome.ConnectionTests/SmartHome.ConnectionTests.csproj -c Release -- --live-ha
```

此模式只读取当前 HA 地址和 Windows 凭据，在内存中认证，读取配置、注册表与实体状态，订阅状态事件后关闭连接；不执行服务调用、不输出令牌、不修改缓存或设备。公网 HA 保留默认代理；Tailscale 域名和私网 IP 绕过代理。网络请求/握手上限 30 秒，生产服务整次初始化预算 90 秒；命令发送与应答共用 20 秒预算。

这组测试不代表高丢包中继网络和真实设备控制已通过长期运行验收。
