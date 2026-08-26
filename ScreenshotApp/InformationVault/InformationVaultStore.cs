using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotApp.InformationVault;

/// <summary>使用主密码派生密钥，对整个信息库执行带认证的本地加密。</summary>
internal sealed class InformationVaultStore : IDisposable
{
    private const int CurrentVersion = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 600_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private byte[]? _key;
    private byte[]? _salt;
    private int _iterations = Pbkdf2Iterations;

    internal InformationVaultStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "X-Tool",
            "InformationVault",
            "vault.dat");
    }

    internal bool Exists => File.Exists(_filePath);

    internal bool IsUnlocked => _key is not null;

    internal string StorageDirectory => Path.GetDirectoryName(_filePath) ?? string.Empty;

    internal InformationVaultData Create(string password)
    {
        if (Exists)
        {
            throw new InvalidOperationException("信息库已经存在。若忘记密码，请先执行清空重置。");
        }

        ValidateNewPassword(password);
        _salt = RandomNumberGenerator.GetBytes(SaltSize);
        _iterations = Pbkdf2Iterations;
        ReplaceKey(DeriveKey(password, _salt, _iterations));

        var data = new InformationVaultData();
        Save(data);
        return data;
    }

    internal InformationVaultData Unlock(string password)
    {
        if (!Exists)
        {
            throw new FileNotFoundException("信息库尚未创建。", _filePath);
        }

        var envelope = ReadEnvelope();
        ValidateEnvelope(envelope);
        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var cipherText = Convert.FromBase64String(envelope.CipherText);
        var candidateKey = DeriveKey(password, salt, envelope.Iterations);
        var plainText = new byte[cipherText.Length];

        try
        {
            using var aes = new AesGcm(candidateKey, TagSize);
            aes.Decrypt(nonce, cipherText, tag, plainText, BuildAssociatedData(envelope.Version, envelope.Iterations));
            var data = JsonSerializer.Deserialize<InformationVaultData>(plainText, JsonOptions)
                ?? throw new InvalidDataException("信息库内容为空或已经损坏。");
            data.Entries ??= [];
            foreach (var entry in data.Entries)
            {
                entry.RecoveryCodes ??= [];
                if (entry.Type == InformationVaultEntryType.RecoveryCodes)
                {
                    // 旧版独立恢复码记录在内存中归并为 GitHub 凭据；仅在用户下次主动保存时落盘。
                    entry.Type = InformationVaultEntryType.GitHubCredential;
                }
            }
            data.Version = Math.Max(data.Version, 2);

            _salt = salt.ToArray();
            _iterations = envelope.Iterations;
            ReplaceKey(candidateKey);
            candidateKey = [];
            return data;
        }
        catch (CryptographicException exception)
        {
            throw new InformationVaultPasswordException("主密码不正确，或信息库文件已经损坏。", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("信息库内容无法解析。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidateKey);
            CryptographicOperations.ZeroMemory(plainText);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(cipherText);
        }
    }

    internal void Save(InformationVaultData data)
    {
        if (_key is null || _salt is null)
        {
            throw new InvalidOperationException("信息库尚未解锁。由主密码派生的密钥不可用。");
        }

        Directory.CreateDirectory(StorageDirectory);
        var plainText = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherText = new byte[plainText.Length];
        var tag = new byte[TagSize];

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plainText, cipherText, tag, BuildAssociatedData(CurrentVersion, _iterations));
            var envelope = new InformationVaultEnvelope
            {
                Version = CurrentVersion,
                Iterations = _iterations,
                Salt = Convert.ToBase64String(_salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                CipherText = Convert.ToBase64String(cipherText)
            };
            var serializedEnvelope = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, serializedEnvelope);
                File.Move(temporaryPath, _filePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainText);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(cipherText);
        }
    }

    internal void Lock()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }

        if (_salt is not null)
        {
            CryptographicOperations.ZeroMemory(_salt);
            _salt = null;
        }
    }

    internal void Reset()
    {
        Lock();
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    public void Dispose()
    {
        Lock();
        GC.SuppressFinalize(this);
    }

    private InformationVaultEnvelope ReadEnvelope()
    {
        try
        {
            return JsonSerializer.Deserialize<InformationVaultEnvelope>(File.ReadAllBytes(_filePath), JsonOptions)
                ?? throw new InvalidDataException("信息库文件为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("信息库文件格式无效。", exception);
        }
    }

    private static void ValidateEnvelope(InformationVaultEnvelope envelope)
    {
        if (envelope.Version != CurrentVersion ||
            envelope.Iterations < 100_000 ||
            string.IsNullOrWhiteSpace(envelope.Salt) ||
            string.IsNullOrWhiteSpace(envelope.Nonce) ||
            string.IsNullOrWhiteSpace(envelope.Tag) ||
            string.IsNullOrWhiteSpace(envelope.CipherText))
        {
            throw new InvalidDataException("信息库文件版本或加密参数无效。");
        }
    }

    private static void ValidateNewPassword(string password)
    {
        if (password.Length < 10)
        {
            throw new ArgumentException("主密码至少需要 10 个字符。", nameof(password));
        }

        if (password.Length > 256)
        {
            throw new ArgumentException("主密码不能超过 256 个字符。", nameof(password));
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }

    private void ReplaceKey(byte[] newKey)
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
        }
        _key = newKey;
    }

    private static byte[] BuildAssociatedData(int version, int iterations) =>
        BitConverter.GetBytes(version).Concat(BitConverter.GetBytes(iterations)).ToArray();

    private sealed class InformationVaultEnvelope
    {
        public int Version { get; set; }

        public int Iterations { get; set; }

        public string Salt { get; set; } = string.Empty;

        public string Nonce { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public string CipherText { get; set; } = string.Empty;
    }
}

internal sealed class InformationVaultPasswordException : Exception
{
    internal InformationVaultPasswordException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
