using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenshotApp.StorageAnalysis;

/// <summary>
/// 使用单个绘图表面呈现平衡树状图，避免为大量块创建独立 WPF 控件。
/// </summary>
public sealed class StorageTreemapControl : FrameworkElement
{
    private static readonly Typeface TitleTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface BodyTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly List<TreemapVisual> _visuals = new();
    private IReadOnlyList<StorageTreemapItem> _items = Array.Empty<StorageTreemapItem>();
    private StorageTreemapItem? _hoveredItem;

    public StorageTreemapControl()
    {
        Focusable = true;
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
    }

    public event EventHandler<StorageTreemapItemEventArgs>? ItemInvoked;
    public event EventHandler<StorageTreemapItemEventArgs>? ItemHovered;

    public IReadOnlyList<StorageTreemapItem> Items => _items;

    public void SetItems(IEnumerable<StorageTreemapItem> items)
    {
        _items = items
            .Where(item => item.Size > 0)
            .OrderByDescending(item => item.Size)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(240)
            .ToArray();
        _hoveredItem = null;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _visuals.Clear();

        var bounds = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        var background = new LinearGradientBrush(
            Color.FromArgb(42, 236, 246, 255),
            Color.FromArgb(24, 255, 255, 255),
            new Point(0, 0),
            new Point(1, 1));
        drawingContext.DrawRoundedRectangle(background, new Pen(new SolidColorBrush(Color.FromArgb(100, 198, 224, 242)), 1), bounds, 18, 18);

        if (_items.Count == 0 || bounds.Width < 40 || bounds.Height < 40)
        {
            DrawEmptyState(drawingContext, bounds);
            return;
        }

        var content = new Rect(8, 8, Math.Max(0, bounds.Width - 16), Math.Max(0, bounds.Height - 16));
        var weighted = _items.Select(item => new WeightedItem(item, item.Size)).ToArray();
        LayoutBalanced(weighted, content, 0, weighted.Length, weighted.Sum(item => item.Weight), _visuals);

        foreach (var visual in _visuals)
        {
            DrawItem(drawingContext, visual, ReferenceEquals(visual.Item, _hoveredItem));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        if (ReferenceEquals(hit, _hoveredItem)) return;

        _hoveredItem = hit;
        Cursor = hit is null ? Cursors.Arrow : Cursors.Hand;
        ToolTip = hit is null ? null : $"{hit.Name}\n{hit.SizeText} · {hit.RatioText}\n{hit.FullPath}";
        if (hit is not null) ItemHovered?.Invoke(this, new StorageTreemapItemEventArgs(hit));
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredItem = null;
        Cursor = Cursors.Arrow;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit is null) return;
        Focus();
        ItemInvoked?.Invoke(this, new StorageTreemapItemEventArgs(hit));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.Key is Key.Enter or Key.Space) && _hoveredItem is not null)
        {
            ItemInvoked?.Invoke(this, new StorageTreemapItemEventArgs(_hoveredItem));
            e.Handled = true;
        }
    }

    private StorageTreemapItem? HitTest(Point point) => _visuals
        .Where(visual => visual.Bounds.Contains(point))
        .OrderBy(visual => visual.Bounds.Width * visual.Bounds.Height)
        .Select(visual => visual.Item)
        .FirstOrDefault();

    private static void LayoutBalanced(
        IReadOnlyList<WeightedItem> items,
        Rect bounds,
        int start,
        int count,
        double totalWeight,
        ICollection<TreemapVisual> output)
    {
        if (count <= 0 || bounds.Width <= 1 || bounds.Height <= 1 || totalWeight <= 0) return;
        if (count == 1)
        {
            output.Add(new TreemapVisual(items[start].Item, Deflate(bounds, 2.2)));
            return;
        }

        var target = totalWeight / 2d;
        var firstWeight = 0d;
        var split = start;
        while (split < start + count - 1)
        {
            var next = firstWeight + items[split].Weight;
            if (firstWeight > 0 && Math.Abs(target - firstWeight) <= Math.Abs(target - next)) break;
            firstWeight = next;
            split++;
        }

        if (split == start)
        {
            firstWeight = items[start].Weight;
            split = start + 1;
        }

        var firstCount = split - start;
        var ratio = Math.Clamp(firstWeight / totalWeight, 0.03, 0.97);
        Rect firstBounds;
        Rect secondBounds;
        if (bounds.Width >= bounds.Height)
        {
            var width = bounds.Width * ratio;
            firstBounds = new Rect(bounds.X, bounds.Y, width, bounds.Height);
            secondBounds = new Rect(bounds.X + width, bounds.Y, bounds.Width - width, bounds.Height);
        }
        else
        {
            var height = bounds.Height * ratio;
            firstBounds = new Rect(bounds.X, bounds.Y, bounds.Width, height);
            secondBounds = new Rect(bounds.X, bounds.Y + height, bounds.Width, bounds.Height - height);
        }

        LayoutBalanced(items, firstBounds, start, firstCount, firstWeight, output);
        LayoutBalanced(items, secondBounds, split, count - firstCount, totalWeight - firstWeight, output);
    }

    private static Rect Deflate(Rect bounds, double amount)
    {
        var width = Math.Max(0, bounds.Width - amount * 2);
        var height = Math.Max(0, bounds.Height - amount * 2);
        return new Rect(bounds.X + amount, bounds.Y + amount, width, height);
    }

    private void DrawItem(DrawingContext drawingContext, TreemapVisual visual, bool hovered)
    {
        var bounds = visual.Bounds;
        if (bounds.Width < 1 || bounds.Height < 1) return;

        var accent = ParseColor(visual.Item.Accent, Color.FromRgb(77, 124, 254));
        var top = Color.FromArgb(hovered ? (byte)235 : (byte)210,
            Lighten(accent.R, hovered ? 34 : 24),
            Lighten(accent.G, hovered ? 34 : 24),
            Lighten(accent.B, hovered ? 34 : 24));
        var bottom = Color.FromArgb(hovered ? (byte)222 : (byte)196, accent.R, accent.G, accent.B);
        var fill = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(1, 1));
        fill.Freeze();

        if (hovered)
        {
            var shadowBounds = new Rect(bounds.X + 2, bounds.Y + 3, bounds.Width, bounds.Height);
            drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(46, 54, 87, 125)), null, shadowBounds, 11, 11);
        }

        var border = new Pen(new SolidColorBrush(Color.FromArgb(hovered ? (byte)235 : (byte)150, 255, 255, 255)), hovered ? 2 : 1);
        border.Freeze();
        drawingContext.DrawRoundedRectangle(fill, border, bounds, 11, 11);

        if (bounds.Width < 52 || bounds.Height < 34) return;
        drawingContext.PushClip(new RectangleGeometry(new Rect(bounds.X + 7, bounds.Y + 6, Math.Max(0, bounds.Width - 14), Math.Max(0, bounds.Height - 12)), 7, 7));

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var titleSize = bounds.Width >= 150 && bounds.Height >= 74 ? 13d : 11d;
        var title = CreateText(visual.Item.Name, TitleTypeface, titleSize, Colors.White, pixelsPerDip, Math.Max(0, bounds.Width - 18));
        drawingContext.DrawText(title, new Point(bounds.X + 10, bounds.Y + 9));

        if (bounds.Width >= 86 && bounds.Height >= 52)
        {
            var size = CreateText(visual.Item.SizeText, TitleTypeface, bounds.Height >= 86 ? 12 : 10, Color.FromArgb(238, 255, 255, 255), pixelsPerDip, Math.Max(0, bounds.Width - 18));
            drawingContext.DrawText(size, new Point(bounds.X + 10, bounds.Y + 12 + title.Height));
        }

        if (bounds.Width >= 130 && bounds.Height >= 92)
        {
            var hint = visual.Item.IsDirectory ? $"{visual.Item.RatioText} · 单击进入" : visual.Item.RatioText;
            var detail = CreateText(hint, BodyTypeface, 9.5, Color.FromArgb(205, 255, 255, 255), pixelsPerDip, Math.Max(0, bounds.Width - 18));
            drawingContext.DrawText(detail, new Point(bounds.X + 10, bounds.Bottom - detail.Height - 9));
        }

        drawingContext.Pop();
    }

    private static FormattedText CreateText(string text, Typeface typeface, double size, Color color, double pixelsPerDip, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, new SolidColorBrush(color), pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1
        };
        return formatted;
    }

    private static void DrawEmptyState(DrawingContext drawingContext, Rect bounds)
    {
        var pixelsPerDip = 1d;
        var text = CreateText("当前目录没有可显示的已扫描内容", BodyTypeface, 12, Color.FromRgb(111, 137, 160), pixelsPerDip, Math.Max(1, bounds.Width - 40));
        drawingContext.DrawText(text, new Point(Math.Max(20, (bounds.Width - text.Width) / 2), Math.Max(20, (bounds.Height - text.Height) / 2)));
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
    }

    private static byte Lighten(byte value, int amount) => (byte)Math.Min(255, value + amount);

    private sealed record WeightedItem(StorageTreemapItem Item, double Weight);
    private sealed record TreemapVisual(StorageTreemapItem Item, Rect Bounds);
}

public sealed record StorageTreemapItem(
    string Name,
    string FullPath,
    long Size,
    double Ratio,
    string Accent,
    bool IsDirectory,
    bool IsAggregate = false)
{
    public string SizeText => StorageAnalysisDisplay.FormatCapacity(Size);
    public string RatioText => $"占当前层级 {Ratio:P1}";
    public string TypeText => IsDirectory ? "目录" : IsAggregate ? "聚合文件" : "文件";
    public bool CanOpenInFileWorkbench => IsDirectory || (!IsAggregate && File.Exists(FullPath));
}

public sealed class StorageTreemapItemEventArgs : EventArgs
{
    public StorageTreemapItemEventArgs(StorageTreemapItem item) => Item = item;
    public StorageTreemapItem Item { get; }
}
