using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenshotApp.StorageAnalysis;

/// <summary>
/// 通过 Everything 1.4+ 的公开 WM_COPYDATA IPC 读取现有索引。
/// 不查找安装目录、不启动或配置 Everything；不可用时由调用方回退原生扫描。
/// </summary>
internal static class EverythingStorageIndexClient
{
    private const uint EverythingWmIpc = 0x0400;
    private const uint WmCopyData = 0x004A;
    private const uint IsNtfsDriveIndexed = 400;
    private const uint IsDatabaseLoaded = 401;
    private const uint IsFileInfoIndexed = 411;
    private const uint FileSizeInfo = 1;
    private const uint CopyDataQuery2W = 18;
    private const uint RequestFullPath = 0x00000004;
    private const uint RequestSize = 0x00000010;
    private const uint SortNameAscending = 1;
    private const uint FolderFlag = 0x00000001;
    private const int PageSize = 20_000;
    private const int MaximumReplyBytes = 128 * 1024 * 1024;
    private const nuint ReplyId = 0x58544F4F;

    internal static bool TryAnalyze(
        string normalizedRoot,
        int largeFileLimit,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken,
        out EverythingStorageAnalysis? analysis)
    {
        analysis = null;
        if (normalizedRoot.Length < 2) return false;
        var driveLetter = char.ToUpperInvariant(normalizedRoot[0]);
        if (driveLetter is < 'A' or > 'Z') return false;

        var everythingWindow = FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
        if (everythingWindow == IntPtr.Zero)
            everythingWindow = FindWindow("EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)", null);
        if (everythingWindow == IntPtr.Zero ||
            SendMessage(everythingWindow, EverythingWmIpc, IsDatabaseLoaded, 0) == IntPtr.Zero ||
            SendMessage(everythingWindow, EverythingWmIpc, IsNtfsDriveIndexed, driveLetter - 'A') == IntPtr.Zero ||
            SendMessage(everythingWindow, EverythingWmIpc, IsFileInfoIndexed, (nint)FileSizeInfo) == IntPtr.Zero)
            return false;

        var completion = new TaskCompletionSource<EverythingStorageAnalysis?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(AnalyzeOnMessageThread(everythingWindow, normalizedRoot,
                    Math.Clamp(largeFileLimit, 1, 500), progress, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch
            {
                completion.TrySetResult(null);
            }
        })
        {
            IsBackground = true,
            Name = "XTool Everything IPC"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        try
        {
            while (!completion.Task.Wait(100)) cancellationToken.ThrowIfCancellationRequested();
            analysis = completion.Task.GetAwaiter().GetResult();
            return analysis is not null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static EverythingStorageAnalysis? AnalyzeOnMessageThread(
        IntPtr everythingWindow,
        string normalizedRoot,
        int largeFileLimit,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var receiver = new QueryReceiver();
        var usages = new Dictionary<string, MutableIndexedUsage>(StringComparer.OrdinalIgnoreCase);
        var largeFiles = new PriorityQueue<StorageLargeFile, long>();
        long files = 0;
        long directories = 0;
        long totalBytes = 0;
        uint offset = 0;
        uint totalItems = uint.MaxValue;
        var search = char.ToLowerInvariant(normalizedRoot[0]) + ":";
        var queryDeadline = DateTime.UtcNow.AddSeconds(60);

        while (offset < totalItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= queryDeadline) return null;
            receiver.BeginPage();
            if (!SendQuery(everythingWindow, receiver.Handle, search, offset, PageSize)) return null;
            var remaining = queryDeadline - DateTime.UtcNow;
            if (!receiver.WaitForPage(remaining < TimeSpan.FromSeconds(20) ? remaining : TimeSpan.FromSeconds(20), cancellationToken, out var page) ||
                page.RequestFlags != (RequestFullPath | RequestSize)) return null;

            totalItems = page.TotalItems;
            foreach (var entry in page.Entries)
            {
                if (!TryGetUsageKey(normalizedRoot, entry.FullPath, entry.IsDirectory,
                        out var key, out var fullPath, out var isRootFiles)) continue;
                if (!usages.TryGetValue(key, out var usage))
                {
                    usage = new MutableIndexedUsage(key, fullPath, isRootFiles);
                    usages.Add(key, usage);
                }

                if (entry.IsDirectory)
                {
                    usage.Directories++;
                    directories++;
                    continue;
                }

                usage.Files++;
                usage.Bytes += entry.Size;
                files++;
                totalBytes += entry.Size;
                if (entry.Size <= 0) continue;
                var largeFile = new StorageLargeFile(entry.FullPath, entry.Size);
                if (largeFiles.Count < largeFileLimit) largeFiles.Enqueue(largeFile, entry.Size);
                else if (largeFiles.TryPeek(out _, out var smallest) && entry.Size > smallest)
                {
                    largeFiles.Dequeue();
                    largeFiles.Enqueue(largeFile, entry.Size);
                }
            }

            offset += (uint)page.Entries.Count;
            progress?.Report(new StorageScanProgress(files, directories, 0,
                $"Everything 索引：{offset:N0} / {totalItems:N0} 项", "Everything 索引"));
            if (page.Entries.Count == 0 && offset < totalItems) return null;
        }

        var indexedUsages = usages.Values.Select(item => new EverythingDirectoryUsage(
            item.Name, item.FullPath, item.Bytes, item.Files, item.Directories, item.IsRootFiles)).ToArray();
        var topFiles = largeFiles.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(item => item.Size).ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        return new EverythingStorageAnalysis(indexedUsages, topFiles, files, directories, totalBytes);
    }

    private static bool TryGetUsageKey(
        string root,
        string path,
        bool isDirectory,
        out string key,
        out string fullPath,
        out bool isRootFiles)
    {
        key = string.Empty;
        fullPath = string.Empty;
        isRootFiles = false;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative)) return false;
        var separator = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        if (separator < 0 && !isDirectory)
        {
            key = "根目录文件";
            fullPath = root;
            isRootFiles = true;
            return true;
        }
        key = separator < 0 ? relative : relative[..separator];
        if (string.IsNullOrWhiteSpace(key)) return false;
        fullPath = Path.Combine(root, key);
        return true;
    }

    private static bool SendQuery(IntPtr everythingWindow, IntPtr replyWindow, string search, uint offset, uint maximumResults)
    {
        var searchBytes = Encoding.Unicode.GetBytes(search + '\0');
        var querySize = 28 + searchBytes.Length;
        var queryPointer = Marshal.AllocHGlobal(querySize);
        var copyDataPointer = IntPtr.Zero;
        try
        {
            Marshal.WriteInt32(queryPointer, 0, unchecked((int)replyWindow.ToInt64()));
            Marshal.WriteInt32(queryPointer, 4, unchecked((int)ReplyId));
            Marshal.WriteInt32(queryPointer, 8, 0);
            Marshal.WriteInt32(queryPointer, 12, unchecked((int)offset));
            Marshal.WriteInt32(queryPointer, 16, unchecked((int)maximumResults));
            Marshal.WriteInt32(queryPointer, 20, unchecked((int)(RequestFullPath | RequestSize)));
            Marshal.WriteInt32(queryPointer, 24, unchecked((int)SortNameAscending));
            Marshal.Copy(searchBytes, 0, IntPtr.Add(queryPointer, 28), searchBytes.Length);

            var copyData = new CopyDataStruct((IntPtr)CopyDataQuery2W, querySize, queryPointer);
            copyDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CopyDataStruct>());
            Marshal.StructureToPtr(copyData, copyDataPointer, false);
            return SendMessageTimeout(everythingWindow, WmCopyData, replyWindow, copyDataPointer,
                0x0002, 5000, out var response) != IntPtr.Zero && response != IntPtr.Zero;
        }
        finally
        {
            if (copyDataPointer != IntPtr.Zero) Marshal.FreeHGlobal(copyDataPointer);
            Marshal.FreeHGlobal(queryPointer);
        }
    }

    private sealed class QueryReceiver : IDisposable
    {
        private readonly HwndSource _source;
        private EverythingIndexPage? _page;

        public QueryReceiver()
        {
            _source = new HwndSource(new HwndSourceParameters("XTool.Everything.Ipc")
            {
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)0x80000000)
            });
            _source.AddHook(WindowHook);
        }

        public IntPtr Handle => _source.Handle;
        public void BeginPage() => _page = null;

        public bool WaitForPage(TimeSpan timeout, CancellationToken cancellationToken, out EverythingIndexPage page)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (_page is null && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            page = _page!;
            return _page is not null;
        }

        private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmCopyData) return IntPtr.Zero;
            var copyData = Marshal.PtrToStructure<CopyDataStruct>(lParam);
            if (unchecked((nuint)copyData.DataId.ToInt64()) != ReplyId || copyData.ByteCount is < 20 or > MaximumReplyBytes || copyData.Data == IntPtr.Zero)
                return IntPtr.Zero;
            try
            {
                _page = ParsePage(copyData.Data, copyData.ByteCount);
                handled = true;
                return (IntPtr)1;
            }
            catch
            {
                _page = null;
                return IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            _source.RemoveHook(WindowHook);
            _source.Dispose();
        }
    }

    private static EverythingIndexPage ParsePage(IntPtr data, int byteCount)
    {
        var totalItems = ReadUInt32(data, 0);
        var itemCount = ReadUInt32(data, 4);
        var requestFlags = ReadUInt32(data, 12);
        if (itemCount > 100_000 || 20L + itemCount * 8L > byteCount)
            throw new InvalidDataException("Everything 返回的索引页无效。");

        var entries = new List<EverythingIndexEntry>((int)itemCount);
        for (var index = 0; index < itemCount; index++)
        {
            var itemOffset = 20 + checked((int)index * 8);
            var flags = ReadUInt32(data, itemOffset);
            var contentOffset = ReadUInt32(data, itemOffset + 4);
            if (contentOffset > byteCount - 12) throw new InvalidDataException("Everything 返回的条目越界。");
            var pathLength = ReadUInt32(data, checked((int)contentOffset));
            var endOffset = contentOffset + 4L + (pathLength + 1L) * 2L + 8L;
            if (pathLength > 32767 || endOffset > byteCount) throw new InvalidDataException("Everything 返回的路径越界。");
            var path = Marshal.PtrToStringUni(IntPtr.Add(data, checked((int)contentOffset + 4)), checked((int)pathLength)) ?? string.Empty;
            var sizeOffset = checked((int)(contentOffset + 4 + (pathLength + 1) * 2));
            var size = Math.Max(0, Marshal.ReadInt64(data, sizeOffset));
            entries.Add(new EverythingIndexEntry(path, (flags & FolderFlag) != 0, size));
        }
        return new EverythingIndexPage(totalItems, requestFlags, entries);
    }

    private static uint ReadUInt32(IntPtr pointer, int offset) => unchecked((uint)Marshal.ReadInt32(pointer, offset));

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public CopyDataStruct(IntPtr dataId, int byteCount, IntPtr data)
        {
            DataId = dataId;
            ByteCount = byteCount;
            Data = data;
        }

        public IntPtr DataId;
        public int ByteCount;
        public IntPtr Data;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    private sealed class MutableIndexedUsage
    {
        public MutableIndexedUsage(string name, string fullPath, bool isRootFiles)
        {
            Name = name;
            FullPath = fullPath;
            IsRootFiles = isRootFiles;
        }
        public string Name { get; }
        public string FullPath { get; }
        public bool IsRootFiles { get; }
        public long Bytes { get; set; }
        public long Files { get; set; }
        public long Directories { get; set; }
    }

    private sealed record EverythingIndexEntry(string FullPath, bool IsDirectory, long Size);
    private sealed record EverythingIndexPage(uint TotalItems, uint RequestFlags, IReadOnlyList<EverythingIndexEntry> Entries);
}

internal sealed record EverythingStorageAnalysis(
    IReadOnlyList<EverythingDirectoryUsage> DirectoryUsages,
    IReadOnlyList<StorageLargeFile> LargeFiles,
    long FilesScanned,
    long DirectoriesScanned,
    long TotalBytes);

internal sealed record EverythingDirectoryUsage(
    string Name,
    string FullPath,
    long Bytes,
    long Files,
    long Directories,
    bool IsRootFiles);
