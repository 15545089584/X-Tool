using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotApp.GitHubCenter;

public sealed class RepositoryEntry
{
    public string Path { get; set; } = "";
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    public string Summary { get; set; } = "尚未读取";
    public DateTimeOffset? LastChecked { get; set; }
    public DateTimeOffset? LastFetched { get; set; }
    public bool Trusted { get; set; }
    public bool Unavailable { get; set; }
    [JsonIgnore] public string Label => $"{Name}\n{Summary}\n{(LastChecked is { } date ? $"上次检查 {date.LocalDateTime:MM-dd HH:mm}" : "未检查")}";
}
public sealed class GitHubPreferences
{
    public List<RepositoryEntry> Repositories { get; set; } = [];
    public string SelectedPath { get; set; } = "";
    public double Width { get; set; } = 1260;
    public double Height { get; set; } = 800;
}
public sealed record GitHubCredential(string Login, string Token);
public sealed record GitHubCache<T>(DateTimeOffset SavedAt, T Value);

public sealed class GitHubStore
{
    public string Root { get; }
    public string CacheDirectory => System.IO.Path.Combine(Root, "Cache");
    public GitHubStore(string? root = null) => Root = root ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "GitHubCenter");
    public GitHubPreferences LoadPreferences() => Read<GitHubPreferences>("repositories.json", false) ?? new();
    public void SavePreferences(GitHubPreferences value) => Write("repositories.json", value, false);
    public GitHubCredential? LoadCredential() => Read<GitHubCredential>("account.dat", true);
    public void SaveCredential(GitHubCredential credential) => Write("account.dat", credential, true);
    public void Disconnect()
    {
        ClearCache();
        string path = System.IO.Path.Combine(Root, "account.dat");
        if (File.Exists(path)) File.Delete(path);
    }
    private static string CacheName(string key) => "Cache/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".dat";
    public void SaveCache<T>(string key, T value) => Write(CacheName(key), new GitHubCache<T>(DateTimeOffset.UtcNow, value), true);
    public GitHubCache<T>? LoadCache<T>(string key) => Read<GitHubCache<T>>(CacheName(key), true);
    public long CacheSize() => Directory.Exists(CacheDirectory) ? new DirectoryInfo(CacheDirectory).EnumerateFiles("*.dat").Sum(f => f.Length) : 0;
    public void ClearCache()
    {
        // 仅删除本模块生成的平面缓存文件，绝不触碰用户仓库和 Git 对象。
        if (Directory.Exists(CacheDirectory)) foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*.dat")) File.Delete(file);
    }
    private T? Read<T>(string name, bool encrypted)
    {
        string path = System.IO.Path.Combine(Root, name);
        if (!File.Exists(path)) return default;
        var bytes = File.ReadAllBytes(path);
        if (encrypted) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<T>(bytes);
    }
    private void Write<T>(string name, T value, bool encrypted)
    {
        string path = System.IO.Path.Combine(Root, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        if (encrypted) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }
}
