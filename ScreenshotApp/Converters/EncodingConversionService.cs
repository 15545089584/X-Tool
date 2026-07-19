using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotApp.Converters;

/// <summary>纯本地编码、令牌和标识符工具，不会上传输入内容。</summary>
internal static class EncodingConversionService
{
    internal static string Base64Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    internal static string Base64Decode(string text) => Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim()));

    internal static string UrlEncode(string text) => Uri.EscapeDataString(text);

    internal static string UrlDecode(string text) => Uri.UnescapeDataString(text);

    internal static string UnicodeEncode(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character is >= ' ' and <= '~' ? character : $"\\u{(int)character:X4}");
        }

        return builder.ToString();
    }

    internal static string UnicodeDecode(string text) => Regex.Replace(
        text,
        @"\\u(?<value>[0-9a-fA-F]{4})",
        match => ((char)int.Parse(match.Groups["value"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

    internal static JwtParseResult ParseJwt(string token)
    {
        var parts = token.Trim().Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException("JWT 必须由 Header.Payload.Signature 三段组成。");
        }

        var header = FormatJson(Base64UrlDecode(parts[0]));
        var payloadText = Base64UrlDecode(parts[1]);
        var payload = FormatJson(payloadText);
        var expiration = TryReadExpiration(payloadText);
        return new JwtParseResult(header, payload, string.IsNullOrWhiteSpace(parts[2]) ? "签名段为空" : "签名段已存在（未验证）", expiration);
    }

    internal static TimestampConversionResult ConvertTimestamp(string value)
    {
        var input = value.Trim();
        DateTimeOffset instant;
        if (long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            instant = input.Length >= 13 ? DateTimeOffset.FromUnixTimeMilliseconds(numeric) : DateTimeOffset.FromUnixTimeSeconds(numeric);
        }
        else if (!DateTimeOffset.TryParse(input, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out instant) &&
                 !DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant))
        {
            throw new FormatException("请输入 10/13 位时间戳或可识别的日期时间。");
        }

        return new TimestampConversionResult(
            instant.ToUnixTimeSeconds(),
            instant.ToUnixTimeMilliseconds(),
            instant.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
    }

    internal static IReadOnlyList<string> GenerateUuids(int count) => Enumerable.Range(0, Math.Clamp(count, 1, 100)).Select(_ => Guid.NewGuid().ToString()).ToArray();

    private static string Base64UrlDecode(string text)
    {
        var normalized = text.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    private static string FormatJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string TryReadExpiration(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("exp", out var expiration) && expiration.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
            }
        }
        catch (JsonException)
        {
            // 载荷格式错误会在 FormatJson 阶段统一反馈。
        }

        return "未声明 exp";
    }
}

internal sealed record JwtParseResult(string Header, string Payload, string SignatureStatus, string Expiration);
internal sealed record TimestampConversionResult(long Seconds, long Milliseconds, string UtcText, string LocalText);
