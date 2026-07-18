using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ScreenshotApp.Ocr;

namespace ScreenshotApp.Capture;

public partial class SelectionOverlayWindow : Window
{
    private readonly CaptureFrame _frame;
    private readonly ScreenColorBuffer _colorBuffer;
    private readonly SelectionPurpose _purpose;
    private Point _dragStart;
    private Rect _selection;
    private bool _isDragging;
    private readonly List<ScreenshotAnnotation> _annotations = new();
    private readonly List<UIElement> _annotationVisuals = new();
    private ScreenshotAnnotationTool _activeAnnotationTool;
    private Color _annotationColor = Color.FromRgb(255, 77, 94);
    private double _annotationThickness = 4;
    private bool _isDrawingAnnotation;
    private Point _annotationStart;
    private Polyline? _workingPolyline;
    private Rectangle? _workingRectangle;
    private List<Point>? _workingPoints;
    private bool _ocrInProgress;
    private ResizeHandle? _activeResizeHandle;
    private Rect _resizeStartSelection;
    private Point _resizeStartPoint;

    private const double MinimumSelectionSize = 16;
    private const double ResizeHandleSize = 14;

    public SelectionOverlayWindow(CaptureFrame frame, SelectionPurpose purpose = SelectionPurpose.Screenshot)
    {
        _frame = frame;
        _colorBuffer = new ScreenColorBuffer(frame.Bitmap);
        _purpose = purpose;
        InitializeComponent();
        ScreenshotImage.Source = frame.Bitmap;
        UpdateAnnotationToolStates();
        UpdateColorStates();
        UpdateThicknessStates();

#if DEBUG
        // 调试构建保留任务栏入口，便于自动化检查无边框选择窗口与工具栏。
        ShowInTaskbar = true;
#endif

        if (purpose == SelectionPurpose.ScrollCaptureRegion)
        {
            Title = "选择滚动长截图区域";
            HintText.Text = "框选后松开鼠标即可开始  ·  默认手动滚动  ·  Esc / 右键取消";
        }
    }

    public BitmapSource? SelectedBitmap { get; private set; }

    public Int32Rect? SelectedScreenBounds { get; private set; }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var bounds = _frame.ScreenBounds;
        NativeMethods.SetWindowPos(
            handle,
            new IntPtr(NativeMethods.HwndTopmost),
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            0);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Keyboard.Focus(CaptureSurface);
        UpdateSelectionVisuals(new Rect());
    }

    private void CaptureSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var position = ClampToSurface(e.GetPosition(CaptureSurface));
        if (e.ClickCount == 2 && _selection.Contains(position))
        {
            ConfirmSelection();
            return;
        }

        if (_purpose == SelectionPurpose.Screenshot &&
            !_selection.IsEmpty &&
            _selection.Contains(position))
        {
            // 已完成框选后，选区内部只用于标注，避免普通单击意外重画选区。
            e.Handled = true;
            return;
        }

        _dragStart = position;
        _selection = new Rect(position, position);
        _isDragging = true;
        ResetAnnotations();
        ActionToolbar.Visibility = Visibility.Collapsed;
        CaptureSurface.CaptureMouse();
        UpdateSelectionVisuals(_selection);
        e.Handled = true;
    }

    private void CaptureSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = ClampToSurface(e.GetPosition(CaptureSurface));
        _selection = Normalize(_dragStart, current);
        UpdateSelectionVisuals(_selection);
    }

    private void CaptureSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        CaptureSurface.ReleaseMouseCapture();

        if (_selection.Width < 2 || _selection.Height < 2)
        {
            _selection = Rect.Empty;
            UpdateSelectionVisuals(_selection);
            return;
        }

        if (_purpose == SelectionPurpose.ScrollCaptureRegion)
        {
            // 长截图不再要求二次点击确认：完成框选后立即进入采集会话。
            ConfirmSelection();
            e.Handled = true;
            return;
        }

        ActionToolbar.Visibility = Visibility.Visible;
        AnnotationCanvas.Visibility = Visibility.Visible;
        AnnotationCanvas.Clip = new RectangleGeometry(_selection);
        HintText.Text = "选择标注工具进行涂鸦或描框  ·  Enter 完成  ·  Esc / 右键取消";
        UpdateSelectionVisuals(_selection);
        PositionToolbar();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            UndoButton_Click(UndoButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !_selection.IsEmpty)
        {
            ConfirmSelection();
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void Window_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        CancelSelection();
        e.Handled = true;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void PenToolButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveAnnotationTool(
            _activeAnnotationTool == ScreenshotAnnotationTool.Pen
                ? ScreenshotAnnotationTool.None
                : ScreenshotAnnotationTool.Pen);
    }

    private void RectangleToolButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveAnnotationTool(
            _activeAnnotationTool == ScreenshotAnnotationTool.Rectangle
                ? ScreenshotAnnotationTool.None
                : ScreenshotAnnotationTool.Rectangle);
    }

    private void ColorPickerToolButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveAnnotationTool(
            _activeAnnotationTool == ScreenshotAnnotationTool.ColorPicker
                ? ScreenshotAnnotationTool.None
                : ScreenshotAnnotationTool.ColorPicker);
    }

    private async void OcrToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrInProgress || _selection.IsEmpty)
        {
            return;
        }

        _ocrInProgress = true;
        OcrToolButton.IsEnabled = false;
        OcrToolButtonText.Text = "识别中…";
        var previousHint = HintText.Text;
        HintText.Text = "正在本机识别文字，首次使用需要加载模型…";
        SetActiveAnnotationTool(ScreenshotAnnotationTool.None);
        try
        {
            var bitmap = CreateSelectionBitmap(includeAnnotations: false);
            var image = OcrImage.FromBitmapSource(bitmap);
            var result = await OcrEngineProvider.Default.RecognizeAsync(
                image,
                new OcrOptions
                {
                    Language = "zh-en",
                    ExecutionProvider = OcrExecutionProvider.Cpu,
                    UseOrientationClassification = false,
                    ConfidenceThreshold = 0.5f
                },
                CancellationToken.None);
            var selectionScreenBounds = new Rect(
                Left + _selection.Left,
                Top + _selection.Top,
                _selection.Width,
                _selection.Height);
            var resultWindow = new OcrResultWindow(result, selectionScreenBounds) { Owner = this };
            resultWindow.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"文字识别失败：{exception.Message}",
                "文字提取",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            HintText.Text = previousHint;
            OcrToolButtonText.Text = "提取文字";
            OcrToolButton.IsEnabled = true;
            _ocrInProgress = false;
        }
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        if (button.Tag is SolidColorBrush colorBrush)
        {
            _annotationColor = colorBrush.Color;
        }
        else if (button.Tag is string colorText &&
                 ColorConverter.ConvertFromString(colorText) is Color color)
        {
            _annotationColor = color;
        }
        else
        {
            return;
        }

        UpdateColorStates();
    }

    private void ThicknessButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string thicknessText } ||
            !double.TryParse(thicknessText, out var thickness))
        {
            return;
        }

        _annotationThickness = thickness;
        UpdateThicknessStates();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_annotations.Count == 0 || _annotationVisuals.Count == 0)
        {
            return;
        }

        _annotations.RemoveAt(_annotations.Count - 1);
        var visual = _annotationVisuals[^1];
        _annotationVisuals.RemoveAt(_annotationVisuals.Count - 1);
        AnnotationCanvas.Children.Remove(visual);
        UndoButton.IsEnabled = _annotations.Count > 0;
    }

    private void AnnotationCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_purpose != SelectionPurpose.Screenshot ||
            _selection.IsEmpty ||
            _activeAnnotationTool == ScreenshotAnnotationTool.None)
        {
            return;
        }

        var surfacePoint = ClampToSurface(e.GetPosition(CaptureSurface));
        if (!_selection.Contains(surfacePoint))
        {
            e.Handled = true;
            return;
        }

        if (_activeAnnotationTool == ScreenshotAnnotationTool.ColorPicker)
        {
            var sample = UpdateColorPicker(surfacePoint);
            try
            {
                Clipboard.SetText(sample.Hex);
                ColorPickerHintText.Text = $"已复制 {sample.Hex}";
            }
            catch
            {
                ColorPickerHintText.Text = "剪贴板忙，请再次单击";
            }

            e.Handled = true;
            return;
        }

        _isDrawingAnnotation = true;
        _annotationStart = ToSelectionPoint(surfacePoint);
        var stroke = CreateAnnotationBrush();
        if (_activeAnnotationTool == ScreenshotAnnotationTool.Pen)
        {
            _workingPoints = new List<Point> { _annotationStart };
            _workingPolyline = new Polyline
            {
                Stroke = stroke,
                StrokeThickness = _annotationThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            _workingPolyline.Points.Add(_annotationStart);
            Canvas.SetLeft(_workingPolyline, _selection.Left);
            Canvas.SetTop(_workingPolyline, _selection.Top);
            AnnotationCanvas.Children.Add(_workingPolyline);
        }
        else
        {
            _workingRectangle = new Rectangle
            {
                Stroke = stroke,
                StrokeThickness = _annotationThickness,
                RadiusX = 2,
                RadiusY = 2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_workingRectangle, surfacePoint.X);
            Canvas.SetTop(_workingRectangle, surfacePoint.Y);
            AnnotationCanvas.Children.Add(_workingRectangle);
        }

        AnnotationCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void SelectionResizeHandle_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (_purpose != SelectionPurpose.Screenshot ||
            _selection.IsEmpty ||
            sender is not Thumb { Tag: string tag } ||
            !Enum.TryParse<ResizeHandle>(tag, out var handle))
        {
            return;
        }

        _activeResizeHandle = handle;
        _resizeStartSelection = _selection;
        _resizeStartPoint = Mouse.GetPosition(CaptureSurface);
        SetActiveAnnotationTool(ScreenshotAnnotationTool.None);
        e.Handled = true;
    }

    private void SelectionResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_activeResizeHandle is not ResizeHandle handle)
        {
            return;
        }

        var current = ClampToSurface(Mouse.GetPosition(CaptureSurface));
        var deltaX = current.X - _resizeStartPoint.X;
        var deltaY = current.Y - _resizeStartPoint.Y;
        var left = _resizeStartSelection.Left;
        var top = _resizeStartSelection.Top;
        var right = _resizeStartSelection.Right;
        var bottom = _resizeStartSelection.Bottom;
        var surfaceWidth = CaptureSurface.ActualWidth;
        var surfaceHeight = CaptureSurface.ActualHeight;

        if (handle is ResizeHandle.Left or ResizeHandle.TopLeft or ResizeHandle.BottomLeft)
        {
            left = Math.Clamp(_resizeStartSelection.Left + deltaX, 0, right - MinimumSelectionSize);
        }

        if (handle is ResizeHandle.Right or ResizeHandle.TopRight or ResizeHandle.BottomRight)
        {
            right = Math.Clamp(_resizeStartSelection.Right + deltaX, left + MinimumSelectionSize, surfaceWidth);
        }

        if (handle is ResizeHandle.Top or ResizeHandle.TopLeft or ResizeHandle.TopRight)
        {
            top = Math.Clamp(_resizeStartSelection.Top + deltaY, 0, bottom - MinimumSelectionSize);
        }

        if (handle is ResizeHandle.Bottom or ResizeHandle.BottomLeft or ResizeHandle.BottomRight)
        {
            bottom = Math.Clamp(_resizeStartSelection.Bottom + deltaY, top + MinimumSelectionSize, surfaceHeight);
        }

        ApplyResizedSelection(new Rect(left, top, right - left, bottom - top));
        e.Handled = true;
    }

    private void SelectionResizeHandle_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _activeResizeHandle = null;
        UpdateSelectionVisuals(_selection);
        e.Handled = true;
    }

    private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_activeAnnotationTool == ScreenshotAnnotationTool.ColorPicker)
        {
            var pickerPoint = e.GetPosition(CaptureSurface);
            if (_selection.Contains(pickerPoint))
            {
                UpdateColorPicker(pickerPoint);
                ColorPickerHintText.Text = "单击复制 HEX";
            }
            else
            {
                ColorPickerPanel.Visibility = Visibility.Collapsed;
            }

            e.Handled = true;
            return;
        }

        if (!_isDrawingAnnotation || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var surfacePoint = ConstrainToSelection(e.GetPosition(CaptureSurface));
        var localPoint = ToSelectionPoint(surfacePoint);
        if (_activeAnnotationTool == ScreenshotAnnotationTool.Pen &&
            _workingPolyline is not null &&
            _workingPoints is not null)
        {
            if ((_workingPoints[^1] - localPoint).Length >= 1.2)
            {
                _workingPoints.Add(localPoint);
                _workingPolyline.Points.Add(localPoint);
            }
        }
        else if (_workingRectangle is not null)
        {
            var bounds = Normalize(_annotationStart, localPoint);
            Canvas.SetLeft(_workingRectangle, _selection.Left + bounds.Left);
            Canvas.SetTop(_workingRectangle, _selection.Top + bounds.Top);
            _workingRectangle.Width = bounds.Width;
            _workingRectangle.Height = bounds.Height;
        }

        e.Handled = true;
    }

    private void AnnotationCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawingAnnotation)
        {
            return;
        }

        _isDrawingAnnotation = false;
        AnnotationCanvas.ReleaseMouseCapture();
        if (_workingPolyline is not null && _workingPoints is { Count: >= 2 })
        {
            _annotations.Add(new PenScreenshotAnnotation(
                _workingPoints.ToArray(),
                _annotationColor,
                _annotationThickness));
            _annotationVisuals.Add(_workingPolyline);
        }
        else if (_workingRectangle is not null &&
                 _workingRectangle.Width >= 2 &&
                 _workingRectangle.Height >= 2)
        {
            var bounds = new Rect(
                Canvas.GetLeft(_workingRectangle) - _selection.Left,
                Canvas.GetTop(_workingRectangle) - _selection.Top,
                _workingRectangle.Width,
                _workingRectangle.Height);
            _annotations.Add(new RectangleScreenshotAnnotation(
                bounds,
                _annotationColor,
                _annotationThickness));
            _annotationVisuals.Add(_workingRectangle);
        }
        else
        {
            if (_workingPolyline is not null)
            {
                AnnotationCanvas.Children.Remove(_workingPolyline);
            }

            if (_workingRectangle is not null)
            {
                AnnotationCanvas.Children.Remove(_workingRectangle);
            }
        }

        _workingPolyline = null;
        _workingRectangle = null;
        _workingPoints = null;
        UndoButton.IsEnabled = _annotations.Count > 0;
        e.Handled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelSelection();
    }

    private void ConfirmSelection()
    {
        if (_selection.IsEmpty || CaptureSurface.ActualWidth <= 0 || CaptureSurface.ActualHeight <= 0)
        {
            return;
        }

        if (_purpose == SelectionPurpose.Screenshot)
        {
            SelectedBitmap = CreateSelectionBitmap(includeAnnotations: true);
        }
        else
        {
            var pixelBounds = GetSelectionPixelBounds();
            SelectedScreenBounds = new Int32Rect(
                _frame.ScreenBounds.X + pixelBounds.X,
                _frame.ScreenBounds.Y + pixelBounds.Y,
                pixelBounds.Width,
                pixelBounds.Height);
        }

        DialogResult = true;
        Close();
    }

    private BitmapSource CreateSelectionBitmap(bool includeAnnotations)
    {
        var bounds = GetSelectionPixelBounds();
        var cropped = new CroppedBitmap(_frame.Bitmap, bounds);
        cropped.Freeze();
        if (!includeAnnotations || _annotations.Count == 0)
        {
            return cropped;
        }

        return ScreenshotAnnotationRenderer.Render(
            cropped,
            _annotations,
            new Size(_selection.Width, _selection.Height));
    }

    private Int32Rect GetSelectionPixelBounds()
    {
        var scaleX = _frame.Bitmap.PixelWidth / CaptureSurface.ActualWidth;
        var scaleY = _frame.Bitmap.PixelHeight / CaptureSurface.ActualHeight;
        var x = Math.Clamp((int)Math.Round(_selection.X * scaleX), 0, _frame.Bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Round(_selection.Y * scaleY), 0, _frame.Bitmap.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Round(_selection.Right * scaleX), x + 1, _frame.Bitmap.PixelWidth);
        var bottom = Math.Clamp((int)Math.Round(_selection.Bottom * scaleY), y + 1, _frame.Bitmap.PixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private void CancelSelection()
    {
        DialogResult = false;
        Close();
    }

    private void UpdateSelectionVisuals(Rect selection)
    {
        var width = Math.Max(0, CaptureSurface.ActualWidth);
        var height = Math.Max(0, CaptureSurface.ActualHeight);

        if (selection.IsEmpty || selection.Width < 1 || selection.Height < 1)
        {
            SetCanvasRect(MaskTop, 0, 0, width, height);
            SetCanvasRect(MaskLeft, 0, 0, 0, 0);
            SetCanvasRect(MaskRight, 0, 0, 0, 0);
            SetCanvasRect(MaskBottom, 0, 0, 0, 0);
            SelectionBorder.Visibility = Visibility.Collapsed;
            SelectionResizeLayer.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            ActionToolbar.Visibility = Visibility.Collapsed;
            AnnotationCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        SetCanvasRect(MaskTop, 0, 0, width, selection.Top);
        SetCanvasRect(MaskLeft, 0, selection.Top, selection.Left, selection.Height);
        SetCanvasRect(MaskRight, selection.Right, selection.Top, Math.Max(0, width - selection.Right), selection.Height);
        SetCanvasRect(MaskBottom, 0, selection.Bottom, width, Math.Max(0, height - selection.Bottom));

        SetCanvasRect(SelectionBorder, selection.Left, selection.Top, selection.Width, selection.Height);
        SelectionBorder.Visibility = Visibility.Visible;
        UpdateResizeHandles(selection);

        var pixelWidth = Math.Max(1, (int)Math.Round(selection.Width * _frame.Bitmap.PixelWidth / width));
        var pixelHeight = Math.Max(1, (int)Math.Round(selection.Height * _frame.Bitmap.PixelHeight / height));
        SizeText.Text = $"{pixelWidth} × {pixelHeight}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(SizeBadge, Math.Max(6, selection.Left));
        Canvas.SetTop(SizeBadge, Math.Max(6, selection.Top - SizeBadge.DesiredSize.Height - 7));

        if (_purpose == SelectionPurpose.Screenshot && ActionToolbar.Visibility == Visibility.Visible)
        {
            AnnotationCanvas.Clip = new RectangleGeometry(selection);
            PositionToolbar();
        }
    }

    private void ApplyResizedSelection(Rect resizedSelection)
    {
        if (resizedSelection == _selection)
        {
            return;
        }

        var previousSelection = _selection;
        _selection = resizedSelection;
        TranslateAnnotations(previousSelection, resizedSelection);
        UpdateSelectionVisuals(resizedSelection);
    }

    private void UpdateResizeHandles(Rect selection)
    {
        if (_purpose != SelectionPurpose.Screenshot || _isDragging || selection.IsEmpty)
        {
            SelectionResizeLayer.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionResizeLayer.Visibility = Visibility.Visible;
        var halfHandle = ResizeHandleSize / 2;
        var centerX = selection.Left + selection.Width / 2 - halfHandle;
        var centerY = selection.Top + selection.Height / 2 - halfHandle;
        PlaceResizeHandle(TopLeftResizeHandle, selection.Left - halfHandle, selection.Top - halfHandle);
        PlaceResizeHandle(TopResizeHandle, centerX, selection.Top - halfHandle);
        PlaceResizeHandle(TopRightResizeHandle, selection.Right - halfHandle, selection.Top - halfHandle);
        PlaceResizeHandle(RightResizeHandle, selection.Right - halfHandle, centerY);
        PlaceResizeHandle(BottomRightResizeHandle, selection.Right - halfHandle, selection.Bottom - halfHandle);
        PlaceResizeHandle(BottomResizeHandle, centerX, selection.Bottom - halfHandle);
        PlaceResizeHandle(BottomLeftResizeHandle, selection.Left - halfHandle, selection.Bottom - halfHandle);
        PlaceResizeHandle(LeftResizeHandle, selection.Left - halfHandle, centerY);
    }

    private static void PlaceResizeHandle(Thumb handle, double left, double top)
    {
        Canvas.SetLeft(handle, left);
        Canvas.SetTop(handle, top);
    }

    private void TranslateAnnotations(Rect previousSelection, Rect resizedSelection)
    {
        if (_annotations.Count == 0)
        {
            return;
        }

        var offsetX = previousSelection.Left - resizedSelection.Left;
        var offsetY = previousSelection.Top - resizedSelection.Top;
        if (Math.Abs(offsetX) < 0.01 && Math.Abs(offsetY) < 0.01)
        {
            return;
        }

        for (var index = 0; index < _annotations.Count; index++)
        {
            _annotations[index] = _annotations[index] switch
            {
                PenScreenshotAnnotation pen => pen with
                {
                    Points = pen.Points.Select(point => new Point(point.X + offsetX, point.Y + offsetY)).ToArray()
                },
                RectangleScreenshotAnnotation rectangle => rectangle with
                {
                    Bounds = new Rect(
                        rectangle.Bounds.X + offsetX,
                        rectangle.Bounds.Y + offsetY,
                        rectangle.Bounds.Width,
                        rectangle.Bounds.Height)
                },
                var annotation => annotation
            };
        }
    }

    private void PositionToolbar()
    {
        ActionToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = ActionToolbar.DesiredSize.Width;
        var toolbarHeight = ActionToolbar.DesiredSize.Height;
        var x = Math.Clamp(_selection.Right - toolbarWidth, 8, Math.Max(8, CaptureSurface.ActualWidth - toolbarWidth - 8));
        var preferredBelow = _selection.Bottom + 10;
        var y = preferredBelow + toolbarHeight <= CaptureSurface.ActualHeight - 8
            ? preferredBelow
            : Math.Max(8, _selection.Top - toolbarHeight - 10);

        ActionToolbar.Margin = new Thickness(x, y, 0, 0);
    }

    private Point ClampToSurface(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, Math.Max(0, CaptureSurface.ActualWidth)),
            Math.Clamp(point.Y, 0, Math.Max(0, CaptureSurface.ActualHeight)));
    }

    private static Rect Normalize(Point start, Point end)
    {
        return new Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));
    }

    private enum ResizeHandle
    {
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left
    }

    private void SetActiveAnnotationTool(ScreenshotAnnotationTool tool)
    {
        _activeAnnotationTool = tool;
        AnnotationCanvas.Cursor = tool switch
        {
            ScreenshotAnnotationTool.None => Cursors.Arrow,
            ScreenshotAnnotationTool.ColorPicker => Cursors.None,
            _ => Cursors.Cross
        };
        if (tool == ScreenshotAnnotationTool.ColorPicker)
        {
            Mouse.OverrideCursor = Cursors.None;
        }
        else if (Mouse.OverrideCursor == Cursors.None)
        {
            Mouse.OverrideCursor = null;
        }

        if (tool != ScreenshotAnnotationTool.ColorPicker)
        {
            ColorPickerPanel.Visibility = Visibility.Collapsed;
        }

        UpdateAnnotationToolStates();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Mouse.OverrideCursor == Cursors.None)
        {
            Mouse.OverrideCursor = null;
        }

        base.OnClosed(e);
    }

    private void UpdateAnnotationToolStates()
    {
        SetButtonSelected(PenToolButton, _activeAnnotationTool == ScreenshotAnnotationTool.Pen);
        SetButtonSelected(RectangleToolButton, _activeAnnotationTool == ScreenshotAnnotationTool.Rectangle);
        SetButtonSelected(ColorPickerToolButton, _activeAnnotationTool == ScreenshotAnnotationTool.ColorPicker);
    }

    private ScreenColorSampler.ColorSample UpdateColorPicker(Point surfacePoint)
    {
        var scaleX = _colorBuffer.PixelWidth / CaptureSurface.ActualWidth;
        var scaleY = _colorBuffer.PixelHeight / CaptureSurface.ActualHeight;
        var pixelX = (int)Math.Floor(surfacePoint.X * scaleX);
        var pixelY = (int)Math.Floor(surfacePoint.Y * scaleY);
        var sample = _colorBuffer.Sample(pixelX, pixelY);

        ColorPickerSwatch.Background = new SolidColorBrush(sample.ToMediaColor());
        ColorHexText.Text = sample.Hex;
        ColorRgbText.Text = sample.Rgb;
        ColorPixelMagnifier.SetPixels(_colorBuffer.CreateNeighborhood(pixelX, pixelY, 5));
        ColorPickerPanel.Visibility = Visibility.Visible;
        PositionColorPickerPanel(surfacePoint);
        return sample;
    }

    private void PositionColorPickerPanel(Point surfacePoint)
    {
        ColorPickerPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var panelWidth = ColorPickerPanel.DesiredSize.Width;
        var panelHeight = ColorPickerPanel.DesiredSize.Height;
        var circleCenterOffset = 70d;
        var x = surfacePoint.X - panelWidth / 2;
        var y = surfacePoint.Y - circleCenterOffset;
        x = Math.Clamp(x, 8, Math.Max(8, CaptureSurface.ActualWidth - panelWidth - 8));
        y = Math.Clamp(y, 8, Math.Max(8, CaptureSurface.ActualHeight - panelHeight - 8));

        Canvas.SetLeft(ColorPickerPanel, x);
        Canvas.SetTop(ColorPickerPanel, y);
    }

    private void UpdateColorStates()
    {
        SetColorSelected(RedColorButton, _annotationColor == Color.FromRgb(255, 77, 94));
        SetColorSelected(BlueColorButton, _annotationColor == Color.FromRgb(62, 139, 255));
        SetColorSelected(YellowColorButton, _annotationColor == Color.FromRgb(255, 200, 71));
        SetColorSelected(WhiteColorButton, _annotationColor == Colors.White);
    }

    private void UpdateThicknessStates()
    {
        SetButtonSelected(ThinButton, Math.Abs(_annotationThickness - 2) < 0.1);
        SetButtonSelected(MediumButton, Math.Abs(_annotationThickness - 4) < 0.1);
        SetButtonSelected(ThickButton, Math.Abs(_annotationThickness - 8) < 0.1);
    }

    private void ResetAnnotations()
    {
        _annotations.Clear();
        _annotationVisuals.Clear();
        AnnotationCanvas.Children.Clear();
        AnnotationCanvas.Clip = null;
        AnnotationCanvas.Visibility = Visibility.Collapsed;
        _workingPolyline = null;
        _workingRectangle = null;
        _workingPoints = null;
        _isDrawingAnnotation = false;
        UndoButton.IsEnabled = false;
        SetActiveAnnotationTool(ScreenshotAnnotationTool.None);
    }

    private Point ToSelectionPoint(Point surfacePoint)
    {
        return new Point(
            surfacePoint.X - _selection.Left,
            surfacePoint.Y - _selection.Top);
    }

    private Point ConstrainToSelection(Point point)
    {
        return new Point(
            Math.Clamp(point.X, _selection.Left, _selection.Right),
            Math.Clamp(point.Y, _selection.Top, _selection.Bottom));
    }

    private SolidColorBrush CreateAnnotationBrush()
    {
        var brush = new SolidColorBrush(_annotationColor);
        brush.Freeze();
        return brush;
    }

    private static void SetButtonSelected(Button button, bool selected)
    {
        button.Background = selected
            ? new SolidColorBrush(Color.FromRgb(218, 234, 255))
            : Brushes.Transparent;
        button.BorderBrush = selected
            ? new SolidColorBrush(Color.FromRgb(76, 139, 235))
            : Brushes.Transparent;
        button.Foreground = selected
            ? new SolidColorBrush(Color.FromRgb(28, 99, 196))
            : new SolidColorBrush(Color.FromRgb(39, 49, 61));
    }

    private static void SetColorSelected(Button button, bool selected)
    {
        button.Background = selected
            ? new SolidColorBrush(Color.FromRgb(220, 234, 255))
            : Brushes.Transparent;
        button.BorderBrush = selected
            ? new SolidColorBrush(Color.FromRgb(31, 104, 213))
            : new SolidColorBrush(Color.FromRgb(124, 136, 151));
    }

    private static void SetCanvasRect(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
            {
                return true;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
