# 热点连接回归

桌面网段选择测试（项目根目录）：

```powershell
dotnet run --project Collaboration.ConnectionTests/Collaboration.ConnectionTests.csproj -c Release
```

Android 接口筛选、定向广播、来源地址与掩码测试（AndroidApp 目录）：

```powershell
$env:ANDROID_HOME='D:\AndroidSDK'
.\gradlew.bat testDebugUnitTest assembleDebug --no-daemon
```

真机验收需手机开热点、电脑接入，检查使用原配对凭据自动发现新 IP，断开/重开热点后恢复；再分别检查普通 Wi-Fi、移动网络 Tailscale 回退、手动连接和关闭自动连接开关。

发现请求不携带凭据；只枚举本地私有 IPv4 接口，最多 8 个，不扫描整段主机。每个接口绑定本地地址发送定向广播，共享接收预算。回复取报文来源 IP，验证电脑 ID 与已配对会话后再保存。状态探测有 5 秒总超时，不复用文件传输的长超时。

单元测试不能替代 OriginOS 热点接口可见性、VPN 分流和 Windows UDP 18121 防火墙的真机验收；本次不修改网络或防火墙规则。
