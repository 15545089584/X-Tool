using System.Windows;
using ScreenshotApp.Capture;

namespace ScreenshotApp.VoiceInput;

/// <summary>不抢占焦点的语音输入状态浮窗。</summary>
public partial class VoiceInputOverlayWindow : Window
{
    internal VoiceInputOverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NativeMethods.MakeWindowNonActivating(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    internal void UpdateStatus(string title, string detail)
    {
        TitleText.Text = title;
        DetailText.Text = detail;
    }
}
