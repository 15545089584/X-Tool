using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Management;
using Microsoft.Win32;

namespace ScreenshotApp.SystemTools;

/// <summary>系统工具的数据读取与受控操作，默认只读，写操作由页面二次确认后调用。</summary>
public static class SystemToolsService
{
    private static readonly Regex NetstatLine = new(@"^\s*(TCP|UDP)\s+(?<local>\S+)\s+(?<remote>\S+)(?:\s+(?<state>\S+))?\s+(?<pid>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ServiceNameLine = new(@"^SERVICE_NAME:\s*(?<name>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex DisplayNameLine = new(@"^DISPLAY_NAME:\s*(?<name>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ServiceStateLine = new(@"^\s*STATE\s*:\s*\d+\s+(?<state>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ServiceProcessIdLine = new(@"^\s*PID\s*:\s*(?<pid>\d+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    public static IReadOnlyList<PortEntry> GetPorts() => GetPorts(processesById: null);

    /// <summary>采集系统概览所需的只读信息，不依赖 WMI，降低普通权限与精简系统上的失败概率。</summary>
    public static SystemOverview GetSystemOverview()
    {
        using var currentVersion = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var productName = currentVersion?.GetValue("ProductName")?.ToString() ?? "Windows";
        var displayVersion = currentVersion?.GetValue("DisplayVersion")?.ToString() ?? currentVersion?.GetValue("ReleaseId")?.ToString() ?? string.Empty;
        var build = currentVersion?.GetValue("CurrentBuildNumber")?.ToString() ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var hasMemory = GlobalMemoryStatusEx(ref memory);
        var totalMemory = hasMemory ? FormatBytes(memory.TotalPhys > long.MaxValue ? long.MaxValue : (long)memory.TotalPhys) : "未知";
        var availableMemory = hasMemory ? FormatBytes(memory.AvailPhys > long.MaxValue ? long.MaxValue : (long)memory.AvailPhys) : "未知";
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "处理器信息不可用";
        var board = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var boardText = $"{board?.GetValue("BaseBoardManufacturer") ?? "未知厂商"} {board?.GetValue("BaseBoardProduct") ?? "未知型号"}".Trim();
        var gpu = GetGraphicsNames();
        var battery = System.Windows.Forms.SystemInformation.PowerStatus;
        var batteryText = battery.BatteryLifePercent < 0 ? "未检测到电池（台式设备或权限限制）" : $"电量 {battery.BatteryLifePercent:P0} · {(battery.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "正在接通电源" : "使用电池供电")}";
        var items = new[]
        {
            new SystemInfoItem("Windows", "系统", $"{productName} {displayVersion}".Trim(), $"内部版本 {build} · {(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")}", "#4D7CFE", "\uE770"),
            new SystemInfoItem("处理器", "CPU", cpu, $"{Environment.ProcessorCount} 个逻辑处理器 · 当前为{(IsRunningAsAdministrator() ? "管理员" : "普通")}权限", "#3B9EFF", "\uEEA1"),
            new SystemInfoItem("显卡", "GPU", gpu, "图形适配器信息来自 Windows 设备注册表", "#8B6CFF", "\uE950"),
            new SystemInfoItem("主板", "主板", boardText, $"设备名称 {Environment.MachineName}", "#F06D75", "\uE772"),
            new SystemInfoItem("内存", "内存", $"可用 {availableMemory} / 共 {totalMemory}", $"系统已运行 {FormatUptime(uptime)}", "#18B894", "\uEEA0"),
            new SystemInfoItem("显示器", "显示器", System.Windows.SystemParameters.PrimaryScreenWidth > 0 ? $"主显示器 {System.Windows.SystemParameters.PrimaryScreenWidth:F0} × {System.Windows.SystemParameters.PrimaryScreenHeight:F0}" : "显示器信息不可用", "分辨率来自当前 Windows 显示设置", "#4D7CFE", "\uE7F9"),
            new SystemInfoItem("电池", "电池", batteryText, "电池数据由 Windows 电源状态提供", "#F3A847", "\uE855")
        };
        return new SystemOverview(items, DateTime.Now);
    }

    /// <summary>按照硬件类别采集可验证的公开属性；不可可靠读取的厂商私有字段保持缺省。</summary>
    public static HardwareOverview GetHardwareOverview()
    {
        var items = new List<HardwarePropertyItem>();
        using var currentVersion = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var operatingSystem = QueryWmi(@"root\cimv2", "SELECT Caption FROM Win32_OperatingSystem").FirstOrDefault();
        var productName = GetText(operatingSystem, "Caption", currentVersion?.GetValue("ProductName")?.ToString() ?? "Windows").Replace("Microsoft ", string.Empty, StringComparison.OrdinalIgnoreCase);
        var displayVersion = currentVersion?.GetValue("DisplayVersion")?.ToString() ?? string.Empty;
        var build = currentVersion?.GetValue("CurrentBuildNumber")?.ToString() ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        var ubr = currentVersion?.GetValue("UBR")?.ToString();
        items.Add(new HardwarePropertyItem("系统", $"{productName} {(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")}", $"版本号  {build}{(string.IsNullOrWhiteSpace(ubr) ? string.Empty : $".{ubr}")} ({displayVersion})", string.Empty, "#4D7CFE", "\uE770"));

        var cpu = QueryWmi(@"root\cimv2", "SELECT Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor").FirstOrDefault();
        var cpuName = CleanHardwareName(GetText(cpu, "Name", "处理器信息不可用"));
        var cores = GetInt(cpu, "NumberOfCores");
        var threads = GetInt(cpu, "NumberOfLogicalProcessors");
        var hybrid = GetHybridCoreLayout();
        var coreText = hybrid.Performance > 0 && hybrid.Efficiency > 0 ? $"核心数  {cores} ({hybrid.Performance}P+{hybrid.Efficiency}E)" : $"核心数  {cores}";
        var baseClock = GetInt(cpu, "MaxClockSpeed");
        var cpuAttributes = $"{coreText}    线程数  {threads}" + (baseClock > 0 ? $"    标称频率  {baseClock / 1000d:F2} GHz" : string.Empty);
        items.Add(new HardwarePropertyItem("处理器", cpuName, cpuAttributes, string.Empty, "#2F9AF5", "\uEEA1"));

        var graphics = QueryWmi(@"root\cimv2", "SELECT Name,AdapterCompatibility,AdapterRAM FROM Win32_VideoController")
            .Where(row => !GetText(row, "Name").Contains("Remote", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => GraphicsAdapterOrder(GetText(row, "Name"))).ToArray();
        var graphicsItems = graphics.Select(row =>
        {
            var name = GetText(row, "Name");
            var vendor = GetText(row, "AdapterCompatibility", "未知厂商").Replace(" Corporation", string.Empty, StringComparison.OrdinalIgnoreCase);
            var memory = GetGraphicsMemoryBytes(name);
            var detail = memory > 0 ? $"{vendor}    显存  {FormatHardwareCapacity(memory)}" : $"{vendor}    共享系统内存";
            return new { Name = CleanHardwareName(GetText(row, "Name", "未知图形适配器")), Detail = detail };
        }).ToArray();
        var primaryGraphics = graphicsItems.FirstOrDefault();
        var graphicsPrimary = primaryGraphics?.Name ?? "图形适配器信息不可用";
        var graphicsDetails = primaryGraphics?.Detail ?? string.Empty;
        var graphicsSecondary = string.Join("\n", graphicsItems.Skip(1).Select(item => $"{item.Name}    {item.Detail}"));
        items.Add(new HardwarePropertyItem("显卡", graphicsPrimary, graphicsDetails, graphicsSecondary, "#7D63F1", "\uE950"));

        var board = QueryWmi(@"root\cimv2", "SELECT Manufacturer,Product,Version FROM Win32_BaseBoard").FirstOrDefault();
        items.Add(new HardwarePropertyItem("主板", GetText(board, "Product", "主板型号不可用"), $"{GetText(board, "Manufacturer", "未知厂商")}    版本  {GetText(board, "Version", "未知")}", string.Empty, "#F06D75", "\uE772"));

        var disks = QueryWmi(@"root\cimv2", "SELECT Model,Size,MediaType,InterfaceType FROM Win32_DiskDrive");
        var diskPrimary = string.Join("\n", disks.Select(row => GetText(row, "Model", "未知磁盘")));
        var diskDetails = string.Join("\n", disks.Select(row =>
        {
            var model = GetText(row, "Model");
            var size = GetLong(row, "Size");
            var type = model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe SSD" : GetText(row, "MediaType", "磁盘").Replace("Fixed hard disk media", "SSD / HDD", StringComparison.OrdinalIgnoreCase);
            return $"实际容量  {FormatHardwareCapacity(size)}    类型  {type}";
        }));
        items.Add(new HardwarePropertyItem("硬盘", string.IsNullOrWhiteSpace(diskPrimary) ? "未检测到物理磁盘" : diskPrimary, diskDetails, string.Empty, "#16B99B", "\uEDA2"));

        var monitor = QueryWmi(@"root\wmi", "SELECT ManufacturerName,UserFriendlyName FROM WmiMonitorID").FirstOrDefault();
        var monitorName = DecodeWmiText(GetValue(monitor, "UserFriendlyName"));
        var activeDisplay = QueryWmi(@"root\cimv2", "SELECT CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate FROM Win32_VideoController")
            .FirstOrDefault(row => GetInt(row, "CurrentHorizontalResolution") > 0);
        var width = GetInt(activeDisplay, "CurrentHorizontalResolution");
        var height = GetInt(activeDisplay, "CurrentVerticalResolution");
        var refresh = GetInt(activeDisplay, "CurrentRefreshRate");
        var displayAttributes = width > 0 ? $"分辨率  {width} × {height}    刷新率  {refresh} Hz" : "当前显示参数不可用";
        items.Add(new HardwarePropertyItem("显示器", string.IsNullOrWhiteSpace(monitorName) ? "主显示器" : monitorName, displayAttributes, string.Empty, "#607D91", "\uE7F9"));

        var modules = QueryWmi(@"root\cimv2", "SELECT Manufacturer,PartNumber,Capacity,ConfiguredClockSpeed,SMBIOSMemoryType,DeviceLocator FROM Win32_PhysicalMemory");
        var totalMemory = modules.Sum(row => GetLong(row, "Capacity"));
        var memorySpeed = modules.Select(row => GetInt(row, "ConfiguredClockSpeed")).Where(value => value > 0).DefaultIfEmpty().Max();
        var memoryType = MemoryTypeName(modules.Select(row => GetInt(row, "SMBIOSMemoryType")).FirstOrDefault(value => value > 0));
        var channels = modules.Select(row => GetText(row, "DeviceLocator").Split('-')[0]).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var memoryAttributes = $"容量  {FormatHardwareCapacity(totalMemory)}    通道  {Math.Max(1, channels)}    频率  {memorySpeed} MHz    类型  {memoryType}";
        var moduleLines = string.Join("\n", modules.Select(row => $"{GetText(row, "Manufacturer", "未知厂商")}  {GetText(row, "PartNumber").Trim()}  {FormatHardwareCapacity(GetLong(row, "Capacity"))}"));
        items.Add(new HardwarePropertyItem("内存", memoryAttributes, moduleLines, string.Empty, "#168BBF", "\uEEA0"));

        var battery = QueryWmi(@"root\cimv2", "SELECT Name,EstimatedChargeRemaining,BatteryStatus FROM Win32_Battery").FirstOrDefault();
        if (battery is not null)
        {
            var fullCapacity = GetLong(QueryWmi(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").FirstOrDefault(), "FullChargedCapacity");
            var percent = GetInt(battery, "EstimatedChargeRemaining");
            var status = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "正在接通电源" : "使用电池供电";
            var details = $"电量  {percent}%    {status}" + (fullCapacity > 0 ? $"    满充容量  {fullCapacity / 1000d:F1} Wh" : string.Empty);
            items.Add(new HardwarePropertyItem("电池", GetText(battery, "Name", "电池"), details, string.Empty, "#F3A847", "\uE855"));
        }
        return new HardwareOverview(items, DateTime.Now);
    }

    /// <summary>读取 Windows 已识别的 PnP 驱动，并补充设备管理器提供的状态；仅使用公开只读 WMI 接口。</summary>
    public static IReadOnlyList<DeviceDriverEntry> GetDeviceDrivers()
    {
        var statesByDeviceId = new Dictionary<string, PnpDeviceState>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in QueryWmi(
                     @"root\cimv2",
                     "SELECT DeviceID,PNPDeviceID,PNPClass,Status,ConfigManagerErrorCode,ErrorDescription FROM Win32_PnPEntity"))
        {
            var state = new PnpDeviceState(
                GetText(row, "PNPClass"),
                GetText(row, "Status"),
                GetNullableInt(row, "ConfigManagerErrorCode"),
                GetText(row, "ErrorDescription"));
            foreach (var deviceId in new[] { GetText(row, "DeviceID"), GetText(row, "PNPDeviceID") }.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                statesByDeviceId[deviceId] = state;
            }
        }

        var drivers = QueryWmi(
                @"root\cimv2",
                "SELECT DeviceName,FriendlyName,DeviceClass,DeviceID,DriverProviderName,DriverVersion,DriverDate,InfName,IsSigned,Signer,Manufacturer FROM Win32_PnPSignedDriver")
            .Select(row =>
            {
                var deviceId = GetText(row, "DeviceID");
                statesByDeviceId.TryGetValue(deviceId, out var state);
                var deviceName = GetText(row, "DeviceName", GetText(row, "FriendlyName", "未命名设备"));
                var category = GetDriverCategory(GetText(row, "DeviceClass"), state?.PnpClass ?? string.Empty, deviceName);
                var provider = GetText(row, "DriverProviderName", GetText(row, "Manufacturer", "未知提供商"));
                return new DeviceDriverEntry(
                    deviceId,
                    deviceName,
                    category,
                    provider,
                    GetText(row, "DriverVersion", "未知版本"),
                    FormatDriverDate(GetValue(row, "DriverDate")),
                    GetText(row, "InfName"),
                    GetBoolean(row, "IsSigned"),
                    GetText(row, "Signer"),
                    state?.ErrorCode,
                    state?.Status ?? string.Empty,
                    state?.ErrorDescription ?? string.Empty,
                    state is not null);
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.DeviceName))
            .GroupBy(
                item => string.IsNullOrWhiteSpace(item.DeviceId)
                    ? $"{item.DeviceName}\u001F{item.InfName}\u001F{item.Version}"
                    : $"{item.DeviceId}\u001F{item.InfName}\u001F{item.Version}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.HasIssue)
            .ThenBy(item => item.CategoryOrder)
            .ThenBy(item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return drivers;
    }

    /// <summary>读取本地卷与物理磁盘的容量快照，仅使用 Windows 的公开只读接口。</summary>
    public static StorageOverview GetStorageOverview()
    {
        var volumes = new List<StorageVolumeEntry>();
        foreach (var drive in DriveInfo.GetDrives().Where(item => item.DriveType == DriveType.Fixed))
        {
            try
            {
                if (!drive.IsReady) continue;
                volumes.Add(new StorageVolumeEntry(
                    drive.Name.TrimEnd('\\'),
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel,
                    string.IsNullOrWhiteSpace(drive.DriveFormat) ? "文件系统未知" : drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch
            {
                // 个别驱动器可能在枚举期间断开，跳过后继续读取其余卷。
            }
        }

        var physicalDisks = QueryWmi(@"root\cimv2", "SELECT Model,Size,MediaType,InterfaceType FROM Win32_DiskDrive")
            .Select(row =>
            {
                var model = CleanHardwareName(GetText(row, "Model", "未知物理磁盘"));
                var mediaType = model.Contains("NVMe", StringComparison.OrdinalIgnoreCase)
                    ? "NVMe SSD"
                    : GetText(row, "MediaType", "固定磁盘").Replace("Fixed hard disk media", "固定磁盘", StringComparison.OrdinalIgnoreCase);
                return new PhysicalStorageEntry(
                    model,
                    GetText(row, "InterfaceType", "接口未知"),
                    mediaType,
                    GetLong(row, "Size"));
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Model))
            .GroupBy(item => $"{item.Model}\u001F{item.CapacityBytes}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var totalBytes = volumes.Sum(item => item.TotalBytes);
        var freeBytes = volumes.Sum(item => item.FreeBytes);
        return new StorageOverview(volumes, physicalDisks, totalBytes, freeBytes, DateTime.Now);
    }

    private static IReadOnlyList<Dictionary<string, object?>> QueryWmi(string scopePath, string query)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scopePath), new ObjectQuery(query));
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData property in item.Properties) row[property.Name] = property.Value;
                    rows.Add(row);
                }
            }
        }
        catch { }
        return rows;
    }

    private static object? GetValue(Dictionary<string, object?>? row, string key) => row is not null && row.TryGetValue(key, out var value) ? value : null;
    private static string GetText(Dictionary<string, object?>? row, string key, string fallback = "") => GetValue(row, key)?.ToString()?.Trim() is { Length: > 0 } value ? value : fallback;
    private static int GetInt(Dictionary<string, object?>? row, string key) => int.TryParse(GetValue(row, key)?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static int? GetNullableInt(Dictionary<string, object?>? row, string key) => int.TryParse(GetValue(row, key)?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static long GetLong(Dictionary<string, object?>? row, string key) => long.TryParse(GetValue(row, key)?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static bool GetBoolean(Dictionary<string, object?>? row, string key)
    {
        var value = GetValue(row, key);
        return value is bool boolean
            ? boolean
            : bool.TryParse(value?.ToString(), out var parsed) ? parsed : string.Equals(value?.ToString(), "1", StringComparison.Ordinal);
    }

    private static string FormatDriverDate(object? value)
    {
        if (value is DateTime date) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var text = value?.ToString()?.Trim() ?? string.Empty;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (text.Length >= 14 && DateTime.TryParseExact(text[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? "未知" : text;
    }

    private static string GetDriverCategory(string deviceClass, string pnpClass, string deviceName)
    {
        var category = $"{deviceClass} {pnpClass} {deviceName}".ToUpperInvariant();
        if (category.Contains("DISPLAY", StringComparison.Ordinal)) return "显卡";
        if (category.Contains("BLUETOOTH", StringComparison.Ordinal)) return "蓝牙";
        if (category.Contains("NET", StringComparison.Ordinal)) return "网络";
        if (category.Contains("AUDIO", StringComparison.Ordinal) || category.Contains("MEDIA", StringComparison.Ordinal)) return "音频";
        if (category.Contains("SCSI", StringComparison.Ordinal) || category.Contains("HDC", StringComparison.Ordinal) || category.Contains("DISK", StringComparison.Ordinal) || category.Contains("VOLUME", StringComparison.Ordinal) || category.Contains("STORAGE", StringComparison.Ordinal)) return "存储";
        if (category.Contains("MONITOR", StringComparison.Ordinal)) return "显示器";
        if (category.Contains("CAMERA", StringComparison.Ordinal) || category.Contains("IMAGE", StringComparison.Ordinal)) return "摄像头";
        if (category.Contains("KEYBOARD", StringComparison.Ordinal) || category.Contains("MOUSE", StringComparison.Ordinal) || category.Contains("HID", StringComparison.Ordinal)) return "输入设备";
        if (category.Contains("USB", StringComparison.Ordinal)) return "USB";
        if (category.Contains("PORT", StringComparison.Ordinal)) return "端口";
        if (category.Contains("SYSTEM", StringComparison.Ordinal) || category.Contains("PROCESSOR", StringComparison.Ordinal) || category.Contains("FIRMWARE", StringComparison.Ordinal)) return "系统设备";
        if (category.Contains("SOFTWARE", StringComparison.Ordinal)) return "软件组件";
        return "其他";
    }

    private sealed record PnpDeviceState(string PnpClass, string Status, int? ErrorCode, string ErrorDescription);

    private static string CleanHardwareName(string value) => value.Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("  ", " ").Trim();
    private static int GraphicsAdapterOrder(string name) => name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? 0 : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? 1 : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    private static string DecodeWmiText(object? value) => value is ushort[] words ? new string(words.Where(word => word > 0).Select(word => (char)word).ToArray()).Trim() : string.Empty;
    private static string MemoryTypeName(int value) => value switch { 34 => "DDR5", 26 => "DDR4", 24 => "DDR3", _ => "内存" };
    private static string FormatHardwareCapacity(long value) => value >= 1024L * 1024 * 1024 ? $"{value / 1024d / 1024d / 1024d:F0} GB" : FormatBytes(value);

    private static long GetGraphicsMemoryBytes(string adapterName)
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters is null) return 0;
            foreach (var keyName in adapters.GetSubKeyNames())
            {
                using var adapter = adapters.OpenSubKey(keyName);
                if (!string.Equals(adapter?.GetValue("DriverDesc")?.ToString(), adapterName, StringComparison.OrdinalIgnoreCase)) continue;
                var value = adapter!.GetValue("HardwareInformation.qwMemorySize");
                return value switch { long number => number, ulong number when number <= long.MaxValue => (long)number, byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0), _ => 0 };
            }
        }
        catch { }
        return 0;
    }

    private static (int Performance, int Efficiency) GetHybridCoreLayout()
    {
        if (!GetSystemCpuSetInformation(IntPtr.Zero, 0, out var required, IntPtr.Zero, 0) && required == 0) return default;
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, required, out required, IntPtr.Zero, 0)) return default;
            var cores = new Dictionary<(ushort Group, byte Core), byte>();
            var offset = 0;
            while (offset + 20 <= required)
            {
                var size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0 || offset + size > required) break;
                if (Marshal.ReadInt32(buffer, offset + 4) == 0)
                {
                    var group = (ushort)Marshal.ReadInt16(buffer, offset + 12);
                    var core = Marshal.ReadByte(buffer, offset + 15);
                    var efficiency = Marshal.ReadByte(buffer, offset + 18);
                    cores[(group, core)] = efficiency;
                }
                offset += size;
            }
            var classes = cores.Values.Distinct().OrderBy(value => value).ToArray();
            return classes.Length < 2 ? default : (cores.Values.Count(value => value == classes[^1]), cores.Values.Count(value => value == classes[0]));
        }
        catch { return default; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string GetGraphicsNames()
    {
        using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (adapters is null) return "显卡信息不可用";
        return string.Join("\n", adapters.GetSubKeyNames().Where(name => name.Length == 4).Select(name => adapters.OpenSubKey(name)?.GetValue("DriverDesc")?.ToString()).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string FormatBytes(long value)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double display = Math.Max(0, value);
        var index = 0;
        while (display >= 1024 && index < units.Length - 1) { display /= 1024d; index++; }
        return index == 0 ? $"{display:F0} {units[index]}" : $"{display:F1} {units[index]}";
    }

    private static string FormatUptime(TimeSpan uptime)
        => uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays} 天 {uptime.Hours} 小时" : $"{uptime.Hours} 小时 {uptime.Minutes} 分钟";

    /// <summary>一次采集三类只读数据，并以进程索引复用端口的进程信息，避免逐条端口重复访问进程句柄。</summary>
    public static SystemRelationshipSnapshot GetRelationshipSnapshot()
    {
        var processes = GetProcesses();
        var processesById = processes.ToDictionary(item => item.ProcessId);
        var ports = GetPorts(processesById);
        var services = GetServices();
        return new SystemRelationshipSnapshot(processes, ports, services, DateTime.Now);
    }

    private static IReadOnlyList<PortEntry> GetPorts(IReadOnlyDictionary<int, ProcessEntry>? processesById)
    {
        var entries = new List<PortEntry>();
        foreach (var line in RunCommand("netstat.exe", "-ano").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = NetstatLine.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["pid"].Value, out var pid) || pid == 0)
            {
                continue;
            }

            var protocol = match.Groups[1].Value.ToUpperInvariant();
            var state = match.Groups["state"].Success ? match.Groups["state"].Value : "监听";
            var (name, path) = processesById is not null && processesById.TryGetValue(pid, out var process)
                ? (process.Name, process.Path)
                : GetProcessDetails(pid);
            entries.Add(new PortEntry(protocol, match.Groups["local"].Value, match.Groups["remote"].Value, state, pid, name, path));
        }

        return entries
            .OrderBy(item => ExtractPort(item.LocalAddress))
            .ThenBy(item => item.Protocol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ProcessEntry> GetProcesses()
    {
        // 每次刷新都使用一组独立的前后样本，避免关系看板或自动刷新覆盖共享基线，
        // 也确保 CPU 与 I/O 速率拥有足够长的测量区间。
        var previousSamples = ReadProcessSamples().ToDictionary(item => item.ProcessId);
        if (previousSamples.Count == 0) return Array.Empty<ProcessEntry>();
        Thread.Sleep(650);
        return CreateProcessEntries(ReadProcessSamples(), previousSamples);
    }

    /// <summary>读取一次累计性能计数；速率由相邻两次采样的增量计算。</summary>
    private static IReadOnlyList<ProcessSample> ReadProcessSamples()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var samples = new List<ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                string path = string.Empty;
                DateTime? startedAt = null;
                var processorTime = TimeSpan.Zero;
                long memoryBytes = 0;
                try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
                try { startedAt = process.StartTime; } catch { }
                try { processorTime = process.TotalProcessorTime; } catch { }
                try { memoryBytes = process.WorkingSet64; } catch { }
                var io = TryGetProcessIoCounters(process, out var counters) ? counters : default;
                samples.Add(new ProcessSample(process.ProcessName, process.Id, processorTime, memoryBytes, path, startedAt, io.ReadTransferCount, io.WriteTransferCount, io.OtherTransferCount, timestamp));
            }
            catch { }
            finally { process.Dispose(); }
        }

        return samples;
    }

    private static IReadOnlyList<ProcessEntry> CreateProcessEntries(IReadOnlyList<ProcessSample> samples, IReadOnlyDictionary<int, ProcessSample> previousSamples)
    {
        var entries = new List<ProcessEntry>();
        foreach (var sample in samples)
        {
            previousSamples.TryGetValue(sample.ProcessId, out var previous);
            var elapsedSeconds = previous is null ? 0d : (sample.Timestamp - previous.Timestamp) / (double)Stopwatch.Frequency;
            var cpu = elapsedSeconds > 0 ? Math.Max(0, (sample.TotalProcessorTime - previous!.TotalProcessorTime).TotalSeconds / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100d) : 0;
            var disk = elapsedSeconds > 0 ? CalculateRate(sample.ReadTransferCount, previous!.ReadTransferCount, elapsedSeconds) + CalculateRate(sample.WriteTransferCount, previous.WriteTransferCount, elapsedSeconds) : 0;
            // Windows 进程级 API 未单独公开所有协议的网络计数；其它 I/O 字节是可用的近似值，并保留在界面提示中说明。
            var network = elapsedSeconds > 0 ? CalculateRate(sample.OtherTransferCount, previous!.OtherTransferCount, elapsedSeconds) * 8d : 0;
            entries.Add(new ProcessEntry(sample.Name, sample.ProcessId, cpu, sample.MemoryBytes, disk, network, sample.Path, sample.StartedAt));
        }

        return entries.OrderByDescending(item => item.MemoryBytes).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static double CalculateRate(ulong current, ulong previous, double elapsedSeconds)
        => current >= previous && elapsedSeconds > 0 ? (current - previous) / elapsedSeconds : 0;

    private static bool TryGetProcessIoCounters(Process process, out IoCounters counters)
    {
        try { return GetProcessIoCounters(process.Handle, out counters); }
        catch { counters = default; return false; }
    }

    public static IReadOnlyList<ServiceEntry> GetServices()
    {
        var blocks = Regex.Split(RunCommand("sc.exe", "queryex type= service state= all").Replace("\r\n", "\n"), @"\n\s*\n");
        var entries = new List<ServiceEntry>();
        foreach (var block in blocks)
        {
            var name = ServiceNameLine.Match(block).Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var displayName = DisplayNameLine.Match(block).Groups["name"].Value.Trim();
            var state = ServiceStateLine.Match(block).Groups["state"].Value.Trim();
            var processIdText = ServiceProcessIdLine.Match(block).Groups["pid"].Value;
            var processId = int.TryParse(processIdText, out var value) ? value : 0;
            entries.Add(new ServiceEntry(name, string.IsNullOrWhiteSpace(displayName) ? name : displayName, NormalizeServiceState(state), processId));
        }

        return entries.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<EnvironmentVariableEntry> GetEnvironmentVariables(EnvironmentVariableTarget target)
    {
        var variables = Environment.GetEnvironmentVariables(target);
        return variables.Keys.Cast<object>()
            .Select(key => new EnvironmentVariableEntry(key.ToString() ?? string.Empty, variables[key]?.ToString() ?? string.Empty, target))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool TryEndProcess(int processId, out string? error)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryEndProcessWithElevation(int processId, out string? error)
        => TryEndProcessesWithElevation(new[] { processId }, out error);

    public static bool TryEndProcessesWithElevation(IReadOnlyCollection<int> processIds, out string? error)
    {
        var distinctProcessIds = processIds.Where(processId => processId > 0).Distinct().ToArray();
        if (distinctProcessIds.Length == 0)
        {
            error = "没有可结束的进程 PID";
            return false;
        }

        var failedProcessIds = new List<int>();
        var directErrors = new List<string>();
        foreach (var processId in distinctProcessIds)
        {
            if (TryEndProcess(processId, out var itemError)) continue;
            failedProcessIds.Add(processId);
            directErrors.Add($"PID {processId}：{itemError}");
        }

        if (failedProcessIds.Count == 0)
        {
            error = null;
            return true;
        }

        if (IsRunningAsAdministrator())
        {
            error = string.Join(Environment.NewLine, directErrors);
            return false;
        }

        return RunElevatedSystemAction(
            new ElevatedSystemActionRequest("EndProcesses", ProcessIds: failedProcessIds.ToArray()),
            "管理员结束进程失败",
            out error);
    }

    private static bool TryEndProcesses(IReadOnlyCollection<int> processIds, out string? error)
    {
        var errors = new List<string>();
        foreach (var processId in processIds)
        {
            if (!TryEndProcess(processId, out var itemError)) errors.Add($"PID {processId}：{itemError}");
        }

        error = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
        return errors.Count == 0;
    }

    public static bool TryOpenProcessDirectory(string path, out string? error)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "未能读取该进程的可执行文件路径";
                return false;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryControlService(string serviceName, bool start, out string? error)
    {
        var result = RunCommandWithExitCode("sc.exe", $"{(start ? "start" : "stop")} \"{serviceName}\"");
        if (result.ExitCode != 0)
        {
            error = result.Output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                ? $"权限不足，请授权管理员操作后重试。{Environment.NewLine}{result.Output.Trim()}"
                : string.IsNullOrWhiteSpace(result.Output) ? $"sc.exe 返回错误代码 {result.ExitCode}" : result.Output.Trim();
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryControlServiceWithElevation(string serviceName, bool start, out string? error)
    {
        if (TryControlService(serviceName, start, out error) || IsRunningAsAdministrator())
        {
            return error is null;
        }

        if (!RequiresElevation(error))
        {
            return false;
        }

        return RunElevatedSystemAction(
            new ElevatedSystemActionRequest("ControlService", Name: serviceName, Start: start),
            $"管理员{(start ? "启动" : "停止")}服务失败",
            out error);
    }

    public static bool TrySaveEnvironmentVariable(string name, string value, EnvironmentVariableTarget target, out string? error)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '=', '\0' }) >= 0)
            {
                error = "变量名不能为空，且不能包含 =";
                return false;
            }

            Environment.SetEnvironmentVariable(name.Trim(), string.IsNullOrWhiteSpace(value) ? null : value, target);
            BroadcastEnvironmentChanged();
            error = null;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "权限不足：系统环境变量需要管理员权限";
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>当前进程未提权时，仅为一次系统变量写入触发 UAC，不让整个应用长期以管理员运行。</summary>
    public static bool TrySaveMachineEnvironmentVariableWithElevation(string name, string value, out string? error)
    {
        if (IsRunningAsAdministrator())
        {
            return TrySaveEnvironmentVariable(name, value, EnvironmentVariableTarget.Machine, out error);
        }

        return RunElevatedSystemAction(
            new ElevatedSystemActionRequest("SetMachineEnvironment", Name: name, Value: value),
            "管理员保存系统环境变量失败",
            out error);
    }

    /// <summary>按需请求管理员权限修改网卡启用状态。</summary>
    public static bool TrySetNetworkAdapterStateWithElevation(string adapterName, bool enabled, out string? error)
        => RunElevatedSystemAction(
            new ElevatedSystemActionRequest("SetNetworkAdapterState", Name: adapterName, Start: enabled),
            $"管理员{(enabled ? "启用" : "禁用")}网卡失败",
            out error);

    /// <summary>按需请求管理员权限释放并续租指定网卡的 DHCP 地址。</summary>
    public static bool TryRenewNetworkAdapterDhcpWithElevation(string adapterName, out string? error)
        => RunElevatedSystemAction(
            new ElevatedSystemActionRequest("RenewNetworkAdapterDhcp", Name: adapterName),
            "管理员续租 DHCP 失败",
            out error);

    /// <summary>按需请求管理员权限设置指定网卡的 IPv4 DNS；主 DNS 为空时恢复自动获取。</summary>
    public static bool TrySetNetworkAdapterDnsWithElevation(string adapterName, string primaryDns, string secondaryDns, out string? error)
        => RunElevatedSystemAction(
            new ElevatedSystemActionRequest("SetNetworkAdapterDns", Name: adapterName, Value: primaryDns, ExtraValue: secondaryDns),
            "管理员保存 DNS 失败",
            out error);

    public static bool TrySetNetworkAdapterAddressWithElevation(string adapterName, bool dhcp, string address, int prefixLength, string gateway, out string? error)
        => RunElevatedSystemAction(new ElevatedSystemActionRequest("SetNetworkAdapterAddress", Name: adapterName,
            Value: dhcp ? "dhcp" : address, ExtraValue: $"{prefixLength}|{gateway}"), "保存 IPv4 地址失败", out error);

    public static bool TrySetNetworkAdapterMtuWithElevation(string adapterName, int mtu, out string? error)
        => RunElevatedSystemAction(new ElevatedSystemActionRequest("SetNetworkAdapterMtu", Name: adapterName, Value: mtu.ToString()), "保存 MTU 失败", out error);

    public static bool TrySetNetworkAdapterMetricWithElevation(string adapterName, int metric, out string? error)
        => RunElevatedSystemAction(new ElevatedSystemActionRequest("SetNetworkAdapterMetric", Name: adapterName, Value: metric.ToString()), "保存接口跃点失败", out error);

    public static bool TryFlushDnsWithElevation(out string? error)
        => RunElevatedSystemAction(new ElevatedSystemActionRequest("FlushDns"), "清理 DNS 缓存失败", out error);

    public static bool TryResetWinsockWithElevation(out string? error)
        => RunElevatedSystemAction(new ElevatedSystemActionRequest("ResetWinsock"), "重置 Winsock 失败", out error);

    public static bool TryResetTcpIpWithElevation(out string? error)
        => RunElevatedSystemAction(new ElevatedSystemActionRequest("ResetTcpIp"), "重置 TCP/IP 失败", out error);

    public static bool TryCreateTemporaryFirewallBlockRuleWithElevation(int port, out string? error)
    {
        if (port is < 1 or > 65535) { error = "端口必须位于 1 到 65535。"; return false; }
        return RunElevatedSystemAction(new ElevatedSystemActionRequest("AddTemporaryFirewallRule", Value: port.ToString()), "创建临时防火墙规则失败", out error);
    }

    public static bool TryRemoveXToolFirewallRuleWithElevation(int port, out string? error)
    {
        if (port is < 1 or > 65535) { error = "端口必须位于 1 到 65535。"; return false; }
        return RunElevatedSystemAction(new ElevatedSystemActionRequest("RemoveXToolFirewallRule", Value: port.ToString()), "删除 X-Tool 临时规则失败", out error);
    }

    public static bool TrySyncWinHttpProxyWithElevation(bool reset, out string? error)
        => RunElevatedSystemAction(
            new ElevatedSystemActionRequest(reset ? "ResetWinHttpProxy" : "SyncWinHttpProxy"),
            reset ? "清除 WinHTTP 代理失败" : "同步 WinHTTP 代理失败",
            out error);

    /// <summary>供经 UAC 启动的无界面子进程调用，只执行临时文件中声明的一项受控操作。</summary>
    public static int ApplyElevatedSystemActionRequest(string requestPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                return 2;
            }

            var request = JsonSerializer.Deserialize<ElevatedSystemActionRequest>(File.ReadAllText(requestPath, Encoding.UTF8));
            if (request is null)
            {
                return 2;
            }

            string? actionError = null;
            var succeeded = request.Action switch
            {
                "SetMachineEnvironment" => TrySaveEnvironmentVariable(request.Name ?? string.Empty, request.Value ?? string.Empty, EnvironmentVariableTarget.Machine, out actionError),
                "EndProcesses" => TryEndProcesses(request.ProcessIds ?? Array.Empty<int>(), out actionError),
                "ControlService" => TryControlService(request.Name ?? string.Empty, request.Start, out actionError),
                "SetNetworkAdapterState" => TrySetNetworkAdapterState(request.Name ?? string.Empty, request.Start, out actionError),
                "RenewNetworkAdapterDhcp" => TryRenewNetworkAdapterDhcp(request.Name ?? string.Empty, out actionError),
                "SetNetworkAdapterDns" => TrySetNetworkAdapterDns(request.Name ?? string.Empty, request.Value ?? string.Empty, request.ExtraValue ?? string.Empty, out actionError),
                "SetNetworkAdapterAddress" => TrySetNetworkAdapterAddress(request.Name ?? string.Empty, request.Value ?? string.Empty, request.ExtraValue ?? string.Empty, out actionError),
                "SetNetworkAdapterMtu" => int.TryParse(request.Value, out var mtu) && mtu is >= 576 and <= 9000 &&
                    TryRunSystemCommand("netsh.exe", $"interface ipv4 set subinterface \"{EscapeCommandValue(request.Name ?? string.Empty)}\" mtu={mtu} store=persistent", out actionError),
                "SetNetworkAdapterMetric" => int.TryParse(request.Value, out var metric) && metric is >= 1 and <= 9999 &&
                    TryRunSystemCommand("netsh.exe", $"interface ipv4 set interface interface=\"{EscapeCommandValue(request.Name ?? string.Empty)}\" metric={metric}", out actionError),
                "FlushDns" => TryRunSystemCommand("ipconfig.exe", "/flushdns", out actionError),
                "ResetWinsock" => TryRunSystemCommand("netsh.exe", "winsock reset", out actionError),
                "ResetTcpIp" => TryRunSystemCommand("netsh.exe", "int ip reset", out actionError),
                "AddTemporaryFirewallRule" => int.TryParse(request.Value, out var firewallPort) && firewallPort is >= 1 and <= 65535 &&
                    TryRunSystemCommand("netsh.exe", $"advfirewall firewall add rule name=\"X-Tool 临时阻止 TCP {firewallPort}\" dir=out action=block protocol=TCP remoteport={firewallPort}", out actionError),
                "RemoveXToolFirewallRule" => int.TryParse(request.Value, out var removeFirewallPort) && removeFirewallPort is >= 1 and <= 65535 &&
                    TryRunSystemCommand("netsh.exe", $"advfirewall firewall delete rule name=\"X-Tool 临时阻止 TCP {removeFirewallPort}\"", out actionError),
                "SyncWinHttpProxy" => TryRunSystemCommand("netsh.exe", "winhttp import proxy source=ie", out actionError),
                "ResetWinHttpProxy" => TryRunSystemCommand("netsh.exe", "winhttp reset proxy", out actionError),
                _ => false
            };
            if (!succeeded && string.IsNullOrWhiteSpace(actionError)) actionError = "不支持的管理员操作";
            if (!string.IsNullOrWhiteSpace(request.ResultPath))
            {
                File.WriteAllText(request.ResultPath, JsonSerializer.Serialize(new ElevatedSystemActionResult(succeeded, actionError)), new UTF8Encoding(false));
            }
            return succeeded ? 0 : 5;
        }
        catch (Exception exception)
        {
            try
            {
                var request = JsonSerializer.Deserialize<ElevatedSystemActionRequest>(File.ReadAllText(requestPath, Encoding.UTF8));
                if (!string.IsNullOrWhiteSpace(request?.ResultPath)) File.WriteAllText(request.ResultPath, JsonSerializer.Serialize(new ElevatedSystemActionResult(false, exception.Message)), new UTF8Encoding(false));
            }
            catch { }
            return 5;
        }
        finally
        {
            try { File.Delete(requestPath); }
            catch { /* 临时文件会由父进程再次兜底清理。 */ }
        }
    }

    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool RunElevatedSystemAction(ElevatedSystemActionRequest request, string failureMessage, out string? error)
    {
        var requestPath = Path.Combine(Path.GetTempPath(), $"xtool-system-action-{Guid.NewGuid():N}.json");
        var resultPath = Path.ChangeExtension(requestPath, ".result.json");
        try
        {
            request = request with { ResultPath = resultPath };
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                error = "未能找到 X-Tool 可执行文件，无法请求管理员授权";
                return false;
            }

            using var process = Process.Start(new ProcessStartInfo(executablePath, $"--apply-elevated-system-action \"{requestPath}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
            {
                error = "无法启动管理员授权进程";
                return false;
            }

            process.WaitForExit();
            ElevatedSystemActionResult? actionResult = null;
            if (File.Exists(resultPath))
            {
                try { actionResult = JsonSerializer.Deserialize<ElevatedSystemActionResult>(File.ReadAllText(resultPath, Encoding.UTF8)); }
                catch { }
            }
            if (process.ExitCode == 0 && actionResult?.Succeeded != false)
            {
                error = null;
                return true;
            }

            error = string.IsNullOrWhiteSpace(actionResult?.Error) ? failureMessage : actionResult.Error;
            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            error = "已取消管理员授权，操作未执行";
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            try { File.Delete(requestPath); }
            catch { /* 管理员子进程可能已经清理临时文件。 */ }
            try { File.Delete(resultPath); }
            catch { /* 结果文件仅用于父子进程传递错误信息。 */ }
        }
    }

    private static bool RequiresElevation(string? error)
    {
        return !string.IsNullOrWhiteSpace(error) &&
               (error.Contains("权限", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("Access is denied", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TrySetNetworkAdapterState(string adapterName, bool enabled, out string? error)
        => TryRunSystemCommand("netsh.exe", $"interface set interface name=\"{EscapeCommandValue(adapterName)}\" admin={(enabled ? "enabled" : "disabled")}", out error);

    private static bool TryRenewNetworkAdapterDhcp(string adapterName, out string? error)
    {
        if (!TryRunSystemCommand("ipconfig.exe", $"/release \"{EscapeCommandValue(adapterName)}\"", out error)) return false;
        return TryRunSystemCommand("ipconfig.exe", $"/renew \"{EscapeCommandValue(adapterName)}\"", out error);
    }

    private static bool TrySetNetworkAdapterDns(string adapterName, string primaryDns, string secondaryDns, out string? error)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            error = "网卡名称不能为空";
            return false;
        }
        if (string.IsNullOrWhiteSpace(primaryDns))
        {
            return TryRunSystemCommand("netsh.exe", $"interface ipv4 set dnsservers name=\"{EscapeCommandValue(adapterName)}\" source=dhcp", out error);
        }
        if (!IPAddress.TryParse(primaryDns, out var primary) || primary.AddressFamily != AddressFamily.InterNetwork ||
            (!string.IsNullOrWhiteSpace(secondaryDns) && (!IPAddress.TryParse(secondaryDns, out var secondary) || secondary.AddressFamily != AddressFamily.InterNetwork)))
        {
            error = "DNS 必须是有效的 IPv4 地址";
            return false;
        }
        if (!TryRunSystemCommand("netsh.exe", $"interface ipv4 set dnsservers name=\"{EscapeCommandValue(adapterName)}\" source=static address=\"{primaryDns}\" validate=no", out error)) return false;
        return string.IsNullOrWhiteSpace(secondaryDns) || TryRunSystemCommand("netsh.exe", $"interface ipv4 add dnsservers name=\"{EscapeCommandValue(adapterName)}\" address=\"{secondaryDns}\" index=2 validate=no", out error);
    }

    private static bool TrySetNetworkAdapterAddress(string adapterName, string value, string extra, out string? error)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) { error = "网卡名称不能为空"; return false; }
        if (string.Equals(value, "dhcp", StringComparison.OrdinalIgnoreCase))
            return TryRunSystemCommand("netsh.exe", $"interface ipv4 set address name=\"{EscapeCommandValue(adapterName)}\" source=dhcp", out error);
        var parts = extra.Split('|');
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            parts.Length != 2 || !int.TryParse(parts[0], out var prefix) || prefix is < 1 or > 32 ||
            !IPAddress.TryParse(parts[1], out var gateway) || gateway.AddressFamily != AddressFamily.InterNetwork)
        { error = "静态 IPv4、前缀长度或网关无效"; return false; }
        var maskValue = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var mask = string.Join('.', new[] { 24, 16, 8, 0 }.Select(shift => ((maskValue >> shift) & 255).ToString()));
        return TryRunSystemCommand("netsh.exe", $"interface ipv4 set address name=\"{EscapeCommandValue(adapterName)}\" source=static address={value} mask={mask} gateway={parts[1]} store=persistent", out error);
    }

    private static bool TryRunSystemCommand(string fileName, string arguments, out string? error)
    {
        var result = RunCommandWithExitCode(fileName, arguments);
        if (result.ExitCode == 0)
        {
            error = null;
            return true;
        }
        error = string.IsNullOrWhiteSpace(result.Output) ? $"{fileName} 返回错误代码 {result.ExitCode}" : result.Output.Trim();
        return false;
    }

    private static string EscapeCommandValue(string value) => value.Replace("\"", string.Empty).Trim();

    private sealed record ElevatedSystemActionRequest(string Action, string? Name = null, string? Value = null, string? ExtraValue = null, int[]? ProcessIds = null, bool Start = false, string? ResultPath = null);
    private sealed record ElevatedSystemActionResult(bool Succeeded, string? Error);

    private static string RunCommand(string fileName, string arguments)
        => RunCommandWithExitCode(fileName, arguments).Output;

    private static CommandResult RunCommandWithExitCode(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null) return new CommandResult(-1, string.Empty);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(8000);
        return new CommandResult(process.HasExited ? process.ExitCode : -1, string.IsNullOrWhiteSpace(output) ? error : output);
    }

    private sealed record CommandResult(int ExitCode, string Output);

    private static (string Name, string Path) GetProcessDetails(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string path = string.Empty;
            try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
            return (process.ProcessName, path);
        }
        catch
        {
            return ("系统或已退出的进程", string.Empty);
        }
    }

    private static int ExtractPort(string address)
    {
        var separator = address.LastIndexOf(':');
        return separator >= 0 && int.TryParse(address[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : int.MaxValue;
    }

    private static string NormalizeServiceState(string state) => state.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) || state.Contains("运行", StringComparison.OrdinalIgnoreCase) ? "运行中" : "已停止";

    private static void BroadcastEnvironmentChanged() => SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters lpIoCounters);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary>进程一次性能采样的累计计数，用于下次刷新计算速率。</summary>
    private sealed record ProcessSample(string Name, int ProcessId, TimeSpan TotalProcessorTime, long MemoryBytes, string Path, DateTime? StartedAt, ulong ReadTransferCount, ulong WriteTransferCount, ulong OtherTransferCount, long Timestamp);
}

public sealed record PortEntry(string Protocol, string LocalAddress, string RemoteAddress, string State, int ProcessId, string ProcessName, string ProcessPath)
{
    /// <summary>从本地地址提取端口，IPv4、IPv6 与通配地址均适用。</summary>
    public int Port
    {
        get
        {
            var separator = LocalAddress.LastIndexOf(':');
            return separator >= 0 && int.TryParse(LocalAddress[(separator + 1)..], out var port) ? port : 0;
        }
    }

    /// <summary>标记 IPv6 地址，供端口列表的显示筛选使用。</summary>
    public bool IsIpv6 => LocalAddress.StartsWith("[", StringComparison.Ordinal);

    /// <summary>以系统目录和核心 PID 识别系统进程，避免把应用自身的端口一并隐藏。</summary>
    public bool IsSystemProcess
    {
        get
        {
            if (ProcessId is > 0 and <= 4) return true;
            if (string.IsNullOrWhiteSpace(ProcessPath)) return false;
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return !string.IsNullOrWhiteSpace(windowsDirectory) && ProcessPath.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed record ProcessEntry(string Name, int ProcessId, double CpuPercent, long MemoryBytes, double DiskBytesPerSecond, double NetworkBitsPerSecond, string Path, DateTime? StartedAt)
{
    public string CpuText => $"{CpuPercent:F1}%";
    public string MemoryText => $"{MemoryBytes / 1024d / 1024d:F1} MB";
    public bool IsActive => CpuPercent >= 0.1 || DiskBytesPerSecond >= 10_000 || NetworkBitsPerSecond >= 10_000;
    public bool IsSystemProcess
    {
        get
        {
            if (ProcessId is > 0 and <= 4) return true;
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return !string.IsNullOrWhiteSpace(windowsDirectory) && !string.IsNullOrWhiteSpace(Path) && Path.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }
    public string StartedAtText => StartedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
}

public sealed record ServiceEntry(string Name, string DisplayName, string Status, int ProcessId = 0)
{
    public bool ShouldStart => Status != "运行中";
    public string ToggleActionText => ShouldStart ? "启动" : "停止";
}

/// <summary>同一时刻采集的系统关系数据，供关联关系页在单次刷新内稳定建立 PID 映射。</summary>
public sealed record SystemRelationshipSnapshot(IReadOnlyList<ProcessEntry> Processes, IReadOnlyList<PortEntry> Ports, IReadOnlyList<ServiceEntry> Services, DateTime CapturedAt);

/// <summary>以运行进程为核心的关联展示行；PID 与启动时间共同标识该次运行实例。</summary>
public sealed record SystemRelationshipEntry(ProcessEntry Process, IReadOnlyList<ServiceEntry> Services, IReadOnlyList<PortEntry> NetworkEntries)
{
    public int ProcessId => Process.ProcessId;
    public DateTime? StartedAt => Process.StartedAt;
    public string ProcessName => Process.Name;
    public string ProcessPath => Process.Path;
    public double CpuPercent => Process.CpuPercent;
    public long MemoryBytes => Process.MemoryBytes;
    public bool HasServices => Services.Count > 0;
    public bool HasNetworkActivity => NetworkEntries.Count > 0;
    public bool HasListeningPorts => NetworkEntries.Any(IsListening);
    public string ServiceSummary => Services.Count == 0 ? "无" : Services.Count == 1 ? Services[0].DisplayName : $"{Services.Count} 个服务";
    public string PortSummary
    {
        get
        {
            var ports = NetworkEntries.Select(item => item.Port).Where(item => item > 0).Distinct().OrderBy(item => item).ToArray();
            if (ports.Length == 0) return "—";
            var visible = string.Join(" · ", ports.Take(3));
            return ports.Length > 3 ? $"{visible} +{ports.Length - 3}" : visible;
        }
    }
    public string NetworkSummary
    {
        get
        {
            if (NetworkEntries.Count == 0) return "无";
            var listening = NetworkEntries.Count(IsListening);
            return listening > 0 ? $"{NetworkEntries.Count} 项 · {listening} 个监听" : $"{NetworkEntries.Count} 项连接";
        }
    }
    public string CpuText => $"{CpuPercent:F1}%";
    public string MemoryText => $"{MemoryBytes / 1024d / 1024d:F1} MB";
    public string StartedAtText => StartedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string InstanceKey => StartedAt.HasValue ? $"PID {ProcessId} · {StartedAt:yyyy-MM-dd HH:mm:ss}" : $"PID {ProcessId}";

    public static bool IsListening(PortEntry entry)
        => entry.State.Contains("LISTEN", StringComparison.OrdinalIgnoreCase) || entry.State.Contains("监听", StringComparison.Ordinal);
}

public sealed record EnvironmentVariableEntry(string Name, string Value, EnvironmentVariableTarget Target);

/// <summary>系统概览的只读快照，界面只渲染已采集数据。</summary>
public sealed record SystemOverview(IReadOnlyList<SystemInfoItem> Items, DateTime CapturedAt);

public sealed record SystemInfoItem(string Key, string Title, string Primary, string Detail, string Accent, string Icon);

/// <summary>按参考硬件面板组织的属性快照。</summary>
public sealed record HardwareOverview(IReadOnlyList<HardwarePropertyItem> Items, DateTime CapturedAt);

public sealed record HardwarePropertyItem(string Title, string Primary, string Attributes, string Secondary, string Accent, string Icon);

/// <summary>Windows PnP 驱动的只读展示项；状态来自设备管理器公开的配置状态。</summary>
public sealed record DeviceDriverEntry(
    string DeviceId,
    string DeviceName,
    string Category,
    string Provider,
    string Version,
    string DriverDateText,
    string InfName,
    bool IsSigned,
    string Signer,
    int? ErrorCode,
    string DeviceStatus,
    string ErrorDescription,
    bool HasDeviceState)
{
    public bool HasIssue => ErrorCode is > 0 ||
                            (HasDeviceState && !string.IsNullOrWhiteSpace(DeviceStatus) && !string.Equals(DeviceStatus, "OK", StringComparison.OrdinalIgnoreCase));

    public bool IsKeyDevice => Category is "显卡" or "网络" or "蓝牙" or "音频" or "存储" or "显示器" or "摄像头" or "输入设备" or "USB" or "端口";

    public string Icon => Category switch
    {
        "显卡" => "\uE950",
        "网络" => "\uE701",
        "蓝牙" => "\uE702",
        "音频" => "\uE767",
        "存储" => "\uEDA2",
        "显示器" => "\uE7F9",
        "摄像头" => "\uE722",
        "输入设备" => "\uE765",
        "USB" => "\uE88E",
        "端口" => "\uE8B7",
        "系统设备" => "\uEEA1",
        "软件组件" => "\uE950",
        _ => "\uE9CE"
    };

    public string Accent => Category switch
    {
        "显卡" => "#7D63F1",
        "网络" => "#2F9AF5",
        "蓝牙" => "#4D7CFE",
        "音频" => "#D97965",
        "存储" => "#16B99B",
        "显示器" => "#607D91",
        "摄像头" => "#8B6CFF",
        "输入设备" => "#168BBF",
        "USB" => "#2F9AF5",
        "端口" => "#4D7CFE",
        "系统设备" => "#F06D75",
        "软件组件" => "#7890A6",
        _ => "#7890A6"
    };

    public int CategoryOrder => Category switch
    {
        "显卡" => 0,
        "网络" => 1,
        "蓝牙" => 2,
        "音频" => 3,
        "存储" => 4,
        "显示器" => 5,
        "摄像头" => 6,
        "输入设备" => 7,
        "USB" => 8,
        "端口" => 9,
        "系统设备" => 10,
        "软件组件" => 11,
        _ => 12
    };

    public string StatusText
    {
        get
        {
            if (ErrorCode == 0) return "正常";
            if (ErrorCode == 14) return "需重启";
            if (ErrorCode == 22) return "已禁用";
            if (ErrorCode == 28) return "未安装驱动";
            if (ErrorCode is > 0) return $"错误 {ErrorCode}";
            if (!HasDeviceState) return "已识别";
            if (string.Equals(DeviceStatus, "OK", StringComparison.OrdinalIgnoreCase)) return "正常";
            return string.IsNullOrWhiteSpace(DeviceStatus) ? "状态未知" : DeviceStatus;
        }
    }

    public string StatusAccent => HasIssue ? "#E16670" : StatusText == "正常" ? "#20B783" : "#4D7CFE";

    public string InfText
    {
        get
        {
            var inf = string.IsNullOrWhiteSpace(InfName) ? "INF 未提供" : InfName;
            return $"{inf} · {(IsSigned ? "已签名" : "签名未确认")}";
        }
    }

    public string SearchText => $"{DeviceName} {Category} {Provider} {Version} {InfName} {Signer} {DeviceId} {ErrorDescription}";
}

/// <summary>同一设备类别的驱动展示组，默认折叠以避免长驱动清单淹没设备页。</summary>
public sealed class DeviceDriverCategoryGroup : INotifyPropertyChanged
{
    private bool _isExpanded;

    public DeviceDriverCategoryGroup(string category, IReadOnlyList<DeviceDriverEntry> drivers, bool isExpanded)
    {
        Category = category;
        Drivers = drivers;
        _isExpanded = isExpanded;
    }

    public string Category { get; }
    public IReadOnlyList<DeviceDriverEntry> Drivers { get; }
    public int Count => Drivers.Count;
    public int IssueCount => Drivers.Count(item => item.HasIssue);
    public string Icon => Drivers.FirstOrDefault()?.Icon ?? "\uE9CE";
    public string Accent => Drivers.FirstOrDefault()?.Accent ?? "#7890A6";
    public string SummaryText => IssueCount > 0 ? $"{Count} 个驱动 · {IssueCount} 项需要注意" : $"{Count} 个驱动 · 状态正常";
    public string IssueSummary => IssueCount > 0 ? $"{IssueCount} 项注意" : "状态正常";
    public string IssueAccent => IssueCount > 0 ? "#E16670" : "#20B783";
    public string ToggleText => IsExpanded ? "收起" : "展开";
    public string ToggleIcon => IsExpanded ? "\uE70E" : "\uE70D";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(ToggleText));
            OnPropertyChanged(nameof(ToggleIcon));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>存储页的只读容量快照，不包含 SMART、温度或健康评估。</summary>
public sealed record StorageOverview(
    IReadOnlyList<StorageVolumeEntry> Volumes,
    IReadOnlyList<PhysicalStorageEntry> PhysicalDisks,
    long TotalBytes,
    long FreeBytes,
    DateTime CapturedAt)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Clamp(UsedBytes * 100d / TotalBytes, 0, 100);
    public string UsedText => StorageDisplay.FormatCapacity(UsedBytes);
    public string FreeText => StorageDisplay.FormatCapacity(FreeBytes);
    public string TotalText => StorageDisplay.FormatCapacity(TotalBytes);
    public string UsageAccent => TotalBytes <= 0 ? "#7890A6" : UsedPercent >= 90 ? "#E16670" : UsedPercent >= 75 ? "#F3A847" : "#16B99B";
    public string VolumeSummaryText => Volumes.Count == 0 ? "未发现已挂载的固定磁盘" : $"{Volumes.Count} 个已挂载本地卷 · {PhysicalDisks.Count} 块物理磁盘";
    public string SummaryText => Volumes.Count == 0 ? "未读取到可用的本地卷" : $"{VolumeSummaryText} · 更新于 {CapturedAt:HH:mm:ss}";
}

/// <summary>一个已挂载固定磁盘的容量信息。</summary>
public sealed record StorageVolumeEntry(string DriveName, string VolumeLabel, string FileSystem, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Clamp(UsedBytes * 100d / TotalBytes, 0, 100);
    public string Title => string.Equals(VolumeLabel, "本地磁盘", StringComparison.Ordinal) ? $"{DriveName} 本地磁盘" : $"{VolumeLabel} ({DriveName})";
    public string DetailText => $"{FileSystem} · 已用 {UsedPercent:F0}%";
    public string UsedPercentText => $"{UsedPercent:F0}% 已用";
    public string UsedText => StorageDisplay.FormatCapacity(UsedBytes);
    public string FreeText => StorageDisplay.FormatCapacity(FreeBytes);
    public string TotalText => StorageDisplay.FormatCapacity(TotalBytes);
    public string UsageAccent => UsedPercent >= 90 ? "#E16670" : UsedPercent >= 75 ? "#F3A847" : "#16B99B";
}

/// <summary>Windows 识别到的物理磁盘摘要，不尝试把卷强行映射到单块磁盘。</summary>
public sealed record PhysicalStorageEntry(string Model, string InterfaceType, string MediaType, long CapacityBytes)
{
    public string CapacityText => StorageDisplay.FormatCapacity(CapacityBytes);
    public string DetailText => $"{InterfaceType} · {MediaType}";
}

internal static class StorageDisplay
{
    public static string FormatCapacity(long value)
    {
        if (value <= 0) return "—";
        if (value >= 1024L * 1024 * 1024 * 1024) return $"{value / 1024d / 1024d / 1024d / 1024d:F1} TB";
        return $"{value / 1024d / 1024d / 1024d:F0} GB";
    }
}
