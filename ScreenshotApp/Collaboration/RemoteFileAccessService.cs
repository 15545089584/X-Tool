using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotApp.Collaboration;

public sealed record RemoteFileRootInfo(string Id, string Name, string Path);

internal sealed record RemoteFileRootDescriptor(string Id, string Name, string Token);

internal sealed record RemoteFileEntryDescriptor(
    string Name,
    bool IsDirectory,
    long Size,
    DateTime ModifiedAt,
    string Token,
    string Extension);

internal sealed record RemoteFileDirectoryPage(
    string CurrentName,
    string? ParentToken,
    IReadOnlyList<RemoteFileEntryDescriptor> Entries,
    int NextOffset);

internal sealed class RemoteFileDownload : IAsyncDisposable
{
    internal RemoteFileDownload(FileStream stream, string name, long length)
    {
        Stream = stream;
        Name = name;
        Length = length;
    }

    internal FileStream Stream { get; }
    internal string Name { get; }
    internal long Length { get; }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// 管理手机可见的只读目录。客户端只能使用由本机签发的短期加密令牌，
/// 不接收原始绝对路径，也不会跟随目录联接、符号链接或其他重解析点。
/// </summary>
public sealed class RemoteFileAccessService
{
    private const int MaximumPageSize = 200;
    private static readonly TimeSpan DirectoryTokenLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan FileTokenLifetime = TimeSpan.FromMinutes(30);
    private static readonly string ConfigurationPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool", "Collaboration", "remote-file-access.json");

    private readonly object _sync = new();
    private RemoteFileAccessConfiguration _configuration;
    private byte[] _tokenKey;

    private RemoteFileAccessService()
    {
        (_configuration, _tokenKey) = LoadConfiguration();
    }

    public static RemoteFileAccessService Instance { get; } = new();

    public event Action? Changed;

    public bool Enabled
    {
        get
        {
            lock (_sync) return _configuration.Enabled;
        }
        set
        {
            lock (_sync)
            {
                if (_configuration.Enabled == value) return;
                _configuration = _configuration with { Enabled = value };
                SaveConfiguration();
            }
            Changed?.Invoke();
        }
    }

    public IReadOnlyList<RemoteFileRootInfo> Roots
    {
        get
        {
            lock (_sync)
            {
                return _configuration.Roots
                    .Select(root => new RemoteFileRootInfo(root.Id, root.Name, root.Path))
                    .ToArray();
            }
        }
    }

    public RemoteFileRootInfo AddRoot(string path)
    {
        var normalizedPath = NormalizeRootPath(path);
        var attributes = File.GetAttributes(normalizedPath);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidOperationException("只能授权文件夹。");
        }
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("不能授权目录联接、符号链接或其他重解析目录。");
        }

        RemoteFileRootInfo result;
        lock (_sync)
        {
            var existing = _configuration.Roots.FirstOrDefault(root =>
                string.Equals(root.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return new RemoteFileRootInfo(existing.Id, existing.Name, existing.Path);
            }

            var displayName = new DirectoryInfo(normalizedPath).Name;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = normalizedPath.TrimEnd(Path.DirectorySeparatorChar);
            }
            var root = new RemoteFileRootRecord(Guid.NewGuid().ToString("N"), displayName, normalizedPath);
            _configuration = _configuration with { Roots = [.. _configuration.Roots, root] };
            SaveConfiguration();
            result = new RemoteFileRootInfo(root.Id, root.Name, root.Path);
        }
        Changed?.Invoke();
        return result;
    }

    public bool RemoveRoot(string id)
    {
        bool removed;
        lock (_sync)
        {
            var roots = _configuration.Roots.Where(root => root.Id != id).ToArray();
            removed = roots.Length != _configuration.Roots.Length;
            if (!removed) return false;
            _configuration = _configuration with { Roots = roots };
            SaveConfiguration();
        }
        Changed?.Invoke();
        return true;
    }

    internal IReadOnlyList<RemoteFileRootDescriptor> GetRootDescriptors()
    {
        lock (_sync)
        {
            if (!_configuration.Enabled) return [];
            var result = new List<RemoteFileRootDescriptor>();
            foreach (var root in _configuration.Roots)
            {
                try
                {
                    if (!Directory.Exists(root.Path) || IsReparsePoint(root.Path)) continue;
                    result.Add(new RemoteFileRootDescriptor(
                        root.Id,
                        root.Name,
                        CreateToken(root, string.Empty, isDirectory: true, 0, Directory.GetLastWriteTimeUtc(root.Path))));
                }
                catch
                {
                    // 单个授权目录暂时离线或无权限时不影响其它目录。
                }
            }
            return result;
        }
    }

    internal bool TryListDirectory(string token, int offset, int requestedPageSize,
        out RemoteFileDirectoryPage? page, out string error)
    {
        page = null;
        error = string.Empty;
        if (!TryResolveToken(token, requireDirectory: true, out var resolved, out error) || resolved is null)
        {
            return false;
        }

        try
        {
            var pageSize = Math.Clamp(requestedPageSize, 1, MaximumPageSize);
            var safeOffset = Math.Max(0, offset);
            var entries = new List<RemoteFileEntryDescriptor>();
            foreach (var path in Directory.EnumerateFileSystemEntries(resolved.FullPath))
            {
                try
                {
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.System))
                    {
                        continue;
                    }
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var info = isDirectory ? null : new FileInfo(path);
                    var relativePath = Path.GetRelativePath(resolved.Root.Path, path);
                    var modifiedAt = isDirectory ? Directory.GetLastWriteTimeUtc(path) : info!.LastWriteTimeUtc;
                    var size = isDirectory ? 0 : info!.Length;
                    entries.Add(new RemoteFileEntryDescriptor(
                        Path.GetFileName(path),
                        isDirectory,
                        size,
                        modifiedAt,
                        CreateToken(resolved.Root, relativePath, isDirectory, size, modifiedAt),
                        isDirectory ? string.Empty : Path.GetExtension(path)));
                }
                catch
                {
                    // 单个不可读项目不应让整个目录浏览失败。
                }
            }

            var ordered = entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var visible = ordered.Skip(safeOffset).Take(pageSize).ToArray();
            var nextOffset = safeOffset + visible.Length < ordered.Length ? safeOffset + visible.Length : -1;
            var parentToken = string.IsNullOrEmpty(resolved.RelativePath)
                ? null
                : CreateParentToken(resolved.Root, resolved.RelativePath);
            page = new RemoteFileDirectoryPage(
                string.IsNullOrEmpty(resolved.RelativePath) ? resolved.Root.Name : Path.GetFileName(resolved.FullPath),
                parentToken,
                visible,
                nextOffset);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "当前 Windows 用户没有读取此目录的权限。";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            error = "目录已被移动或删除。";
            return false;
        }
        catch
        {
            error = "无法读取远程目录。";
            return false;
        }
    }

    internal bool TryOpenDownload(string token, out RemoteFileDownload? download, out string error)
    {
        download = null;
        error = string.Empty;
        if (!TryResolveToken(token, requireDirectory: false, out var resolved, out error) || resolved is null)
        {
            return false;
        }
        if (resolved.Payload.IsDirectory)
        {
            error = "文件夹不能直接下载。";
            return false;
        }

        try
        {
            var info = new FileInfo(resolved.FullPath);
            if (!info.Exists || info.Length != resolved.Payload.Length ||
                info.LastWriteTimeUtc.Ticks != resolved.Payload.ModifiedUtcTicks)
            {
                error = "文件已发生变化，请刷新目录后重试。";
                return false;
            }
            var stream = new FileStream(resolved.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            download = new RemoteFileDownload(stream, info.Name, stream.Length);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "当前 Windows 用户没有读取此文件的权限。";
            return false;
        }
        catch
        {
            error = "文件无法打开或已被其他程序独占。";
            return false;
        }
    }

    private string CreateParentToken(RemoteFileRootRecord root, string relativePath)
    {
        var parent = Path.GetDirectoryName(relativePath) ?? string.Empty;
        var fullPath = CombineInsideRoot(root.Path, parent);
        return CreateToken(root, parent, true, 0, Directory.GetLastWriteTimeUtc(fullPath));
    }

    private string CreateToken(RemoteFileRootRecord root, string relativePath, bool isDirectory, long length,
        DateTime modifiedAt)
    {
        var expiresAt = DateTime.UtcNow.Add(isDirectory ? DirectoryTokenLifetime : FileTokenLifetime);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new RemoteFileTokenPayload(
            root.Id,
            relativePath,
            isDirectory,
            length,
            modifiedAt.Ticks,
            expiresAt.Ticks));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[payload.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_tokenKey, tag.Length);
        aes.Encrypt(nonce, payload, cipher, tag);
        var combined = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, combined, nonce.Length + tag.Length, cipher.Length);
        return ToBase64Url(combined);
    }

    private bool TryResolveToken(string token, bool requireDirectory, out ResolvedRemotePath? resolved, out string error)
    {
        resolved = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
        {
            error = "目录令牌无效。";
            return false;
        }

        RemoteFileTokenPayload? payload;
        try
        {
            var combined = FromBase64Url(token);
            if (combined.Length < 29) throw new CryptographicException();
            var nonce = combined.AsSpan(0, 12);
            var tag = combined.AsSpan(12, 16);
            var cipher = combined.AsSpan(28);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_tokenKey, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain);
            payload = JsonSerializer.Deserialize<RemoteFileTokenPayload>(plain);
        }
        catch
        {
            error = "目录令牌无效或已经损坏。";
            return false;
        }

        if (payload is null || payload.ExpiresUtcTicks <= DateTime.UtcNow.Ticks ||
            (requireDirectory && !payload.IsDirectory))
        {
            error = "目录令牌已经过期，请刷新后重试。";
            return false;
        }

        RemoteFileRootRecord? root;
        lock (_sync)
        {
            if (!_configuration.Enabled)
            {
                error = "电脑端未开启远程文件访问。";
                return false;
            }
            root = _configuration.Roots.FirstOrDefault(candidate => candidate.Id == payload.RootId);
        }
        if (root is null || !Directory.Exists(root.Path) || IsReparsePoint(root.Path))
        {
            error = "授权目录已经不可用。";
            return false;
        }

        try
        {
            var fullPath = CombineInsideRoot(root.Path, payload.RelativePath);
            if (!EnsurePathContainsNoReparsePoint(root.Path, payload.RelativePath))
            {
                error = "目标路径经过目录联接或符号链接，已拒绝访问。";
                return false;
            }
            if (payload.IsDirectory ? !Directory.Exists(fullPath) : !File.Exists(fullPath))
            {
                error = "目标已经移动或删除。";
                return false;
            }
            resolved = new ResolvedRemotePath(root, payload.RelativePath, fullPath, payload);
            return true;
        }
        catch
        {
            error = "目标路径超出授权范围。";
            return false;
        }
    }

    private static string NormalizeRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("目录不能为空。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("所选目录不存在。");
        return fullPath;
    }

    private static string CombineInsideRoot(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("不接受绝对路径。");
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("路径超出授权目录。");
        }
        return candidate;
    }

    private static bool EnsurePathContainsNoReparsePoint(string rootPath, string relativePath)
    {
        var current = rootPath;
        if (IsReparsePoint(current)) return false;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current)) return false;
        }
        return true;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return true; }
    }

    private static (RemoteFileAccessConfiguration Configuration, byte[] Key) LoadConfiguration()
    {
        try
        {
            if (File.Exists(ConfigurationPath))
            {
                var configuration = JsonSerializer.Deserialize<RemoteFileAccessConfiguration>(
                    File.ReadAllText(ConfigurationPath, Encoding.UTF8));
                if (configuration is not null)
                {
                    var key = Convert.FromBase64String(configuration.TokenKey);
                    if (key.Length == 32)
                    {
                        var roots = configuration.Roots
                            .Where(root => !string.IsNullOrWhiteSpace(root.Id) && !string.IsNullOrWhiteSpace(root.Path))
                            .ToArray();
                        return (configuration with { Roots = roots }, key);
                    }
                }
            }
        }
        catch
        {
            // 配置损坏时以关闭状态重新创建，绝不猜测可共享目录。
        }

        var generatedKey = RandomNumberGenerator.GetBytes(32);
        var fresh = new RemoteFileAccessConfiguration(false, Convert.ToBase64String(generatedKey), []);
        return (fresh, generatedKey);
    }

    private void SaveConfiguration()
    {
        var directory = Path.GetDirectoryName(ConfigurationPath)!;
        Directory.CreateDirectory(directory);
        var temporary = ConfigurationPath + ".tmp";
        var persisted = _configuration with { TokenKey = Convert.ToBase64String(_tokenKey) };
        File.WriteAllText(temporary, JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temporary, ConfigurationPath, overwrite: true);
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record RemoteFileRootRecord(string Id, string Name, string Path);
    private sealed record RemoteFileAccessConfiguration(bool Enabled, string TokenKey, RemoteFileRootRecord[] Roots);
    private sealed record RemoteFileTokenPayload(
        string RootId,
        string RelativePath,
        bool IsDirectory,
        long Length,
        long ModifiedUtcTicks,
        long ExpiresUtcTicks);
    private sealed record ResolvedRemotePath(
        RemoteFileRootRecord Root,
        string RelativePath,
        string FullPath,
        RemoteFileTokenPayload Payload);
}
