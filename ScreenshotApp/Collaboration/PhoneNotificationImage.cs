using System.Globalization;
using System.IO;
using System.Buffers.Binary;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Collaboration;

/// <summary>只解码有界 PNG 缩略图，既不读取外部路径也不访问网络。</summary>
public sealed class PhoneNotificationImage : IValueConverter
{
    public static byte[]? Validate(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return null;
        if (encoded.Length > 22000) throw new InvalidDataException("通知头像过大");
        var data = System.Convert.FromBase64String(encoded);
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length is < 33 or > 16384 || !data.AsSpan(0, 8).SequenceEqual(signature) ||
            !data.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)) is 0 or > 96 ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4)) is 0 or > 96)
            throw new InvalidDataException("通知头像格式无效");
        return data;
    }
    public static BitmapSource? Decode(string? encoded)
    {
        try
        {
            var data = Validate(encoded); if (data is null) return null;
            using var stream = new MemoryStream(data);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch { return null; }
    }
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => Decode(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
