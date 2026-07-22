using System.Windows;
using System.Windows.Controls;
using ScreenshotApp.Capture;

namespace ScreenshotApp.VoiceInput;

/// <summary>不抢占焦点的语音输入状态浮窗。</summary>
public partial class VoiceInputOverlayWindow : Window
{
    private readonly Border[] _waveBars;

    internal VoiceInputOverlayWindow()
    {
        InitializeComponent();
        _waveBars = new[]
        {
            WaveBar0, WaveBar1, WaveBar2, WaveBar3, WaveBar4, WaveBar5, WaveBar6, WaveBar7, WaveBar8,
            WaveBar9, WaveBar10, WaveBar11, WaveBar12, WaveBar13, WaveBar14, WaveBar15, WaveBar16
        };
        // 在窗口真正显示前设置扩展样式，不能等到 Loaded 后才处理，否则会短暂抢走网页输入框焦点。
        SourceInitialized += (_, _) =>
        {
            var windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            NativeMethods.MakeWindowNonActivating(windowHandle);
            NativeMethods.KeepWindowTopmostWithoutActivating(windowHandle);
        };
    }

    /// <summary>在系统面板等顶层窗口出现后，重新确认提示窗的顶层顺序。</summary>
    internal void EnsureTopmostWithoutActivation()
    {
        var windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        // WPF 的 Topmost 只保证处于顶层窗口带；先切换一次属性可将提示窗重新排到
        // 同一顶层带最前方，再由 Win32 调用确保整个过程不会激活窗口。
        Topmost = false;
        Topmost = true;
        NativeMethods.KeepWindowTopmostWithoutActivating(windowHandle);
    }

    internal void UpdateStatus(string title, string detail)
    {
        // 极简收音浮窗不展示文字状态，保留接口供录音与识别流程统一调用。
    }

    internal void AppendRecognizedText(char character)
    {
        RecognizedTextBlock.Text += character;
        TextBubble.Visibility = Visibility.Visible;
    }

    internal void SetRecognizedText(string text)
    {
        RecognizedTextBlock.Text = text;
        TextBubble.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void SetTranslationPreview(string sourceText, string translatedText)
    {
        SetRecognizedText(sourceText);
        TranslatedTextBlock.Text = translatedText;
        TranslationArrow.Visibility = Visibility.Visible;
        TranslatedTextBubble.Visibility = Visibility.Visible;
    }

    internal void ClearTranslationPreview()
    {
        TranslatedTextBlock.Text = string.Empty;
        TranslationArrow.Visibility = Visibility.Collapsed;
        TranslatedTextBubble.Visibility = Visibility.Collapsed;
    }

    internal void ClearRecognizedText()
    {
        RecognizedTextBlock.Text = string.Empty;
        TextBubble.Visibility = Visibility.Collapsed;
        ClearTranslationPreview();
    }

    internal void UpdateLevel(double level)
    {
        level = Math.Clamp(level, 0, 1);
        var center = (_waveBars.Length - 1) / 2d;
        for (var index = 0; index < _waveBars.Length; index++)
        {
            var distance = Math.Abs(index - center) / center;
            // 中央条承载主音量，左右按平滑曲线递减，避免整排等高。
            var centerWeight = Math.Pow(1 - distance, 0.75);
            var barLevel = level * (0.18 + centerWeight * 0.82);
            _waveBars[index].Height = 3 + barLevel * 28;
        }
    }
}
