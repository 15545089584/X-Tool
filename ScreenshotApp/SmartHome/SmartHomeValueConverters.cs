using System.Globalization;
using System.Windows.Data;

namespace ScreenshotApp.SmartHome;

/// <summary>让自绘下拉框同时正确显示模式对象与普通字符串。</summary>
public sealed class SmartComboSelectionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SmartModeOption option => option.DisplayName,
        null => string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
