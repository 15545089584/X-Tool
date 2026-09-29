using System.IO;
using System.Security.Cryptography;
using System.Text;
using MimeKit;

namespace ScreenshotApp.Mail;

public sealed record MailStorageUsage(string Path, long Bytes, long AccountBytes, int Messages, string Error);

/// <summary>完整 MIME 仅以当前 Windows 用户可解密的形式落盘，不自动淘汰。</summary>
public sealed class MailDiskCache(string root, string? legacyRoot = null)
{
    public string Root { get; } = Path.GetFullPath(root);
    private string AccountPath(string id) => Path.Combine(Root, "messages", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))));
    internal string MessagePath(MailRow row) => Path.Combine(AccountPath(row.AccountId), $"{row.Validity}-{row.Uid}.bin");
    private string? LegacyPath(MailRow row) => legacyRoot is null ? null : Path.Combine(legacyRoot, Path.GetRelativePath(Root, MessagePath(row)));
    public bool Contains(MailRow row) => File.Exists(MessagePath(row)) || File.Exists(LegacyPath(row));

    public void ImportLegacy()
    {
        Directory.CreateDirectory(Root);
        if (legacyRoot is null) return;
        var roots = new List<string> { legacyRoot };
        var locator = Path.Combine(legacyRoot, "cache-location.txt");
        if (File.Exists(locator))
        {
            var previous = File.ReadAllText(locator, Encoding.UTF8).Trim();
            if (Path.IsPathFullyQualified(previous) && Path.GetFileName(previous).Equals("MailCache", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFullPath(previous).Equals(Root, StringComparison.OrdinalIgnoreCase)) roots.Insert(0, previous);
        }
        foreach (var previousRoot in roots)
        {
            if (!Directory.Exists(Path.Combine(previousRoot, "messages"))) continue;
            foreach (var source in Directory.EnumerateFiles(Path.Combine(previousRoot, "messages"), "*.bin", SearchOption.AllDirectories))
            {
                var target = Path.Combine(Root, Path.GetRelativePath(previousRoot, source));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(source, temporary);
                    if (!File.Exists(target)) File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        // 只记录上次缓存位置，后续切换版本目录时复制已有副本，不删除旧目录。
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(locator + ".tmp", Root, new UTF8Encoding(false));
        File.Move(locator + ".tmp", locator, true);
    }

    public MimeMessage? Read(MailRow row, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = MessagePath(row);
        if (!File.Exists(path)) path = LegacyPath(row);
        if (path is null || !File.Exists(path)) return null;
        byte[]? plain = null;
        try
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) return null;
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            using var stream = new MemoryStream(plain, writable: false);
            return MimeMessage.Load(stream, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            // 损坏文件保留，在线读取成功后再用完整副本原子替换。
            return null;
        }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public void Write(MailRow row, MimeMessage message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new MemoryStream();
        message.WriteTo(stream, token);
        byte[] bytes;
        var plain = stream.ToArray();
        try { bytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(stream.GetBuffer()); }
        var path = MessagePath(row);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public MailStorageUsage Measure(string accountId, CancellationToken token)
    {
        long total = 0, account = 0;
        int count = 0;
        string error = "";
        var accountPath = AccountPath(accountId) + Path.DirectorySeparatorChar;
        try
        {
            if (Directory.Exists(Root))
                foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var size = new FileInfo(path).Length;
                        total += size;
                        if (path.StartsWith(accountPath, StringComparison.OrdinalIgnoreCase))
                        {
                            account += size;
                            if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) count++;
                        }
                    }
                    catch (FileNotFoundException) { }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "部分存储文件无法统计，当前数值可能不完整。"; }
        return new(Root, total, account, count, error);
    }
}
