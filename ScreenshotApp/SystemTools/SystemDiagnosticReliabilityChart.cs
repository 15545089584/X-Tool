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
    private static readonly Brush AlternatingCellBrush = FrozenBrush("#22AFC9E0");
    private static readonly Brush SelectedCellBrush = FrozenBrush("#4A8AB8FF");
    private static readonly Brush SelectedBorderBrush = FrozenBrush("#4D7CFE");
    private static readonly Brush MarkerBackgroundBrush = FrozenBrush("#D8FFFFFF");

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
            if (column % 2 == 0)
            {
                drawingContext.DrawRectangle(AlternatingCellBrush, null, new Rect(x, HeaderHeight, columnWidth, plotHeight));
            }

            if (SelectedBucketIndex == column && SelectedSeverity is null)
            {
                drawingContext.DrawRoundedRectangle(SelectedCellBrush, new Pen(SelectedBorderBrush, 1.4), new Rect(x + 2, HeaderHeight + 2, Math.Max(1, columnWidth - 4), plotHeight - 4), 8, 8);
            }

            drawingContext.DrawLine(gridPen, new Point(x, HeaderHeight), new Point(x, HeaderHeight + plotHeight));
            DrawText(drawingContext, _buckets[column].Label, 10, LabelBrush, new Point(x, HeaderHeight + plotHeight + 15), columnWidth, TextAlignment.Center, pixelsPerDip, FontWeights.SemiBold);
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
                if (SelectedBucketIndex == column && SelectedSeverity == severity)
                {
                    drawingContext.DrawRoundedRectangle(SelectedCellBrush, new Pen(SelectedBorderBrush, 1.7), new Rect(cell.X + 3, cell.Y + 3, Math.Max(1, cell.Width - 6), Math.Max(1, cell.Height - 6)), 8, 8);
                }
                else if (_hoveredBucketIndex == column && _hoveredSeverity == severity)
                {
                    drawingContext.DrawRoundedRectangle(FrozenBrush("#26FFFFFF"), new Pen(FrozenBrush("#729BC0E3"), 1), new Rect(cell.X + 4, cell.Y + 4, Math.Max(1, cell.Width - 8), Math.Max(1, cell.Height - 8)), 8, 8);
                }

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
            _hoveredSeverity = severity;
            Cursor = Cursors.Hand;
            var bucket = _buckets[bucketIndex];
            ToolTip = severity is null
                ? $"{bucket.Label} · 全部级别 {bucket.TotalCount:N0} 条"
                : $"{bucket.Label} · {SeverityText(severity.Value)} {CountFor(bucket, severity.Value):N0} 条";
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
        var plotHeight = Math.Max(1, ActualHeight - HeaderHeight - FooterHeight);
        if (point.Y >= HeaderHeight + plotHeight)
        {
            return true;
        }

        var row = Math.Clamp((int)((point.Y - HeaderHeight) / (plotHeight / RowSeverities.Length)), 0, RowSeverities.Length - 1);
        severity = RowSeverities[row];
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
            context.DrawEllipse(FrozenBrush("#54B5C9DB"), null, new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2), 2.2, 2.2);
            return;
        }

        var color = SeverityBrush(severity);
        var size = Math.Min(34, Math.Max(25, Math.Min(cell.Width - 14, cell.Height - 25)));
        var marker = new Rect(cell.X + (cell.Width - size) / 2, cell.Y + (cell.Height - size) / 2 - 5, size, size);
        context.DrawRoundedRectangle(MarkerBackgroundBrush, new Pen(color, 1.5), marker, size / 2, size / 2);
        DrawText(context, SeverityGlyph(severity), size * 0.5, color, new Point(marker.X, marker.Y + size * 0.23), marker.Width, TextAlignment.Center, pixelsPerDip, FontWeights.SemiBold, "Segoe Fluent Icons");
        DrawText(context, count.ToString("N0"), 9, color, new Point(cell.X, marker.Bottom + 3), cell.Width, TextAlignment.Center, pixelsPerDip, FontWeights.SemiBold);
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

    private static string SeverityText(SystemDiagnosticSeverity severity) => severity switch
    {
        SystemDiagnosticSeverity.Critical => "关键事件",
        SystemDiagnosticSeverity.Error => "错误事件",
        _ => "警告事件"
    };

    private static string SeverityGlyph(SystemDiagnosticSeverity severity) => severity switch
    {
        SystemDiagnosticSeverity.Critical => "\uEA39",
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
