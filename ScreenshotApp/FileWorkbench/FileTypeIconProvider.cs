using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.FileWorkbench;

/// <summary>按扩展名读取 Windows 关联的小图标，并冻结缓存供大量重复文件行复用。</summary>
internal static class FileTypeIconProvider
{
    private const uint FileAttributeNormal = 0x00000080;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    internal static ImageSource? GetIcon(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        var cacheKey = string.IsNullOrWhiteSpace(extension) ? "<无扩展名>" : extension.ToLowerInvariant();
        if (IconCache.TryGetValue(cacheKey, out var cachedIcon))
        {
            return cachedIcon;
        }

        var representativePath = string.IsNullOrWhiteSpace(extension) ? "file" : $"file{extension}";
        try
        {
            var fileInfo = new ShellFileInfo();
            var result = ShGetFileInfo(
                representativePath,
                FileAttributeNormal,
                ref fileInfo,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);
            if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var icon = Imaging.CreateBitmapSourceFromHIcon(
                    fileInfo.IconHandle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
                IconCache.TryAdd(cacheKey, icon);
                return icon;
            }
            finally
            {
                DestroyIcon(fileInfo.IconHandle);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            // 图标属于装饰信息；Shell 接口不可用时回退通用图标，不能中断重复文件扫描。
            return null;
        }
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ShGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        internal IntPtr IconHandle;
        internal int IconIndex;
        internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        internal string TypeName;
    }
}
