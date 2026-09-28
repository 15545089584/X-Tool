using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.DesktopPet;

/// <summary>仅在本次 X-Tool 运行期间保留的桌宠文件暂存区。</summary>
internal sealed class PetSessionShelfService : IDisposable
{
    internal const int MaximumItemCount = 200;
    internal const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif"
    };

    private readonly string _shelfRoot;
    private readonly string _sessionDirectory;
    private readonly List<PetShelfItem> _items = [];
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private bool _disposed;

    internal PetSessionShelfService(bool cleanupStaleSessions = true, string? storageRoot = null)
    {
        _shelfRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "X-Tool",
            "SessionShelf");
        Directory.CreateDirectory(_shelfRoot);
        if (cleanupStaleSessions)
        {
            CleanupStaleSessions();
        }

        _sessionDirectory = Path.Combine(_shelfRoot, $"session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sessionDirectory);
    }

    internal event Action? ItemsChanged;

    internal IReadOnlyList<PetShelfItem> Items => _items.ToArray();

    internal long TotalBytes => _items.Sum(item => item.Size);

    internal async Task<PetShelfAddResult> AddFilesAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        await _operationLock.WaitAsync(linkedCancellation.Token);
        try
        {
            var added = 0;
            var duplicates = 0;
            var failed = 0;
            string? limitMessage = null;

            foreach (var sourcePath in sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var fullSourcePath = Path.GetFullPath(sourcePath);
                    var sourceInfo = new FileInfo(fullSourcePath);
                    if (!sourceInfo.Exists)
                    {
                        failed++;
                        continue;
                    }

                    var sourceSignature = CreateSourceSignature(sourceInfo);
                    if (_items.Any(item => item.SourceSignature.Equals(sourceSignature, StringComparison.OrdinalIgnoreCase)))
                    {
                        duplicates++;
                        continue;
                    }

                    if (_items.Count >= MaximumItemCount)
                    {
                        limitMessage = $"暂存区最多保留 {MaximumItemCount} 项";
                        break;
                    }

                    if (TotalBytes + sourceInfo.Length > MaximumTotalBytes)
                    {
                        limitMessage = "暂存区总大小不能超过 2 GB";
                        break;
                    }

                    var extension = sourceInfo.Extension;
                    // 暂存副本保留原文件名，标准文件拖出与粘贴时不会变成随机编号；同名文件追加序号。
                    var storedPath = Path.Combine(_sessionDirectory, sourceInfo.Name);
                    for (var suffix = 2; File.Exists(storedPath); suffix++)
                    {
                        storedPath = Path.Combine(_sessionDirectory,
                            $"{Path.GetFileNameWithoutExtension(sourceInfo.Name)} ({suffix}){extension}");
                    }
                    var partialPath = Path.Combine(_sessionDirectory, $"{Guid.NewGuid():N}.partial");
                    try
                    {
                        await CopyFileAsync(fullSourcePath, partialPath, linkedCancellation.Token);
                        File.Move(partialPath, storedPath);
                    }
                    catch
                    {
                        TryDeleteFile(partialPath);
                        throw;
                    }

                    var isImage = ImageExtensions.Contains(extension);
                    var thumbnail = isImage ? TryLoadThumbnail(storedPath) : null;
                    _items.Insert(0, new PetShelfItem(
                        Guid.NewGuid(),
                        sourceInfo.Name,
                        storedPath,
                        fullSourcePath,
                        sourceSignature,
                        sourceInfo.Length,
                        DateTime.Now,
                        isImage,
                        thumbnail));
                    added++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    Debug.WriteLine($"文件存入桌宠暂存区失败：{exception.GetBaseException().Message}");
                }
            }

            if (added > 0)
            {
                ItemsChanged?.Invoke();
            }

            return new PetShelfAddResult(added, duplicates, failed, limitMessage);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    internal async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var failed = 0;
            foreach (var item in _items.ToArray())
            {
                try
                {
                    File.Delete(item.StoredPath);
                    _items.Remove(item);
                }
                catch
                {
                    failed++;
                }
            }

            ItemsChanged?.Invoke();
            return failed;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    internal async Task<BitmapSource?> LoadImageAsync(PetShelfItem item)
    {
        if (!item.IsImage || !File.Exists(item.StoredPath))
        {
            return null;
        }

        return await Task.Run<BitmapSource?>(() =>
        {
            try
            {
                using var stream = new FileStream(item.StoredPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.FirstOrDefault();
                frame?.Freeze();
                return frame;
            }
            catch
            {
                return null;
            }
        });
    }

    internal static bool TryOpen(PetShelfItem item)
    {
        if (!File.Exists(item.StoredPath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.StoredPath) { UseShellExecute = true });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.StoredPath}\"")
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCancellation.Cancel();
        _items.Clear();
        ItemsChanged?.Invoke();
        TryDeleteSessionDirectory(_sessionDirectory);
        _disposeCancellation.Dispose();
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static string CreateSourceSignature(FileInfo info) =>
        $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

    private static BitmapSource? TryLoadThumbnail(string filePath)
    {
        try
        {
            var thumbnail = new BitmapImage();
            thumbnail.BeginInit();
            thumbnail.CacheOption = BitmapCacheOption.OnLoad;
            thumbnail.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            thumbnail.DecodePixelWidth = 96;
            thumbnail.UriSource = new Uri(filePath, UriKind.Absolute);
            thumbnail.EndInit();
            thumbnail.Freeze();
            return thumbnail;
        }
        catch
        {
            return null;
        }
    }

    private void CleanupStaleSessions()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_shelfRoot, "session-*", SearchOption.TopDirectoryOnly))
            {
                TryDeleteSessionDirectory(directory);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"清理上次桌宠暂存区失败：{exception.GetBaseException().Message}");
        }
    }

    private void TryDeleteSessionDirectory(string directory)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(_shelfRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!normalizedDirectory.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalizedDirectory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"删除桌宠暂存目录失败：{exception.GetBaseException().Message}");
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // 下次启动时会按会话目录整体清理。
        }
    }
}

internal sealed record PetShelfItem(
    Guid Id,
    string DisplayName,
    string StoredPath,
    string SourcePath,
    string SourceSignature,
    long Size,
    DateTime AddedAt,
    bool IsImage,
    BitmapSource? Thumbnail);

internal sealed record PetShelfAddResult(
    int AddedCount,
    int DuplicateCount,
    int FailedCount,
    string? LimitMessage);
