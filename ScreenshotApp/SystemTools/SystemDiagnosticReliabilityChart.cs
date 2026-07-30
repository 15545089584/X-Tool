using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenshotApp.SystemTools;

internal sealed class SystemDiagnosticTimeCellSelectedEventArgs : EventArgs
{
    internal SystemDiagnosticTimeCellSelectedEventArgs(int bucketIndex, SystemDiagnosticSeverity? severity)
    {
        BucketIndex = bucketIndex;
        Severity = severity;
    }

    internal int BucketIndex { get; }
    internal SystemDiagnosticSeverity? Severity { get; }
}

/// <summary>参考 Windows 可靠性监视器，以可点击的时间格展示各级异常事件。</summary>
public sealed class SystemDiagnosticReliabilityChart : FrameworkElement
{
    private const double PlotLeft = 12;
    private const double HeaderHeight = 12;
    private const double FooterHeight = 48;
    private static readonly SystemDiagnosticSeverity[] RowSeverities =
    {
        SystemDiagnosticSeverity.Critical,
        SystemDiagnosticSeverity.Error,
        SystemDiagnosticSeverity.Warning
    };

    private static readonly Brush AxisBrush = FrozenBrush("#7892A9BF");
    private static readonly Brush GridBrush = FrozenBrush("#58A9C4DA");
    private static readonly Brush LabelBrush = FrozenBrush("#667F98");
    private static readonly Brush MutedBrush = FrozenBrush("#8A9EAF");
    private static readonly Brush CriticalBrush = FrozenBrush("#E56565");
    private static readonly Brush ErrorBrush = FrozenBrush("#F08A62");
    private static readonly Brush WarningBrush = FrozenBrush("#3B9EFF");
    private static readonly Brush CriticalTextBrush = FrozenBrush("#C54848");
    private static readonly Brush ErrorTextBrush = FrozenBrush("#C65D36");
    private static readonly Brush WarningTextBrush = FrozenBrush("#247CC7");
    private static readonly Brush EvenColumnBrush = FrozenBrush("#30B7CCDF");
    private static readonly Brush OddColumnBrush = FrozenBrush("#14FFFFFF");
    private static readonly Brush PairDividerBrush = FrozenBrush("#70A2BED4");
    private static readonly Brush SelectedCellBrush = FrozenBrush("#4A8AB8FF");
    private static readonly Brush SelectedBorderBrush = FrozenBrush("#4D7CFE");

    private IReadOnlyList<SystemDiagnosticTimelineBucket> _buckets = Array.Empty<SystemDiagnosticTimelineBucket>();
    private int _hoveredBucketIndex = -1;
    private SystemDiagnosticSeverity? _hoveredSeverity;

    internal event EventHandler<SystemDiagnosticTimeCellSelectedEventArgs>? CellSelected;

    internal IReadOnlyList<SystemDiagnosticTimelineBucket> Buckets
    {
        get => _buckets;
        set
        {
            _buckets = value ?? Array.Empty<SystemDiagnosticTimelineBucket>();
            if (SelectedBucketIndex >= _buckets.Count)
            {
                SelectedBucketIndex = -1;
                SelectedSeverity = null;
            }

            InvalidateVisual();
        }
    }

    internal int SelectedBucketIndex { get; private set; } = -1;
    internal SystemDiagnosticSeverity? SelectedSeverity { get; private set; }

    internal void SelectCell(int bucketIndex, SystemDiagnosticSeverity? severity, bool raiseEvent)
    {
        if (bucketIndex < 0 || bucketIndex >= _buckets.Count)
        {
            SelectedBucketIndex = -1;
            SelectedSeverity = null;
        }
        else
        {
            SelectedBucketIndex = bucketIndex;
            SelectedSeverity = severity;
        }

        InvalidateVisual();
        if (raiseEvent && SelectedBucketIndex >= 0)
        {
            CellSelected?.Invoke(this, new SystemDiagnosticTimeCellSelectedEventArgs(SelectedBucketIndex, SelectedSeverity));
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 360 || height < 220)
        {
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var plotWidth = Math.Max(1, width - PlotLeft - 10);
        var plotHeight = Math.Max(1, height - HeaderHeight - FooterHeight);
        var rowHeight = plotHeight / RowSeverities.Length;
        var axisPen = new Pen(AxisBrush, 1);
        var gridPen = new Pen(GridBrush, 1);

        if (_buckets.Count == 0)
        {
            DrawEmptyState(drawingContext, width, height, pixelsPerDip);
            return;
        }

        var columnWidth = plotWidth / _buckets.Count;
        for (var column = 0; column < _buckets.Count; column++)
        {
            var x = PlotLeft + columnWidth * column;
            drawingContext.DrawRectangle(
                column % 2 == 0 ? EvenColumnBrush : OddColumnBrush,
                null,
                new Rect(x, HeaderHeight, columnWidth, plotHeight));

            if (SelectedBucketIndex == column && SelectedSeverity is null)
            {
                drawingContext.DrawRoundedRectangle(SelectedCellBrush, new Pen(SelectedBorderBrush, 1.4), new Rect(x + 2, HeaderHeight + 2, Math.Max(1, columnWidth - 4), plotHeight - 4), 8, 8);
            }
            else if (_hoveredBucketIndex == column)
            {
                drawingContext.DrawRoundedRectangle(FrozenBrush("#20FFFFFF"), new Pen(FrozenBrush("#729BC0E3"), 1), new Rect(x + 3, HeaderHeight + 3, Math.Max(1, columnWidth - 6), plotHeight - 6), 7, 7);
            }

            var columnPen = column > 0 ? new Pen(PairDividerBrush, 1.1) : gridPen;
            drawingContext.DrawLine(columnPen, new Point(x, HeaderHeight), new Point(x, HeaderHeight + plotHeight));
            DrawText(drawingContext, _buckets[column].Label, 9.2, LabelBrush, new Point(x, HeaderHeight + plotHeight + 15), columnWidth, TextAlignment.Center, pixelsPerDip, FontWeights.SemiBold);
        }

        drawingContext.DrawLine(gridPen, new Point(PlotLeft + plotWidth, HeaderHeight), new Point(PlotLeft + plotWidth, HeaderHeight + plotHeight));
        for (var row = 0; row <= RowSeverities.Length; row++)
        {
            var y = HeaderHeight + rowHeight * row;
            drawingContext.DrawLine(row == RowSeverities.Length ? axisPen : gridPen, new Point(PlotLeft, y), new Point(PlotLeft + plotWidth, y));
        }

        drawingContext.DrawLine(axisPen, new Point(PlotLeft, HeaderHeight), new Point(PlotLeft, HeaderHeight + plotHeight));
        for (var row = 0; row < RowSeverities.Length; row++)
        {
            var severity = RowSeverities[row];
            var rowY = HeaderHeight + rowHeight * row;
            for (var column = 0; column < _buckets.Count; column++)
            {
                var x = PlotLeft + columnWidth * column;
                var cell = new Rect(x, rowY, columnWidth, rowHeight);
                DrawMarker(drawingContext, severity, CountFor(_buckets[column], severity), cell, pixelsPerDip);
            }
        }

    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (!TryHitCell(e.GetPosition(this), out var bucketIndex, out var severity))
        {
            return;
        }

        SelectCell(bucketIndex, severity, true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var previousBucket = _hoveredBucketIndex;
        var previousSeverity = _hoveredSeverity;
        if (TryHitCell(e.GetPosition(this), out var bucketIndex, out var severity))
        {
            _hoveredBucketIndex = bucketIndex;
            _hoveredSeverity = null;
            Cursor = Cursors.Hand;
            var bucket = _buckets[bucketIndex];
            ToolTip = $"{bucket.Label} · 全部级别 {bucket.TotalCount:N0} 条";
        }
        else
        {
            _hoveredBucketIndex = -1;
            _hoveredSeverity = null;
            Cursor = Cursors.Arrow;
            ToolTip = null;
        }

        if (previousBucket != _hoveredBucketIndex || previousSeverity != _hoveredSeverity)
        {
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredBucketIndex = -1;
        _hoveredSeverity = null;
        Cursor = Cursors.Arrow;
        ToolTip = null;
        InvalidateVisual();
    }

    private bool TryHitCell(Point point, out int bucketIndex, out SystemDiagnosticSeverity? severity)
    {
        bucketIndex = -1;
        severity = null;
        if (_buckets.Count == 0 || point.X < PlotLeft || point.X > ActualWidth - 10 || point.Y < HeaderHeight || point.Y > ActualHeight)
        {
            return false;
        }

        var plotWidth = Math.Max(1, ActualWidth - PlotLeft - 10);
        var columnWidth = plotWidth / _buckets.Count;
        bucketIndex = Math.Clamp((int)((point.X - PlotLeft) / columnWidth), 0, _buckets.Count - 1);
        // 点击列内任意区域都选中整天，不再进入单个危险等级的选中状态。
        severity = null;
        return true;
    }

    private static void DrawEmptyState(DrawingContext context, double width, double height, double pixelsPerDip)
    {
        DrawText(context, "完成诊断后将在此显示可交互的异常时间格", 13, LabelBrush, new Point(PlotLeft, height / 2 - 20), width - PlotLeft - 10, TextAlignment.Center, pixelsPerDip, FontWeights.SemiBold);
        DrawText(context, "红色、橙色与蓝色图标分别表示不同事件级别", 10, MutedBrush, new Point(PlotLeft, height / 2 + 4), width - PlotLeft - 10, TextAlignment.Center, pixelsPerDip, FontWeights.Normal);
    }

    private static void DrawMarker(DrawingContext context, SystemDiagnosticSeverity severity, int count, Rect cell, double pixelsPerDip)
    {
        if (count <= 0)
        {
            return;
        }

        var color = SeverityBrush(severity);
        var size = Math.Min(25, Math.Max(20, Math.Min(cell.Width - 10, cell.Height - 30)));
        var center = new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2 - 5);
        context.DrawEllipse(color, null, center, size / 2, size / 2);
        DrawText(
            context,
            SeverityGlyph(severity),
            size * 0.56,
            Brushes.White,
            new Point(center.X - size / 2, center.Y - size * 0.31),
            size,
            TextAlignment.Center,
            pixelsPerDip,
            FontWeights.Bold,
            "Segoe Fluent Icons");
        DrawText(context, count.ToString("N0"), 10.5, SeverityTextBrush(severity), new Point(cell.X, center.Y + size / 2 + 4), cell.Width, TextAlignment.Center, pixelsPerDip, FontWeights.Bold);
    }

    private static int CountFor(SystemDiagnosticTimelineBucket bucket, SystemDiagnosticSeverity severity) => severity switch
    {
        SystemDiagnosticSeverity.Critical => bucket.CriticalCount,
        SystemDiagnosticSeverity.Error => bucket.ErrorCount,
        _ => bucket.WarningCount
    };

    private static Brush SeverityBrush(SystemDiagnosticSeverity severity) => severity switch
    {
        SystemDiagnosticSeverity.Critical => CriticalBrush,
        SystemDiagnosticSeverity.Error => ErrorBrush,
        _ => WarningBrush
    };

    private static Brush SeverityTextBrush(SystemDiagnosticSeverity severity) => severity switch
    {
        SystemDiagnosticSeverity.Critical => CriticalTextBrush,
        SystemDiagnosticSeverity.Error => ErrorTextBrush,
        _ => WarningTextBrush
    };

    private static string SeverityGlyph(SystemDiagnosticSeverity severity) => severity switch
    {
        SystemDiagnosticSeverity.Critical => "\uE711",
        SystemDiagnosticSeverity.Error => "\uE783",
        _ => "\uE7BA"
    };

    private static void DrawText(
        DrawingContext context,
        string text,
        double size,
        Brush brush,
        Point origin,
        double width,
        TextAlignment alignment,
        double pixelsPerDip,
        FontWeight weight,
        string fontFamily = "Segoe UI")
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(fontFamily), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width),
            TextAlignment = alignment,
            Trimming = TextTrimming.CharacterEllipsis
        };
        context.DrawText(formatted, origin);
    }

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
