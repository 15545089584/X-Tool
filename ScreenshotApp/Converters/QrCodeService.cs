using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace ScreenshotApp.Converters;

/// <summary>二维码内容的实用分类，用于历史与结果展示。分类只做展示提示，不改变原文。</summary>
public enum QrContentCategory
{
    PlainText,
    Url,
    Email,
    Phone,
    Wifi,
    VCard,
    Calendar,
    Batch
}

/// <summary>单张图片的解码结果；失败时内容为空并携带错误说明，不阻断其他图片。</summary>
public sealed record QrDecodedItem(string FileName, string? Content, QrContentCategory Category, string? Error);

/// <summary>二维码历史条目，保存在本机 JSON 文件中。</summary>
public sealed record QrHistoryEntry(
    string Id,
    string Kind,
    string Category,
    string Content,
    string? FilePath,
    DateTime CreatedAt);

public static class QrCodeService
{
    /// <summary>按当前内容与参数生成二维码位图；全部在本地完成。</summary>
    public static BitmapSource Generate(
        string content,
        int size,
        int margin,
        ErrorCorrectionLevel level,
        Color foreground,
        Color background)
    {
        var hints = new Dictionary<EncodeHintType, object>
        {
            { EncodeHintType.WIDTH, size },
            { EncodeHintType.HEIGHT, size },
            { EncodeHintType.MARGIN, margin },
            { EncodeHintType.ERROR_CORRECTION, level },
            { EncodeHintType.CHARACTER_SET, "UTF-8" }
        };

        // QRCodeWriter.encode 会按 width/height 把模块矩阵放大到最终像素尺寸（含留白）。
        var matrix = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, size, size, hints);
        var width = matrix.Width;
        var height = matrix.Height;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var dark = matrix[x, y];
                var color = dark ? foreground : background;
                var offset = row + x * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        // 在创建线程冻结，允许回到 UI 线程安全赋值。
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>从位图识别二维码，自动尝试旋转并支持同图多个二维码。</summary>
    public static IReadOnlyList<string> Decode(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var luminance = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                CharacterSet = "UTF-8",
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
            }
        };

        var results = reader.DecodeMultiple(luminance);
        var items = new List<string>();
        if (results != null)
        {
            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    items.Add(result.Text);
                }
            }
        }
        return items;
    }

    public static BitmapSource DecodeBitmapFile(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return FreezeForCrossThread(decoder.Frames[0]);
    }

    /// <summary>在创建线程把位图冻结为可跨线程读取的副本；无法冻结时复制像素重建。</summary>
    public static BitmapSource FreezeForCrossThread(BitmapSource source)
    {
        if (source.CanFreeze)
        {
            source.Freeze();
            return source;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    public static QrContentCategory Classify(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return QrContentCategory.PlainText;
        }

        var text = content.TrimStart();
        if (text.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase))
        {
            return QrContentCategory.Wifi;
        }
        if (text.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
        {
            return QrContentCategory.VCard;
        }
        if (text.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
        {
            return QrContentCategory.Calendar;
        }
        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return QrContentCategory.Email;
        }
        if (text.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("SMSTO:", StringComparison.OrdinalIgnoreCase))
        {
            return QrContentCategory.Phone;
        }
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return QrContentCategory.Url;
        }
        return QrContentCategory.PlainText;
    }

    public static string CategoryText(QrContentCategory category) => category switch
    {
        QrContentCategory.Url => "网址",
        QrContentCategory.Email => "邮件",
        QrContentCategory.Phone => "电话/短信",
        QrContentCategory.Wifi => "WiFi",
        QrContentCategory.VCard => "名片",
        QrContentCategory.Calendar => "日历事件",
        QrContentCategory.Batch => "批量",
        _ => "文本"
    };

    /// <summary>WiFi 内容按二维码规范转义特殊字符。</summary>
    public static string BuildWifi(string ssid, string password, string auth)
    {
        static string Escape(string value) => value
            .Replace("\\", "\\\\")
            .Replace(";", "\\;")
            .Replace(",", "\\,")
            .Replace("\"", "\\\"")
            .Replace(":", "\\:");

        var builder = new StringBuilder("WIFI:T:");
        builder.Append(auth switch
        {
            "WEP" => "WEP",
            "无密码" => "nopass",
            _ => "WPA"
        });
        builder.Append(";S:").Append(Escape(ssid));
        if (password.Length > 0)
        {
            builder.Append(";P:").Append(Escape(password));
        }
        builder.Append(";H:false;;");
        return builder.ToString();
    }

    public static string BuildVCard(
        string name,
        string phone,
        string email,
        string company,
        string title,
        string url,
        string note)
    {
        var builder = new StringBuilder();
        builder.AppendLine("BEGIN:VCARD");
        builder.AppendLine("VERSION:3.0");
        if (!string.IsNullOrWhiteSpace(name))
        {
            builder.Append("FN:").AppendLine(name);
        }
        if (!string.IsNullOrWhiteSpace(company))
        {
            builder.Append("ORG:").AppendLine(company);
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("TITLE:").AppendLine(title);
        }
        if (!string.IsNullOrWhiteSpace(phone))
        {
            builder.Append("TEL;TYPE=CELL:").AppendLine(phone);
        }
        if (!string.IsNullOrWhiteSpace(email))
        {
            builder.Append("EMAIL:").AppendLine(email);
        }
        if (!string.IsNullOrWhiteSpace(url))
        {
            builder.Append("URL:").AppendLine(url);
        }
        if (!string.IsNullOrWhiteSpace(note))
        {
            builder.Append("NOTE:").AppendLine(note);
        }
        builder.Append("END:VCARD");
        return builder.ToString();
    }

    public static string BuildMailto(string to, string subject, string body)
    {
        var builder = new StringBuilder("mailto:").Append(Uri.EscapeDataString(to.Trim()));
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(subject))
        {
            query.Add("subject=" + Uri.EscapeDataString(subject));
        }
        if (!string.IsNullOrWhiteSpace(body))
        {
            query.Add("body=" + Uri.EscapeDataString(body));
        }
        if (query.Count > 0)
        {
            builder.Append('?').Append(string.Join("&", query));
        }
        return builder.ToString();
    }

    public static string BuildSms(string phone, string body) =>
        string.IsNullOrWhiteSpace(body) ? "SMSTO:" + phone.Trim() : "SMSTO:" + phone.Trim() + ":" + body;

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>仅在用户明确点击后打开外部链接，避免自动执行未知内容。</summary>
    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static string FriendlySize(string? content)
    {
        if (content == null)
        {
            return "0 字符";
        }
        var length = Encoding.UTF8.GetByteCount(content);
        return length >= 1024
            ? $"{length / 1024.0:0.0} KB"
            : $"{length} 字节";
    }

    public static string EscapeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return builder.ToString();
    }
}

/// <summary>二维码历史持久化：本机 JSON 文件，最多保留 500 条，读取与写入都在调用线程完成。</summary>
public static class QrHistoryStore
{
    private const int MaxEntries = 500;

    private static readonly string RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X-Tool",
        "QRCode");

    private static readonly string HistoryFile = Path.Combine(RootDirectory, "history.json");
    private static readonly object Gate = new();
    private static List<QrHistoryEntry>? _cache;
    private static DateTime _cacheFileTime = DateTime.MinValue;

    public static IReadOnlyList<QrHistoryEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(HistoryFile))
                {
                    _cache = null;
                    return Array.Empty<QrHistoryEntry>();
                }
                var fileTime = File.GetLastWriteTimeUtc(HistoryFile);
                if (_cache != null && _cacheFileTime == fileTime)
                {
                    return _cache;
                }
                var json = File.ReadAllText(HistoryFile, Encoding.UTF8);
                var entries = JsonSerializer.Deserialize<List<QrHistoryEntry>>(json);
                _cache = entries ?? new List<QrHistoryEntry>();
                _cacheFileTime = fileTime;
                return _cache;
            }
            catch
            {
                // 历史文件损坏只影响浏览，不阻断二维码功能。
                return Array.Empty<QrHistoryEntry>();
            }
        }
    }

    public static void Add(QrHistoryEntry entry)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(RootDirectory);
                var entries = LoadUnsafe();
                entries.Insert(0, entry);
                if (entries.Count > MaxEntries)
                {
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
                }
                WriteUnsafe(entries);
                _cache = null;
            }
            catch
            {
                // 历史写入失败不影响生成/识别结果本身。
            }
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(HistoryFile))
                {
                    return;
                }
                var entries = LoadUnsafe();
                var removed = entries.RemoveAll(e => e.Id == id);
                if (removed > 0)
                {
                    WriteUnsafe(entries);
                    _cache = null;
                }
            }
            catch
            {
                // 删除失败时保持原历史，下次启动仍可清理。
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(HistoryFile))
                {
                    File.Delete(HistoryFile);
                }
                _cache = null;
            }
            catch
            {
                // 清理失败时保持原历史。
            }
        }
    }

    private static List<QrHistoryEntry> LoadUnsafe()
    {
        if (!File.Exists(HistoryFile))
        {
            return new List<QrHistoryEntry>();
        }
        var json = File.ReadAllText(HistoryFile, Encoding.UTF8);
        return JsonSerializer.Deserialize<List<QrHistoryEntry>>(json) ?? new List<QrHistoryEntry>();
    }

    private static void WriteUnsafe(List<QrHistoryEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(HistoryFile, json, new UTF8Encoding(false));
    }
}
