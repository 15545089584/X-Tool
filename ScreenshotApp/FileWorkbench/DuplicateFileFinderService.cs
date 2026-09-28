using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ScreenshotApp.FileWorkbench;

/// <summary>对指定目录执行一次性重复文件查找；不建立常驻索引，也不依赖普通搜索结果池。</summary>
internal static class DuplicateFileFinderService
{
    private const int SampleBlockSize = 64 * 1024;
    private const int FullHashBufferSize = 1024 * 1024;
    /// <summary>小文件的打开与寻址成本高于额外读取，直接一次完整哈希可避免二次打开。</summary>
    private const long SmallFileSinglePassThreshold = 512L * 1024;

    internal static DuplicateFileSearchResult FindDuplicates(
        string rootDirectory,
        long minimumFileSize,
        IReadOnlyList<FileWorkbenchItem>? reusableFiles,
        IProgress<DuplicateFileSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var candidatesBySize = new Dictionary<long, List<DuplicateFileCandidate>>();
        var scannedFiles = 0L;
        var skippedFiles = 0L;
        var sizeCandidateFileCount = 0L;
        var currentPath = rootDirectory;
        var progressStopwatch = Stopwatch.StartNew();
        if (reusableFiles is not null)
        {
            foreach (var item in reusableFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedFiles++;
                currentPath = item.FullPath;
                AddMetadata(item.FullPath, item.Size, item.ModifiedAt.ToUniversalTime());
            }
        }
        else
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var path in Directory.EnumerateFiles(rootDirectory, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedFiles++;
                currentPath = path;
                try
                {
                    var info = new FileInfo(path);
                    AddMetadata(info.FullName, info.Length, info.LastWriteTimeUtc);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skippedFiles++;
                }

                ReportProgress(DuplicateFileSearchStage.Enumerating, 0, sizeCandidateFileCount, force: false);
            }
        }

        var sizeCandidateGroups = candidatesBySize.Values.Where(group => group.Count > 1).ToArray();
        var candidateFileCount = sizeCandidateGroups.Sum(group => group.Count);
        progress?.Report(new DuplicateFileSearchProgress(
            DuplicateFileSearchStage.QuickHashing,
            scannedFiles,
            candidateFileCount,
            0,
            skippedFiles,
            rootDirectory));

        var quickGroups = new List<QuickHashCandidateGroup>();
        var processedCandidates = 0L;
        foreach (var sizeGroup in sizeCandidateGroups)
        {
            var byQuickHash = new Dictionary<string, List<DuplicateFileCandidate>>(StringComparer.Ordinal);
            var quickHashIsFullHash = sizeGroup[0].Size <= SmallFileSinglePassThreshold;
            foreach (var candidate in sizeGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentPath = candidate.FullPath;
                try
                {
                    var quickHash = quickHashIsFullHash
                        ? ComputeFullHash(candidate, cancellationToken)
                        : ComputeQuickHash(candidate, cancellationToken);
                    AddCandidate(byQuickHash, quickHash, candidate);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skippedFiles++;
                }

                processedCandidates++;
                ReportProgress(DuplicateFileSearchStage.QuickHashing, processedCandidates, candidateFileCount, force: false);
            }

            quickGroups.AddRange(byQuickHash
                .Where(pair => pair.Value.Count > 1)
                .Select(pair => new QuickHashCandidateGroup(pair.Key, pair.Value, quickHashIsFullHash)));
        }

        var fullHashCandidateCount = quickGroups
            .Where(group => !group.HashIsComplete)
            .Sum(group => group.Candidates.Count);
        var fullHashProcessed = 0L;
        var duplicateGroups = new List<DuplicateFileGroup>();
        progress?.Report(new DuplicateFileSearchProgress(
            DuplicateFileSearchStage.FullHashing,
            scannedFiles,
            fullHashCandidateCount,
            0,
            skippedFiles,
            rootDirectory));

        foreach (var quickGroup in quickGroups)
        {
            if (quickGroup.HashIsComplete)
            {
                AddConfirmedDuplicateGroup(quickGroup.Hash, quickGroup.Candidates);
                continue;
            }

            var byFullHash = new Dictionary<string, List<DuplicateFileCandidate>>(StringComparer.Ordinal);
            foreach (var candidate in quickGroup.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentPath = candidate.FullPath;
                try
                {
                    var fullHash = ComputeFullHash(candidate, cancellationToken);
                    AddCandidate(byFullHash, fullHash, candidate);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skippedFiles++;
                }

                fullHashProcessed++;
                ReportProgress(DuplicateFileSearchStage.FullHashing, fullHashProcessed, fullHashCandidateCount, force: false);
            }

            foreach (var hashGroup in byFullHash)
            {
                AddConfirmedDuplicateGroup(hashGroup.Key, hashGroup.Value);
            }
        }

        ReportProgress(DuplicateFileSearchStage.Completed, fullHashProcessed, fullHashCandidateCount, force: true);
        var orderedGroups = duplicateGroups
            .OrderByDescending(group => group.FileSize)
            .ThenByDescending(group => group.ReclaimableSize)
            .ThenBy(group => group.Entries[0].FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DuplicateFileSearchResult(orderedGroups, scannedFiles, candidateFileCount, skippedFiles);

        void AddConfirmedDuplicateGroup(string hash, IReadOnlyList<DuplicateFileCandidate> candidates)
        {
            var distinctPhysicalFiles = RemoveHardLinkAliases(candidates);
            if (distinctPhysicalFiles.Count <= 1)
            {
                return;
            }

            duplicateGroups.Add(new DuplicateFileGroup(
                hash,
                distinctPhysicalFiles.Select(candidate => new DuplicateFileEntry(
                    candidate.FullPath,
                    Path.GetFileName(candidate.FullPath),
                    candidate.Size,
                    candidate.LastWriteTimeUtc.ToLocalTime())).ToArray()));
        }

        void AddMetadata(string fullPath, long size, DateTime lastWriteTimeUtc)
        {
            if (size <= 0 || size < minimumFileSize)
            {
                return;
            }

            if (!candidatesBySize.TryGetValue(size, out var candidates))
            {
                candidates = new List<DuplicateFileCandidate>();
                candidatesBySize.Add(size, candidates);
            }

            sizeCandidateFileCount += candidates.Count switch
            {
                0 => 0,
                1 => 2,
                _ => 1
            };
            candidates.Add(new DuplicateFileCandidate(fullPath, size, lastWriteTimeUtc));
        }

        void ReportProgress(DuplicateFileSearchStage stage, long processed, long total, bool force)
        {
            if (progress is null || (!force && progressStopwatch.Elapsed < TimeSpan.FromMilliseconds(220)))
            {
                return;
            }

            progressStopwatch.Restart();
            progress.Report(new DuplicateFileSearchProgress(stage, scannedFiles, total, processed, skippedFiles, currentPath));
        }
    }

    private static void AddCandidate(
        IDictionary<string, List<DuplicateFileCandidate>> groups,
        string hash,
        DuplicateFileCandidate candidate)
    {
        if (!groups.TryGetValue(hash, out var group))
        {
            group = new List<DuplicateFileCandidate>();
            groups.Add(hash, group);
        }

        group.Add(candidate);
    }

    private static string ComputeQuickHash(DuplicateFileCandidate candidate, CancellationToken cancellationToken)
    {
        ValidateCandidate(candidate);
        using var stream = OpenRead(candidate.FullPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(candidate.Size));
        var offsets = new[]
        {
            0L,
            Math.Max(0L, candidate.Size / 2 - SampleBlockSize / 2),
            Math.Max(0L, candidate.Size - SampleBlockSize)
        };
        var buffer = new byte[SampleBlockSize];
        foreach (var offset in offsets.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = offset;
            var remaining = (int)Math.Min(SampleBlockSize, stream.Length - offset);
            var read = 0;
            while (read < remaining)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = stream.Read(buffer, read, remaining - read);
                if (count == 0)
                {
                    throw new IOException("读取文件样本时遇到意外结尾。");
                }

                read += count;
            }

            hash.AppendData(buffer, 0, read);
        }

        ValidateCandidate(candidate);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeFullHash(DuplicateFileCandidate candidate, CancellationToken cancellationToken)
    {
        ValidateCandidate(candidate);
        using var stream = OpenRead(candidate.FullPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[FullHashBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        ValidateCandidate(candidate);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        FullHashBufferSize,
        FileOptions.SequentialScan);

    private static void ValidateCandidate(DuplicateFileCandidate candidate)
    {
        var info = new FileInfo(candidate.FullPath);
        if (!info.Exists || info.Length != candidate.Size || info.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
        {
            throw new IOException("文件在扫描期间发生变化，已跳过。");
        }
    }

    private static IReadOnlyList<DuplicateFileCandidate> RemoveHardLinkAliases(IEnumerable<DuplicateFileCandidate> candidates)
    {
        var identities = new HashSet<FileIdentity>();
        var pathsWithoutIdentity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DuplicateFileCandidate>();
        foreach (var candidate in candidates)
        {
            if (TryGetFileIdentity(candidate.FullPath, out var identity))
            {
                if (!identities.Add(identity))
                {
                    continue;
                }
            }
            else if (!pathsWithoutIdentity.Add(candidate.FullPath))
            {
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    private static bool TryGetFileIdentity(string path, out FileIdentity identity)
    {
        identity = default;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info))
            {
                return false;
            }

            identity = new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex);
    private sealed record DuplicateFileCandidate(string FullPath, long Size, DateTime LastWriteTimeUtc);
    private sealed record QuickHashCandidateGroup(
        string Hash,
        IReadOnlyList<DuplicateFileCandidate> Candidates,
        bool HashIsComplete);
}

internal enum DuplicateFileSearchStage
{
    Enumerating,
    QuickHashing,
    FullHashing,
    Completed
}

internal sealed record DuplicateFileSearchProgress(
    DuplicateFileSearchStage Stage,
    long ScannedFiles,
    long CandidateFiles,
    long ProcessedCandidates,
    long SkippedFiles,
    string CurrentPath)
{
    internal string SummaryText => Stage switch
    {
        DuplicateFileSearchStage.Enumerating => $"正在枚举文件 · 已扫描 {ScannedFiles:N0} 个",
        DuplicateFileSearchStage.QuickHashing => $"正在快速比对 · {ProcessedCandidates:N0} / {CandidateFiles:N0}",
        DuplicateFileSearchStage.FullHashing => $"正在完整校验 · {ProcessedCandidates:N0} / {CandidateFiles:N0}",
        _ => "重复文件校验完成"
    };
}

internal sealed record DuplicateFileSearchResult(
    IReadOnlyList<DuplicateFileGroup> Groups,
    long ScannedFiles,
    long CandidateFiles,
    long SkippedFiles)
{
    internal int DuplicateFileCount => Groups.Sum(group => group.Entries.Count);
    internal long ReclaimableSize => Groups.Sum(group => group.ReclaimableSize);
}

internal sealed record DuplicateFileGroup(string ContentHash, IReadOnlyList<DuplicateFileEntry> Entries)
{
    internal long FileSize => Entries.Count == 0 ? 0 : Entries[0].Size;
    internal long ReclaimableSize => Math.Max(0, Entries.Count - 1) * FileSize;
}

internal sealed record DuplicateFileEntry(string FullPath, string FileName, long Size, DateTime ModifiedAt);
