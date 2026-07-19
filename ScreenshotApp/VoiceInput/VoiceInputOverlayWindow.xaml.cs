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
        Loaded += (_, _) => NativeMethods.MakeWindowNonActivating(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    internal void UpdateStatus(string title, string detail)
    {
        // 极简收音浮窗不展示文字状态，保留接口供录音与识别流程统一调用。
    }

    internal void UpdateLevel(double level)
    {
        level = Math.Clamp(level, 0, 1);
        var center = (_waveBars.Length - 1) / 2d;
        for (var index = 0; index < _waveBars.Length; index++)
        {
            var distance = Math.Abs(index - center) / center;
            var emphasis = 0.35 + (1 - distance) * 0.65;
            _waveBars[index].Height = 4 + level * (7 + emphasis * 19);
        }
    }
}
