# 截影 Windows 截图工具

基于 WPF 与 Windows 原生桌面捕获能力开发的本地截图工具，默认将截图复制到剪贴板，并保存到 `E:\截影\Screenshots`。

## 当前包含

- 毛玻璃主界面、系统托盘与单实例运行
- 全局快捷键普通截图和滚动长截图
- 普通截图涂鸦、矩形、撤销和像素级取色器
- 普通截图 PP-OCRv5 本地文字提取、结果编辑与一键复制
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
