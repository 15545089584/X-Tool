# 截影 Windows 截图工具

基于 WPF 与 Windows 原生桌面捕获能力开发的本地截图工具，默认将截图复制到剪贴板，并保存到 `E:\截影\Screenshots`。

## 当前包含

- 毛玻璃主界面、系统托盘与单实例运行
- 全局快捷键 `Ctrl + Shift + A` 进入统一截图入口
- 框选后支持涂鸦、矩形、撤销、像素级取色、文字提取与离线翻译
- 在同一工具栏中切换为手动滚动长截图，无需再次框选
- DXGI Desktop Duplication 截图，GDI 作为兼容回退
- 手动滚动长截图、上下往返去重、固定顶栏识别和逐帧拼接
- 截图自动复制、保存与历史记录
- 自定义标题栏、窗口动画与高 DPI 适配

## 运行

```powershell
dotnet run --project .\ScreenshotApp.csproj
```

## 回归测试

```powershell
dotnet run --project ..\ScreenshotApp.RegressionTests\ScreenshotApp.RegressionTests.csproj -c Release
```
