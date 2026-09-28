using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace ScreenshotApp.Archives;

public sealed class ArchiveService
{
    private static readonly string[] SupportedArchiveExtensions =
    [
        ".tar.gz", ".tar.bz2", ".tar.xz", ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz",
        ".bz2", ".xz", ".lzip", ".zst", ".arc", ".arj", ".ace"
    ];

    public static readonly string OpenArchiveFilter =
        "支持的压缩包|*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.lzip;*.zst;*.arc;*.arj;*.ace|" +
        "ZIP 压缩包|*.zip|7z 压缩包|*.7z|RAR 压缩包|*.rar|TAR 与压缩流|*.tar;*.gz;*.tgz;*.bz2;*.xz;*.lzip;*.zst|所有文件|*.*";

    public static bool IsSupportedArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return SupportedArchiveExtensions.Any(extension =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    public Task<ArchiveInspection> InspectAsync(
        string archivePath,
        string? password,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => InspectCore(archivePath, password, cancellationToken), cancellationToken);
    }

    public Task<ArchiveOperationResult> CreateAsync(
        IReadOnlyCollection<string> sourcePaths,
        string destinationArchive,
        ArchiveOutputFormat format,
        ArchiveCompressionPreset preset,
        bool includeTopLevelDirectory,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => CreateCore(
                sourcePaths,
                destinationArchive,
                format,
                preset,
                includeTopLevelDirectory,
                progress,
                cancellationToken),
            cancellationToken);
    }

    public Task<ArchiveOperationResult> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        string? password,
        ArchiveConflictPolicy conflictPolicy,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => ExtractCore(
                archivePath,
                destinationDirectory,
                password,
                conflictPolicy,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private static ArchiveInspection InspectCore(
        string archivePath,
        string? password,
        CancellationToken cancellationToken)
    {
        ValidateArchivePath(archivePath);
        var options = CreateReaderOptions(password);
        var archiveInformation = ArchiveFactory.GetArchiveInformation(archivePath, options);
        var previewEntries = new List<ArchivePreviewItem>();
        var totalEntries = 0;
        long totalBytes = 0;
        var encrypted = false;
        ArchiveType? type = archiveInformation?.Type;

        if (archiveInformation?.SupportsRandomAccess == true)
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, options);
            type = archive.Type;
            encrypted = archive.IsEncrypted;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddInspectionEntry(entry, previewEntries, ref totalEntries, ref totalBytes, ref encrypted);
            }
        }
        else
        {
            using var reader = ReaderFactory.OpenReader(archivePath, options);
            type = reader.Type;
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddInspectionEntry(reader.Entry, previewEntries, ref totalEntries, ref totalBytes, ref encrypted);
            }
        }

        ValidateArchiveTotals(archivePath, totalEntries, totalBytes);
        return new ArchiveInspection(
            GetFormatDisplayName(type),
            previewEntries,
            totalEntries,
            totalBytes,
            encrypted,
            totalEntries > previewEntries.Count);
    }

    private static void AddInspectionEntry(
        IEntry entry,
        ICollection<ArchivePreviewItem> previewEntries,
        ref int totalEntries,
        ref long totalBytes,
        ref bool encrypted)
    {
        var entryPath = entry.Key
            ?? throw new InvalidDataException("压缩包包含未命名条目，已停止读取。");
        totalEntries++;
        if (totalEntries > ArchiveSafety.MaximumEntryCount)
        {
            throw new InvalidDataException($"压缩包条目超过 {ArchiveSafety.MaximumEntryCount:N0} 个，已停止读取。");
        }

        ArchiveSafety.ValidateEntryMetadata(entryPath, entry.Size, entry.LinkTarget);
        _ = ArchiveSafety.ResolveSafeDestination(
            Path.Combine(Path.GetTempPath(), "xtool-archive-validation"),
            entryPath);
        if (!entry.IsDirectory)
        {
            totalBytes = checked(totalBytes + entry.Size);
        }

        encrypted |= entry.IsEncrypted;
        if (previewEntries.Count < ArchiveSafety.MaximumPreviewEntryCount)
        {
            previewEntries.Add(new ArchivePreviewItem(
                entryPath,
                entry.Size,
                entry.IsDirectory,
                entry.IsEncrypted,
                entry.LastModifiedTime));
        }
    }

    private static ArchiveOperationResult CreateCore(
        IReadOnlyCollection<string> sourcePaths,
        string destinationArchive,
        ArchiveOutputFormat format,
        ArchiveCompressionPreset preset,
        bool includeTopLevelDirectory,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        if (sourcePaths.Count == 0)
        {
            throw new InvalidOperationException("请先添加需要压缩的文件或文件夹。");
        }

        var destinationPath = Path.GetFullPath(destinationArchive);
        var outputDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("无法确定压缩包输出目录。");
        Directory.CreateDirectory(outputDirectory);

        var inputEntries = EnumerateInputEntries(
            sourcePaths,
            includeTopLevelDirectory,
            destinationPath,
            cancellationToken);
        if (inputEntries.Count == 0)
        {
            throw new InvalidOperationException("没有找到可以压缩的内容。");
        }

        var totalBytes = inputEntries.Where(item => !item.IsDirectory).Sum(item => item.Size);
        ArchiveSafety.EnsureSufficientFreeSpace(outputDirectory, totalBytes);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.xtool-partial");
        long processedBytes = 0;
        var processedFiles = 0;

        try
        {
            var (archiveType, compressionType) = ResolveWriterFormat(format, preset);
            var compressionLevel = compressionType == CompressionType.LZMA2
                ? 0
                : ResolveCompressionLevel(preset);
            var writerOptions = new WriterOptions(compressionType, compressionLevel)
            {
                ArchiveEncoding = new ArchiveEncoding
                {
                    Default = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    UTF8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    Password = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    Forced = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                },
                LeaveStreamOpen = false,
                BufferSize = 128 * 1024
            };

            using (var outputStream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.SequentialScan))
            using (var writer = WriterFactory.OpenWriter(outputStream, archiveType, writerOptions))
            {
                foreach (var item in inputEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.IsDirectory)
                    {
                        writer.WriteDirectory(item.EntryPath, item.LastModifiedTime);
                        continue;
                    }

                    progress?.Report(new ArchiveProgressInfo(
                        "正在压缩",
                        item.EntryPath,
                        processedBytes,
                        totalBytes));
                    using var inputStream = new FileStream(
                        item.SourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.SequentialScan);
                    using var monitoredStream = new ProgressReadStream(
                        inputStream,
                        cancellationToken,
                        bytesRead =>
                        {
                            processedBytes += bytesRead;
                            progress?.Report(new ArchiveProgressInfo(
                                "正在压缩",
                                item.EntryPath,
                                processedBytes,
                                totalBytes));
                        });
                    writer.Write(item.EntryPath, monitoredStream, item.LastModifiedTime);
                    processedFiles++;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (format == ArchiveOutputFormat.Zip)
            {
                EnsureZipUsesUtf8EntryNames(temporaryPath);
            }

            File.Move(temporaryPath, destinationPath, true);
            progress?.Report(new ArchiveProgressInfo("压缩完成", string.Empty, totalBytes, totalBytes));
            return new ArchiveOperationResult(processedFiles, 0, processedBytes, destinationPath);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static void EnsureZipUsesUtf8EntryNames(string archivePath)
    {
        const uint endOfCentralDirectorySignature = 0x06054B50;
        const uint zip64EndOfCentralDirectorySignature = 0x06064B50;
        const uint zip64LocatorSignature = 0x07064B50;
        const uint centralDirectoryHeaderSignature = 0x02014B50;
        const uint localHeaderSignature = 0x04034B50;
        const ushort utf8Flag = 0x0800;

        using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.RandomAccess);

        var searchLength = (int)Math.Min(stream.Length, ushort.MaxValue + 22L);
        var searchBuffer = new byte[searchLength];
        stream.Position = stream.Length - searchLength;
        stream.ReadExactly(searchBuffer);

        var endOffsetInBuffer = -1;
        for (var index = searchBuffer.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(searchBuffer.AsSpan(index, 4)) ==
                endOfCentralDirectorySignature)
            {
                endOffsetInBuffer = index;
                break;
            }
        }

        if (endOffsetInBuffer < 0)
        {
            throw new InvalidDataException("ZIP 中央目录结尾无效，无法写入 UTF-8 文件名标志。");
        }

        var endOffset = stream.Length - searchLength + endOffsetInBuffer;
        var endRecord = searchBuffer.AsSpan(endOffsetInBuffer, 22);
        ulong entryCount = BinaryPrimitives.ReadUInt16LittleEndian(endRecord.Slice(10, 2));
        ulong centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(endRecord.Slice(16, 4));

        if (entryCount == ushort.MaxValue || centralDirectoryOffset == uint.MaxValue)
        {
            if (endOffset < 20)
            {
                throw new InvalidDataException("ZIP64 定位记录缺失，无法写入 UTF-8 文件名标志。");
            }

            var locator = ReadBytesAt(stream, endOffset - 20, 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != zip64LocatorSignature)
            {
                throw new InvalidDataException("ZIP64 定位记录无效，无法写入 UTF-8 文件名标志。");
            }

            var zip64EndOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8, 8)));
            var zip64EndRecord = ReadBytesAt(stream, zip64EndOffset, 56);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64EndRecord) != zip64EndOfCentralDirectorySignature)
            {
                throw new InvalidDataException("ZIP64 中央目录结尾无效，无法写入 UTF-8 文件名标志。");
            }

            entryCount = BinaryPrimitives.ReadUInt64LittleEndian(zip64EndRecord.AsSpan(32, 8));
            centralDirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64EndRecord.AsSpan(48, 8));
        }

        var centralPosition = checked((long)centralDirectoryOffset);
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        for (ulong entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            var header = ReadBytesAt(stream, centralPosition, 46);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != centralDirectoryHeaderSignature)
            {
                throw new InvalidDataException("ZIP 中央目录条目无效，无法写入 UTF-8 文件名标志。");
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
            var nameBytes = ReadBytesAt(stream, centralPosition + 46, nameLength);
            _ = strictUtf8.GetString(nameBytes);

            var centralFlags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
            WriteUInt16At(stream, centralPosition + 8, (ushort)(centralFlags | utf8Flag));

            var localHeaderOffset = ResolveLocalHeaderOffset(
                stream,
                centralPosition,
                header,
                nameLength,
                extraLength);
            var localHeader = ReadBytesAt(stream, localHeaderOffset, 30);
            if (BinaryPrimitives.ReadUInt32LittleEndian(localHeader) != localHeaderSignature)
            {
                throw new InvalidDataException("ZIP 本地条目头无效，无法写入 UTF-8 文件名标志。");
            }

            var localFlags = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(6, 2));
            WriteUInt16At(stream, localHeaderOffset + 6, (ushort)(localFlags | utf8Flag));
            centralPosition += 46L + nameLength + extraLength + commentLength;
        }
    }

    private static long ResolveLocalHeaderOffset(
        FileStream stream,
        long centralPosition,
        byte[] header,
        ushort nameLength,
        ushort extraLength)
    {
        var localOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(42, 4));
        if (localOffset32 != uint.MaxValue)
        {
            return localOffset32;
        }

        var extra = ReadBytesAt(stream, centralPosition + 46L + nameLength, extraLength);
        var cursor = 0;
        while (cursor + 4 <= extra.Length)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(cursor, 2));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(cursor + 2, 2));
            cursor += 4;
            if (cursor + length > extra.Length)
            {
                break;
            }

            if (tag == 0x0001)
            {
                var valueOffset = cursor;
                if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4)) == uint.MaxValue)
                    valueOffset += 8;
                if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4)) == uint.MaxValue)
                    valueOffset += 8;
                if (valueOffset + 8 <= cursor + length)
                {
                    return checked((long)BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(valueOffset, 8)));
                }
            }

            cursor += length;
        }

        throw new InvalidDataException("ZIP64 本地条目偏移缺失，无法写入 UTF-8 文件名标志。");
    }

    private static byte[] ReadBytesAt(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count)
        {
            throw new InvalidDataException("ZIP 结构超出文件边界。");
        }

        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void WriteUInt16At(FileStream stream, long offset, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Position = offset;
        stream.Write(buffer);
    }

    private static ArchiveOperationResult ExtractCore(
        string archivePath,
        string destinationDirectory,
        string? password,
        ArchiveConflictPolicy conflictPolicy,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        ValidateArchivePath(archivePath);
        var inspection = InspectCore(archivePath, password, cancellationToken);
        if (inspection.IsEncrypted && string.IsNullOrEmpty(password))
        {
            throw new InvalidDataException("该压缩包包含加密内容，请输入密码后重试。");
        }

        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        ArchiveSafety.EnsureSufficientFreeSpace(destinationRoot, inspection.TotalUncompressedBytes);
        var options = CreateReaderOptions(password);
        var archiveInformation = ArchiveFactory.GetArchiveInformation(archivePath, options);
        var state = new ExtractionState(
            archivePath,
            destinationRoot,
            conflictPolicy,
            inspection.TotalUncompressedBytes,
            progress,
            cancellationToken);

        if (archiveInformation?.SupportsRandomAccess == true)
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, options);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = entry.IsDirectory ? null : entry.OpenEntryStream();
                ExtractEntry(entry, stream, state);
            }
        }
        else
        {
            using var reader = ReaderFactory.OpenReader(archivePath, options);
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = reader.Entry.IsDirectory ? null : reader.OpenEntryStream();
                ExtractEntry(reader.Entry, stream, state);
            }
        }

        progress?.Report(new ArchiveProgressInfo(
            "解压完成",
            string.Empty,
            state.ProcessedBytes,
            state.TotalBytes));
        return new ArchiveOperationResult(
            state.ProcessedFiles,
            state.SkippedFiles,
            state.ProcessedBytes,
            destinationRoot);
    }

    private static void ExtractEntry(IEntry entry, Stream? entryStream, ExtractionState state)
    {
        var entryPath = entry.Key
            ?? throw new InvalidDataException("压缩包包含未命名条目，已停止解压。");
        ArchiveSafety.ValidateEntryMetadata(entryPath, entry.Size, entry.LinkTarget);
        var targetPath = ArchiveSafety.ResolveSafeDestination(state.DestinationRoot, entryPath);
        ArchiveSafety.EnsureNoReparsePointBetween(state.DestinationRoot, targetPath);

        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(targetPath);
            return;
        }

        if (entryStream is null)
        {
            throw new InvalidDataException($"无法读取压缩条目：{entryPath}");
        }

        var resolvedTarget = ResolveFileConflict(targetPath, state.ConflictPolicy);
        if (resolvedTarget is null)
        {
            state.SkippedFiles++;
            state.ProcessedBytes = checked(state.ProcessedBytes + entry.Size);
            state.Progress?.Report(new ArchiveProgressInfo(
                "已跳过同名文件",
                entryPath,
                state.ProcessedBytes,
                state.TotalBytes));
            return;
        }

        var parentDirectory = Path.GetDirectoryName(resolvedTarget)
            ?? throw new InvalidDataException($"无法确定条目目标目录：{entryPath}");
        Directory.CreateDirectory(parentDirectory);
        ArchiveSafety.EnsureNoReparsePointBetween(state.DestinationRoot, resolvedTarget);
        var temporaryPath = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(resolvedTarget)}.{Guid.NewGuid():N}.xtool-partial");

        state.Progress?.Report(new ArchiveProgressInfo(
            "正在解压",
            entryPath,
            state.ProcessedBytes,
            state.TotalBytes));
        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var read = entryStream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                    state.ProcessedBytes = checked(state.ProcessedBytes + read);
                    if (state.ProcessedBytes > state.MaximumAllowedBytes)
                    {
                        throw new InvalidDataException("解压后的数据量超出安全阈值，任务已停止。");
                    }

                    state.Progress?.Report(new ArchiveProgressInfo(
                        "正在解压",
                        entryPath,
                        state.ProcessedBytes,
                        state.TotalBytes));
                }
            }

            File.Move(temporaryPath, resolvedTarget, state.ConflictPolicy == ArchiveConflictPolicy.Overwrite);
            if (entry.LastModifiedTime is { } modifiedTime)
            {
                File.SetLastWriteTime(resolvedTarget, modifiedTime);
            }

            state.ProcessedFiles++;
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static List<ArchiveInputEntry> EnumerateInputEntries(
        IEnumerable<string> sourcePaths,
        bool includeTopLevelDirectory,
        string destinationArchive,
        CancellationToken cancellationToken)
    {
        var entries = new List<ArchiveInputEntry>();
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourcePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(source))
            {
                if (string.Equals(source, destinationArchive, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("输出压缩包不能与输入文件相同。");
                }

                AddInputEntry(
                    entries,
                    entryNames,
                    new ArchiveInputEntry(
                        source,
                        NormalizeArchivePath(Path.GetFileName(source)),
                        false,
                        new FileInfo(source).Length,
                        File.GetLastWriteTime(source)));
                continue;
            }

            if (!Directory.Exists(source))
            {
                throw new FileNotFoundException("找不到需要压缩的文件或文件夹。", source);
            }

            var rootDirectory = new DirectoryInfo(source);
            var prefix = includeTopLevelDirectory ? rootDirectory.Name : string.Empty;
            var directories = Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).ToArray();
            if (includeTopLevelDirectory || !directories.Any() && !Directory.EnumerateFiles(source).Any())
            {
                var rootEntry = string.IsNullOrWhiteSpace(prefix) ? rootDirectory.Name : prefix;
                AddInputEntry(
                    entries,
                    entryNames,
                    new ArchiveInputEntry(
                        source,
                        NormalizeArchivePath(rootEntry) + "/",
                        true,
                        0,
                        rootDirectory.LastWriteTime));
            }

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, directory);
                var entryName = CombineArchivePath(prefix, relative) + "/";
                AddInputEntry(
                    entries,
                    entryNames,
                    new ArchiveInputEntry(
                        directory,
                        entryName,
                        true,
                        0,
                        Directory.GetLastWriteTime(directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(file);
                if (string.Equals(fullPath, destinationArchive, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new FileInfo(fullPath);
                AddInputEntry(
                    entries,
                    entryNames,
                    new ArchiveInputEntry(
                        fullPath,
                        CombineArchivePath(prefix, Path.GetRelativePath(source, fullPath)),
                        false,
                        info.Length,
                        info.LastWriteTime));
            }
        }

        return entries;
    }

    private static void AddInputEntry(
        ICollection<ArchiveInputEntry> entries,
        ISet<string> entryNames,
        ArchiveInputEntry item)
    {
        if (entries.Count >= ArchiveSafety.MaximumEntryCount)
        {
            throw new InvalidOperationException(
                $"待压缩内容超过 {ArchiveSafety.MaximumEntryCount:N0} 个条目，请拆分为多个任务。");
        }

        if (!item.IsDirectory && item.Size > ArchiveSafety.MaximumSingleEntryBytes)
        {
            throw new InvalidOperationException(
                $"单个文件超过当前安全限制：{item.SourcePath}");
        }

        if (!entryNames.Add(item.EntryPath))
        {
            throw new InvalidOperationException($"多个来源会生成同名条目：{item.EntryPath}");
        }

        entries.Add(item);
    }

    private static string CombineArchivePath(string prefix, string relativePath)
    {
        return string.IsNullOrWhiteSpace(prefix)
            ? NormalizeArchivePath(relativePath)
            : $"{NormalizeArchivePath(prefix)}/{NormalizeArchivePath(relativePath)}";
    }

    private static string NormalizeArchivePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static (ArchiveType ArchiveType, CompressionType CompressionType) ResolveWriterFormat(
        ArchiveOutputFormat format,
        ArchiveCompressionPreset preset)
    {
        return format switch
        {
            ArchiveOutputFormat.SevenZip => (ArchiveType.SevenZip, CompressionType.LZMA2),
            ArchiveOutputFormat.TarGZip => (ArchiveType.Tar, CompressionType.GZip),
            _ => (ArchiveType.Zip,
                preset == ArchiveCompressionPreset.Store ? CompressionType.None : CompressionType.Deflate)
        };
    }

    private static int ResolveCompressionLevel(ArchiveCompressionPreset preset)
    {
        return preset switch
        {
            ArchiveCompressionPreset.Store => 0,
            ArchiveCompressionPreset.Fast => 3,
            ArchiveCompressionPreset.Smallest => 9,
            _ => 6
        };
    }

    private static ReaderOptions CreateReaderOptions(string? password)
    {
        return string.IsNullOrEmpty(password)
            ? ReaderOptions.ForFilePath
            : ReaderOptions.ForEncryptedArchive(password) with { LeaveStreamOpen = false };
    }

    private static void ValidateArchiveTotals(string archivePath, int entryCount, long totalBytes)
    {
        if (entryCount > ArchiveSafety.MaximumEntryCount)
        {
            throw new InvalidDataException($"压缩包条目超过 {ArchiveSafety.MaximumEntryCount:N0} 个，已停止处理。");
        }

        var compressedBytes = new FileInfo(archivePath).Length;
        var maximumAllowed = ArchiveSafety.GetMaximumAllowedExpandedBytes(compressedBytes);
        if (totalBytes > maximumAllowed)
        {
            throw new InvalidDataException(
                $"压缩包预计展开为 {ArchiveSizeFormatter.Format(totalBytes)}，超出当前安全阈值 {ArchiveSizeFormatter.Format(maximumAllowed)}。");
        }
    }

    private static string? ResolveFileConflict(string targetPath, ArchiveConflictPolicy policy)
    {
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            return targetPath;
        }

        if (policy == ArchiveConflictPolicy.Skip)
        {
            return null;
        }

        if (policy == ArchiveConflictPolicy.Overwrite)
        {
            if (Directory.Exists(targetPath))
            {
                throw new IOException($"目标位置存在同名文件夹，无法覆盖：{targetPath}");
            }

            return targetPath;
        }

        var directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var extension = Path.GetExtension(targetPath);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为同名文件生成可用名称：{targetPath}");
    }

    private static string GetFormatDisplayName(ArchiveType? type)
    {
        return type switch
        {
            ArchiveType.SevenZip => "7z",
            ArchiveType.Rar => "RAR",
            ArchiveType.Tar => "TAR",
            ArchiveType.GZip => "GZip / TAR.GZ",
            ArchiveType.Arc => "ARC",
            ArchiveType.Arj => "ARJ",
            ArchiveType.Ace => "ACE",
            ArchiveType.Lzw => "LZW",
            _ => "ZIP"
        };
    }

    private static void ValidateArchivePath(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            throw new FileNotFoundException("找不到要打开的压缩包。", archivePath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"清理压缩任务临时文件失败：{exception.Message}");
        }
    }

    private sealed record ArchiveInputEntry(
        string SourcePath,
        string EntryPath,
        bool IsDirectory,
        long Size,
        DateTime? LastModifiedTime);

    private sealed class ExtractionState(
        string archivePath,
        string destinationRoot,
        ArchiveConflictPolicy conflictPolicy,
        long totalBytes,
        IProgress<ArchiveProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        public string DestinationRoot { get; } = destinationRoot;
        public ArchiveConflictPolicy ConflictPolicy { get; } = conflictPolicy;
        public long TotalBytes { get; } = totalBytes;
        public long MaximumAllowedBytes { get; } = ArchiveSafety.GetMaximumAllowedExpandedBytes(new FileInfo(archivePath).Length);
        public IProgress<ArchiveProgressInfo>? Progress { get; } = progress;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public int ProcessedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public long ProcessedBytes { get; set; }
    }

    private sealed class ProgressReadStream(
        Stream inner,
        CancellationToken cancellationToken,
        Action<int> progress) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                progress(read);
            }

            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer);
            if (read > 0)
            {
                progress(read);
            }

            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
