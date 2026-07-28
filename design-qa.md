# 硬件信息面板设计验收

- source visual truth path: `C:\Windows\TEMP\codex-clipboard-53e6e8ea-1385-47c8-900d-9d2008e7026c.png`
- implementation screenshot: Computer Use 会话内窗口截图（未持久化为本地文件）
- viewport: 1480 × 900
- source pixels: 2048 × 1155
- implementation pixels: 1480 × 900
- density normalization: 按可见内容区域和信息层级比较，未进行像素级缩放
- state: 系统工具 / 硬件信息 / 静态只读信息

## Full-view comparison evidence

窗口截图确认硬件信息页采用参考图的信息结构：系统、处理器、显卡、主板、硬盘、显示器、内存和电池均采用“型号 + 关键属性 + 设备明细”。页面保留项目既有浅色毛玻璃主题，并仅保留单列静态硬件信息。

## Focused region comparison evidence

可访问性快照验证了 24 核（8P+16E）/32 线程、双显卡、8 GB 独显、FX607JIR、954 GB NVMe、2560 × 1600 / 165 Hz、双通道 DDR5-5600 和 GA50358 电池等字段。首轮发现 Windows 名称和显卡顺序与参考图不一致，已经改为读取 WMI 系统名称并优先排列独立显卡。

## Findings

- [P2] 修正后的最终窗口未能再次截图
  - Location: 硬件信息面板
  - Evidence: 复查时检测到用户正在操作目标窗口，自动界面控制按安全规则停止。
  - Impact: 已通过编译和数据快照验证，但无法完成最终像素级对照。
  - Fix: 用户停止操作窗口后重新捕获相同视口并复查 Windows 名称、显卡顺序和滚动密度。

- [P3] 厂商私有字段无法通用读取
  - Location: CPU 制程、GPU 流处理器/显存颗粒、内存完整时序
  - Evidence: Windows WMI、注册表和通用硬件库没有稳定公开这些字段。
  - Impact: 页面显示真实可验证的数据，不伪造参考软件的私有数据库结果。

## Comparison history

1. 首轮：发现 Windows 11 被旧注册表兼容字段标为 Windows 10、核显排列在独显之前。
2. 修正：系统名称改用 `Win32_OperatingSystem.Caption`，显卡按 NVIDIA/AMD/Intel 排序，并统一容量为整数 GB。
3. 复查：因检测到用户输入而停止窗口自动化，缺少修正后的最终截图。

## Implementation checklist

- [x] 硬件信息页整体改为硬件属性表
- [x] 接入公开 WMI/注册表/CPU Set 数据
- [x] 保留浅色毛玻璃和单列自适应布局
- [x] 移除实时传感器面板、管理员助手与计划任务启动链路
- [ ] 用户空闲时补充最终窗口截图对照

final result: blocked
