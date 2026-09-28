using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScreenshotApp.FileWorkbench;

/// <summary>
/// 通过 Windows Search 的公开只读 OLE DB 提供器查询系统索引。
/// 该服务不建立 X-Tool 自己的常驻索引，失败或目录未覆盖时由调用方回退到递归扫描。
/// </summary>
internal static class WindowsSearchFileSearchBackend
{
    private const string ConnectionString = "Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows';";
    private const int CommandTimeoutSeconds = 8;

    internal static WindowsSearchSearchAttempt TrySearch(
        string rootDirectory,
        string keyword,
        string typeFilter,
        DateTime? modifiedAfter,
        IReadOnlyList<FileSortDescriptor> sortDescriptors,
        IProgress<FileSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(FileSearchProgress.ForStatus("正在查询 Windows Search 系统索引…", rootDirectory));

        try
        {
            var scope = ToScope(rootDirectory);
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            cancellationToken.ThrowIfCancellationRequested();

            using var command = new OleDbCommand(BuildSearchQuery(scope, keyword, typeFilter, modifiedAfter, sortDescriptors), connection)
            {
                CommandTimeout = CommandTimeoutSeconds
            };
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    command.Cancel();
                }
                catch (InvalidOperationException)
                {
                    // 查询已结束时无需额外处理。
                }
            });
            using var reader = command.ExecuteReader();
            var searchResult = ReadResult(reader, keyword, typeFilter, modifiedAfter, sortDescriptors, cancellationToken);

            if (searchResult.RawResultCount == 0 && !ScopeHasIndexedItem(connection, scope, cancellationToken))
            {
                return WindowsSearchSearchAttempt.Fallback("Windows Search 未覆盖此目录");
            }

            if (searchResult.RawResultCount > 0 && searchResult.Result.Items.Count == 0)
            {
                return WindowsSearchSearchAttempt.Fallback("Windows Search 返回的索引项当前无法直接读取");
            }

            progress?.Report(FileSearchProgress.ForStatus("Windows Search 已返回索引结果", rootDirectory));
            return WindowsSearchSearchAttempt.Success(searchResult.Result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is OleDbException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return WindowsSearchSearchAttempt.Fallback("Windows Search 服务不可用或索引查询失败");
        }
    }

    private static WindowsSearchReadResult ReadResult(
        OleDbDataReader reader,
        string keyword,
        string typeFilter,
        DateTime? modifiedAfter,
        IReadOnlyList<FileSortDescriptor> sortDescriptors,
        CancellationToken cancellationToken)
    {
        var items = new List<FileWorkbenchItem>(FileWorkbenchService.MaxDisplayResultCount);
        var rawResultCount = 0;
        var hasMoreResults = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rawResultCount++;
            if (rawResultCount > FileWorkbenchService.MaxDisplayResultCount)
            {
                hasMoreResults = true;
                break;
            }

            if (reader.IsDBNull(0))
            {
                continue;
            }

            if (!TryGetFileSystemPath(reader.GetString(0), out var path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (!Matches(info, keyword, typeFilter, modifiedAfter))
                {
                    continue;
                }

                items.Add(FileWorkbenchItem.Create(info));
            }
            catch (UnauthorizedAccessException)
            {
                // Windows Search 的旧索引项可能已无读取权限，跳过并保留其他结果。
            }
            catch (IOException)
            {
                // 索引与文件系统存在短暂不同步时，跳过已移动或被占用的文件。
            }
        }

        var orderedItems = FileWorkbenchService.SortResults(items, sortDescriptors);
        var matchedFiles = hasMoreResults
            ? FileWorkbenchService.MaxDisplayResultCount + 1L
            : orderedItems.Count;
        var result = new FileSearchResult(
            orderedItems,
            rawResultCount,
            matchedFiles,
            FileSearchBackend.WindowsSearch,
            null,
            !hasMoreResults);
        return new WindowsSearchReadResult(result, rawResultCount);
    }

    private static bool ScopeHasIndexedItem(OleDbConnection connection, string scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = new OleDbCommand(
            $"SELECT TOP 1 System.ItemUrl FROM SYSTEMINDEX WHERE SCOPE='{EscapeLiteral(scope)}'",
            connection)
        {
            CommandTimeout = CommandTimeoutSeconds
        };
        using var reader = command.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        return reader.Read();
    }

    /// <summary>
    /// System.ItemPathDisplay 会按系统语言本地化（例如 C:\\用户），不能直接传给 FileInfo。
    /// ItemUrl 保留真实 NTFS 路径，使用 Uri.LocalPath 可同时兼容本地盘与 UNC 路径。
    /// </summary>
    private static bool TryGetFileSystemPath(string itemUrl, out string path)
    {
        if (Uri.TryCreate(itemUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
            return !string.IsNullOrWhiteSpace(path);
        }

        path = string.Empty;
        return false;
    }

    private static string BuildSearchQuery(
        string scope,
        string keyword,
        string typeFilter,
        DateTime? modifiedAfter,
        IReadOnlyList<FileSortDescriptor> sortDescriptors)
    {
        var conditions = new List<string> { $"SCOPE='{EscapeLiteral(scope)}'" };
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            conditions.Add($"System.FileName LIKE '%{EscapeLikePattern(keyword)}%'");
        }

        var extensionPredicate = BuildExtensionPredicate(typeFilter);
        if (!string.IsNullOrWhiteSpace(extensionPredicate))
        {
            conditions.Add(extensionPredicate);
        }

        if (modifiedAfter is not null)
        {
            conditions.Add($"System.DateModified >= '{modifiedAfter.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}'");
        }

        var effectiveSortDescriptors = sortDescriptors.Count == 0
            ? new[] { new FileSortDescriptor(FileSortField.Name, true) }
            : sortDescriptors;
        var orderBy = string.Join(", ", effectiveSortDescriptors
            .Select(descriptor => $"{GetSearchProperty(descriptor.Field)} {(descriptor.Ascending ? "ASC" : "DESC")}"));
        return $"SELECT TOP {FileWorkbenchService.MaxDisplayResultCount + 1} System.ItemUrl FROM SYSTEMINDEX WHERE {string.Join(" AND ", conditions)} ORDER BY {orderBy}, System.ItemUrl ASC";
    }

    private static string BuildExtensionPredicate(string typeFilter)
    {
        var extensions = typeFilter switch
        {
            "图片" => new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff" },
            "视频" => new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".flv", ".wmv" },
            "音频" => new[] { ".mp3", ".wav", ".m4a", ".flac", ".aac", ".ogg", ".opus" },
            "文档" => new[] { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md" },
            "压缩包" => new[] { ".zip", ".rar", ".7z", ".tar", ".gz" },
            _ => Array.Empty<string>()
        };
        if (extensions.Length == 0)
        {
            return string.Empty;
        }

        return $"({string.Join(" OR ", extensions.Select(extension => $"System.FileExtension = '{extension}'"))})";
    }

    private static bool Matches(FileInfo info, string keyword, string typeFilter, DateTime? modifiedAfter)
    {
        if (!string.IsNullOrWhiteSpace(keyword) && !info.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(typeFilter, "全部", StringComparison.Ordinal) &&
            !string.Equals(FileWorkbenchService.GetCategory(info.Extension), typeFilter, StringComparison.Ordinal))
        {
            return false;
        }

        return modifiedAfter is null || info.LastWriteTime >= modifiedAfter.Value;
    }

    private static string GetSearchProperty(FileSortField field) => field switch
    {
        FileSortField.Name => "System.FileName",
        FileSortField.Extension => "System.FileExtension",
        FileSortField.Size => "System.Size",
        FileSortField.Modified => "System.DateModified",
        _ => "System.FileName"
    };

    private static string ToScope(string rootDirectory) => $"file:{Path.GetFullPath(rootDirectory).Replace('\\', '/')}";

    private static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Windows Search 的 LIKE 参数不支持绑定，需将用户输入转为字面量模式。</summary>
    private static string EscapeLikePattern(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            escaped.Append(character switch
            {
                '%' => "[%]",
                '_' => "[_]",
                '[' => "[[]",
                ']' => "[]]",
                '\'' => "''",
                _ => character.ToString()
            });
        }

        return escaped.ToString();
    }
}

/// <summary>Windows Search 的查询结果或递归扫描所需的降级原因。</summary>
internal sealed record WindowsSearchSearchAttempt(FileSearchResult? Result, string? FallbackReason)
{
    internal static WindowsSearchSearchAttempt Success(FileSearchResult result) => new(result, null);

    internal static WindowsSearchSearchAttempt Fallback(string reason) => new(null, reason);
}

internal sealed record WindowsSearchReadResult(FileSearchResult Result, int RawResultCount);
