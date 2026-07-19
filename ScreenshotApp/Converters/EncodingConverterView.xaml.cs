using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenshotApp.Converters;

/// <summary>本地编码转换页面。</summary>
public partial class EncodingConverterView : UserControl
{
    public EncodingConverterView()
    {
        InitializeComponent();
        SetMode("Text");
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e) => SetMode((sender as FrameworkElement)?.Tag?.ToString() ?? "Text");

    private void SetMode(string mode)
    {
        TextModePanel.Visibility = mode == "Text" ? Visibility.Visible : Visibility.Collapsed;
        JwtModePanel.Visibility = mode == "Jwt" ? Visibility.Visible : Visibility.Collapsed;
        TimestampModePanel.Visibility = mode == "Timestamp" ? Visibility.Visible : Visibility.Collapsed;
        UuidModePanel.Visibility = mode == "Uuid" ? Visibility.Visible : Visibility.Collapsed;
        SetModeButton(TextModeButton, mode == "Text");
        SetModeButton(JwtModeButton, mode == "Jwt");
        SetModeButton(TimestampModeButton, mode == "Timestamp");
        SetModeButton(UuidModeButton, mode == "Uuid");
        StatusText.Text = string.Empty;
    }

    private static void SetModeButton(Button button, bool selected)
    {
        button.Background = new SolidColorBrush(selected ? Color.FromRgb(77, 124, 254) : Color.FromArgb(142, 255, 255, 255));
        button.BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(117, 160, 255) : Color.FromRgb(169, 212, 234));
        button.Foreground = new SolidColorBrush(selected ? Colors.White : Color.FromRgb(66, 97, 125));
    }

    private void TextOperation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TextOutputBox.Text = (sender as FrameworkElement)?.Tag?.ToString() switch
            {
                "Base64Encode" => EncodingConversionService.Base64Encode(TextInputBox.Text),
                "Base64Decode" => EncodingConversionService.Base64Decode(TextInputBox.Text),
                "UrlEncode" => EncodingConversionService.UrlEncode(TextInputBox.Text),
                "UrlDecode" => EncodingConversionService.UrlDecode(TextInputBox.Text),
                "UnicodeEncode" => EncodingConversionService.UnicodeEncode(TextInputBox.Text),
                "UnicodeDecode" => EncodingConversionService.UnicodeDecode(TextInputBox.Text),
                _ => string.Empty
            };
            StatusText.Text = "转换完成";
        }
        catch (Exception exception) { StatusText.Text = $"转换失败：{exception.Message}"; }
    }

    private void CopyTextResult_Click(object sender, RoutedEventArgs e) => Copy(TextOutputBox.Text);

    private void ParseJwt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = EncodingConversionService.ParseJwt(JwtInputBox.Text);
            JwtHeaderBox.Text = result.Header;
            JwtPayloadBox.Text = result.Payload;
            JwtStatusText.Text = $"{result.SignatureStatus} · 到期时间：{result.Expiration}";
            StatusText.Text = "JWT 已解析（未验证签名）";
        }
        catch (Exception exception) { JwtStatusText.Text = $"解析失败：{exception.Message}"; }
    }

    private void ConvertTimestamp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = EncodingConversionService.ConvertTimestamp(TimestampInputBox.Text);
            TimestampSecondsBox.Text = result.Seconds.ToString(CultureInfo.InvariantCulture);
            TimestampMillisecondsBox.Text = result.Milliseconds.ToString(CultureInfo.InvariantCulture);
            TimestampUtcBox.Text = result.UtcText;
            TimestampLocalBox.Text = result.LocalText;
            StatusText.Text = "时间已转换";
        }
        catch (Exception exception) { StatusText.Text = $"转换失败：{exception.Message}"; }
    }

    private void UseCurrentTime_Click(object sender, RoutedEventArgs e)
    {
        TimestampInputBox.Text = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        ConvertTimestamp_Click(sender, e);
    }

    private void GenerateUuid_Click(object sender, RoutedEventArgs e)
    {
        var count = int.TryParse(UuidCountBox.Text, out var parsed) ? parsed : 1;
        UuidOutputBox.Text = string.Join(Environment.NewLine, EncodingConversionService.GenerateUuids(count));
        StatusText.Text = $"已生成 {Math.Clamp(count, 1, 100)} 个 UUID v4";
    }

    private void CopyUuid_Click(object sender, RoutedEventArgs e) => Copy(UuidOutputBox.Text);

    private void Copy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { StatusText.Text = "没有可复制的内容"; return; }
        try { Clipboard.SetText(text); StatusText.Text = "已复制到剪贴板"; }
        catch (Exception exception) { StatusText.Text = $"复制失败：{exception.Message}"; }
    }
}
