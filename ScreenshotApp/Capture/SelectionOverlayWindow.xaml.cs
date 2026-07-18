using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ScreenshotApp.Ocr;
using ScreenshotApp.Translation;
using ScreenshotApp.History;
using ScreenshotApp.Sticker;

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
    private Shape? _workingShape;
    private Path? _workingLine;
    private AnnotationShape _selectedShape = AnnotationShape.Rectangle;
    private List<Point>? _workingPoints;
    private bool _ocrInProgress;
    private bool _translationInProgress;
    private ResizeHandle? _activeResizeHandle;
    private Rect _resizeStartSelection;
    private Point _resizeStartPoint;
    private bool? _toolbarBelowSelection;
    private bool _toolbarPositioned;
    private bool _recordSystemAudio;
    private bool _recordMicrophone;
    private bool _isSynchronizingAnnotationThickness;

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

    /// <summary>
    /// 普通截图工具栏中选择“长截图”后为 true。选区本身直接复用，
    /// 主窗口据此切换到滚动采集会话，不会要求用户再次框选。
    /// </summary>
    public bool IsScrollCaptureRequested { get; private set; }

    public bool IsScreenRecordingRequested { get; private set; }

    public bool RecordSystemAudio { get; private set; }

    public bool RecordMicrophone { get; private set; }

    /// <summary>
    /// OCR、翻译完成后把文本交由主窗口写入统一历史记录。
    /// </summary>
    public event EventHandler<HistoryTextContent>? HistoryTextCreated;

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
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject))
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
        _toolbarBelowSelection = null;
        _toolbarPositioned = false;
        ResetAnnotations();
        ActionToolbar.Visibility = Visibility.Collapsed;
        RecordingOptionsPanel.Visibility = Visibility.Collapsed;
        HideToolPanels();
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

    private void LongCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.IsEmpty)
        {
            return;
        }

        SetActiveAnnotationTool(ScreenshotAnnotationTool.None);
        ConfirmSelection(useForScrollCapture: true);
    }

    private void RecordingToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.IsEmpty)
        {
            return;
        }

        if (RecordingOptionsPanel.Visibility == Visibility.Visible)
        {
            RecordingOptionsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateRecordingOptionStates();
        RecordingOptionsPanel.Visibility = Visibility.Visible;
        PositionRecordingOptionsPanel();
    }

    private void StickerToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.IsEmpty)
        {
            return;
        }

        // 先冻结选区及标注，再关闭遮罩，避免贴图重新截取屏幕时把自身或遮罩带入图片。
        var bitmap = CreateSelectionBitmap(includeAnnotations: true);
        var pixelBounds = GetSelectionPixelBounds();
        var screenBounds = new Int32Rect(
            _frame.ScreenBounds.X + pixelBounds.X,
            _frame.ScreenBounds.Y + pixelBounds.Y,
            pixelBounds.Width,
            pixelBounds.Height);
        var sticker = new StickerWindow(bitmap, screenBounds);

        Close();
        Dispatcher.BeginInvoke(new Action(sticker.Show));
    }

    private void SystemAudioOptionButton_Click(object sender, RoutedEventArgs e)
    {
        _recordSystemAudio = !_recordSystemAudio;
        UpdateRecordingOptionStates();
    }

    private void MicrophoneOptionButton_Click(object sender, RoutedEventArgs e)
    {
        _recordMicrophone = !_recordMicrophone;
        UpdateRecordingOptionStates();
    }

    private void StartRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.IsEmpty)
        {
            return;
        }

        RecordSystemAudio = _recordSystemAudio;
        RecordMicrophone = _recordMicrophone;
        IsScreenRecordingRequested = true;
        SetActiveAnnotationTool(ScreenshotAnnotationTool.None);
        ConfirmSelection(useForScreenRecording: true);
    }

    private void UpdateRecordingOptionStates()
    {
        SystemAudioOptionText.Text = _recordSystemAudio ? "电脑声音：开" : "电脑声音：关";
        MicrophoneOptionText.Text = _recordMicrophone ? "麦克风：开" : "麦克风：关";
        SystemAudioOptionButton.Background = _recordSystemAudio ? new SolidColorBrush(Color.FromRgb(224, 238, 255)) : new SolidColorBrush(Color.FromRgb(247, 250, 253));
        SystemAudioOptionButton.BorderBrush = _recordSystemAudio ? new SolidColorBrush(Color.FromRgb(88, 149, 255)) : new SolidColorBrush(Color.FromRgb(215, 225, 236));
        MicrophoneOptionButton.Background = _recordMicrophone ? new SolidColorBrush(Color.FromRgb(224, 238, 255)) : new SolidColorBrush(Color.FromRgb(247, 250, 253));
        MicrophoneOptionButton.BorderBrush = _recordMicrophone ? new SolidColorBrush(Color.FromRgb(88, 149, 255)) : new SolidColorBrush(Color.FromRgb(215, 225, 236));
    }

    private void PenToolButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveAnnotationTool(ScreenshotAnnotationTool.Pen);
        ToggleToolPanel(PenOptionsPanel, PenToolButton);
    }

    private void ShapeToolButton_Click(object sender, RoutedEventArgs e)
    {
        PenOptionsPanel.Visibility = Visibility.Collapsed;
        ToggleToolPanel(ShapeOptionsPanel, ShapeToolButton);
    }

    private void ShapeOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string shapeText } || !Enum.TryParse<AnnotationShape>(shapeText, out var shape))
        {
            return;
        }

        _selectedShape = shape;
        SetActiveAnnotationTool(ScreenshotAnnotationTool.Shape);
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
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                HistoryTextCreated?.Invoke(this, new HistoryTextContent(HistoryEntryKind.TextExtraction, result.Text));
            }

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

    private async void TranslationToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_translationInProgress || _selection.IsEmpty)
        {
            return;
        }

        _translationInProgress = true;
        TranslationToolButton.IsEnabled = false;
        TranslationToolButtonText.Text = "翻译中…";
        var previousHint = HintText.Text;
        HintText.Text = "正在本机识别英文并准备离线翻译…";
        SetActiveAnnotationTool(ScreenshotAnnotationTool.None);
        try
        {
            var bitmap = CreateSelectionBitmap(includeAnnotations: false);
            var ocrResult = await OcrEngineProvider.Default.RecognizeAsync(
                OcrImage.FromBitmapSource(bitmap),
                new OcrOptions
                {
                    Language = "zh-en",
                    ExecutionProvider = OcrExecutionProvider.Cpu,
                    UseOrientationClassification = false,
                    ConfidenceThreshold = 0.5f
                },
                CancellationToken.None);

            var sourceText = TranslationTextPreprocessor.Normalize(ocrResult.Text);
            if (!TranslationTextPreprocessor.ContainsEnglish(sourceText))
            {
                MessageBox.Show(
                    this,
                    "框选区域中没有识别到可翻译的英文文字。",
                    "离线翻译",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var selectionScreenBounds = new Rect(
                Left + _selection.Left,
                Top + _selection.Top,
                _selection.Width,
                _selection.Height);
            var engine = TranslationEngineProvider.Default;
            if (!engine.IsReady)
            {
                new TranslationResultWindow(
                    sourceText,
                    null,
                    engine.UnavailableReason,
                    null,
                    selectionScreenBounds) { Owner = this }.ShowDialog();
                return;
            }

            HintText.Text = "正在本机将英文翻译为中文…";
            var translation = await engine.TranslateAsync(
                new TranslationRequest(sourceText),
                CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(translation.TranslatedText))
            {
                var historyContent = $"英文原文{Environment.NewLine}{translation.SourceText}{Environment.NewLine}{Environment.NewLine}中文译文{Environment.NewLine}{translation.TranslatedText}";
                HistoryTextCreated?.Invoke(this, new HistoryTextContent(HistoryEntryKind.Translation, historyContent));
            }

            new TranslationResultWindow(
                translation.SourceText,
                translation.TranslatedText,
                null,
                editedSource => engine.TranslateAsync(
                    new TranslationRequest(TranslationTextPreprocessor.Normalize(editedSource)),
                    CancellationToken.None),
                selectionScreenBounds) { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"离线翻译失败：{exception.Message}",
                "离线翻译",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            HintText.Text = previousHint;
            TranslationToolButtonText.Text = "翻译";
            TranslationToolButton.IsEnabled = true;
            _translationInProgress = false;
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

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThicknessSlider is null || _isSynchronizingAnnotationThickness)
        {
            return;
        }

        SetAnnotationThickness(ThicknessSlider.Value);
    }

    private void ShapeThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ShapeThicknessSlider is null || _isSynchronizingAnnotationThickness)
        {
            return;
        }

        SetAnnotationThickness(ShapeThicknessSlider.Value);
    }

    private void ToggleToolPanel(Border panel, FrameworkElement anchor)
    {
        var shouldShow = panel.Visibility != Visibility.Visible;
        if (panel == PenOptionsPanel)
        {
            ShapeOptionsPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            PenOptionsPanel.Visibility = Visibility.Collapsed;
            ShapeOptionsPanel.Visibility = Visibility.Collapsed;
        }
        if (!shouldShow)
        {
            return;
        }

        panel.Visibility = Visibility.Visible;
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var point = anchor.TransformToAncestor(CaptureSurface).Transform(new Point(0, 0));
        var x = Math.Clamp(point.X, 8, Math.Max(8, CaptureSurface.ActualWidth - panel.DesiredSize.Width - 8));
        var y = point.Y + anchor.ActualHeight + 8;
        if (y + panel.DesiredSize.Height > CaptureSurface.ActualHeight - 8)
        {
            y = Math.Max(8, point.Y - panel.DesiredSize.Height - 8);
        }

        panel.Margin = new Thickness(x, y, 0, 0);
    }

    private void HideToolPanels()
    {
        PenOptionsPanel.Visibility = Visibility.Collapsed;
        ShapeOptionsPanel.Visibility = Visibility.Collapsed;
    }

    private Shape CreateWorkingShape(Brush stroke)
    {
        Shape shape = _selectedShape switch
        {
            AnnotationShape.Ellipse => new Ellipse(),
            AnnotationShape.Diamond => new Polygon(),
            AnnotationShape.Triangle => new Polygon(),
            _ => new Rectangle { RadiusX = 2, RadiusY = 2 }
        };
        shape.Stroke = stroke;
        shape.StrokeThickness = _annotationThickness;
        shape.StrokeLineJoin = PenLineJoin.Round;
        shape.IsHitTestVisible = false;
        return shape;
    }

    private void UpdateWorkingShapeBounds(Shape shape, Rect bounds)
    {
        shape.Width = bounds.Width;
        shape.Height = bounds.Height;
        if (shape is not Polygon polygon)
        {
            return;
        }

        polygon.Points = _selectedShape == AnnotationShape.Diamond
            ? new PointCollection
            {
                new Point(bounds.Width / 2, 0), new Point(bounds.Width, bounds.Height / 2),
                new Point(bounds.Width / 2, bounds.Height), new Point(0, bounds.Height / 2)
            }
            : new PointCollection
            {
                new Point(bounds.Width / 2, 0), new Point(bounds.Width, bounds.Height), new Point(0, bounds.Height)
            };
    }

    private void UpdateWorkingLine(Point surfacePoint)
    {
        if (_workingLine is null)
        {
            return;
        }

        var start = new Point(_selection.Left + _annotationStart.X, _selection.Top + _annotationStart.Y);
        var end = surfacePoint;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.LineTo(end, true, false);
            if (_selectedShape == AnnotationShape.Arrow)
            {
                var vector = start - end;
                if (vector.Length >= 1)
                {
                    vector.Normalize();
                    var left = end + RotateVector(vector, 28) * Math.Max(16, _annotationThickness * 4.5);
                    var right = end + RotateVector(vector, -28) * Math.Max(16, _annotationThickness * 4.5);
                    context.BeginFigure(end, false, false);
                    context.LineTo(left, true, false);
                    context.BeginFigure(end, false, false);
                    context.LineTo(right, true, false);
                }
            }
        }

        geometry.Freeze();
        _workingLine.Data = geometry;
    }

    private static Vector RotateVector(Vector vector, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Vector(vector.X * Math.Cos(radians) - vector.Y * Math.Sin(radians), vector.X * Math.Sin(radians) + vector.Y * Math.Cos(radians));
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
        else if (_selectedShape is AnnotationShape.Arrow or AnnotationShape.Line)
        {
            _workingLine = new Path
            {
                Stroke = stroke,
                StrokeThickness = _annotationThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            AnnotationCanvas.Children.Add(_workingLine);
        }
        else
        {
            _workingShape = CreateWorkingShape(stroke);
            Canvas.SetLeft(_workingShape, surfacePoint.X);
            Canvas.SetTop(_workingShape, surfacePoint.Y);
            AnnotationCanvas.Children.Add(_workingShape);
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
        else if (_workingLine is not null)
        {
            UpdateWorkingLine(surfacePoint);
        }
        else if (_workingShape is not null)
        {
            var bounds = Normalize(_annotationStart, localPoint);
            Canvas.SetLeft(_workingShape, _selection.Left + bounds.Left);
            Canvas.SetTop(_workingShape, _selection.Top + bounds.Top);
            UpdateWorkingShapeBounds(_workingShape, bounds);
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
        else if (_workingLine is not null &&
                 (_annotationStart - ToSelectionPoint(ConstrainToSelection(e.GetPosition(CaptureSurface)))).Length >= 2)
        {
            var end = ToSelectionPoint(ConstrainToSelection(e.GetPosition(CaptureSurface)));
            _annotations.Add(new LineScreenshotAnnotation(_selectedShape, _annotationStart, end, _annotationColor, _annotationThickness));
            _annotationVisuals.Add(_workingLine);
        }
        else if (_workingShape is not null &&
                 _workingShape.Width >= 2 &&
                 _workingShape.Height >= 2)
        {
            var bounds = new Rect(
                Canvas.GetLeft(_workingShape) - _selection.Left,
                Canvas.GetTop(_workingShape) - _selection.Top,
                _workingShape.Width,
                _workingShape.Height);
            _annotations.Add(new ShapeScreenshotAnnotation(
                _selectedShape,
                bounds,
                _annotationColor,
                _annotationThickness));
            _annotationVisuals.Add(_workingShape);
        }
        else
        {
            if (_workingPolyline is not null)
            {
                AnnotationCanvas.Children.Remove(_workingPolyline);
            }

            if (_workingShape is not null)
            {
                AnnotationCanvas.Children.Remove(_workingShape);
            }
            if (_workingLine is not null)
            {
                AnnotationCanvas.Children.Remove(_workingLine);
            }
        }

        _workingPolyline = null;
        _workingShape = null;
        _workingLine = null;
        _workingPoints = null;
        UndoButton.IsEnabled = _annotations.Count > 0;
        e.Handled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelSelection();
    }

    private void ConfirmSelection(bool useForScrollCapture = false, bool useForScreenRecording = false)
    {
        if (_selection.IsEmpty || CaptureSurface.ActualWidth <= 0 || CaptureSurface.ActualHeight <= 0)
        {
            return;
        }

        if (_purpose == SelectionPurpose.Screenshot && !useForScrollCapture && !useForScreenRecording)
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
            IsScrollCaptureRequested = _purpose == SelectionPurpose.ScrollCaptureRegion || useForScrollCapture;
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
        HideToolPanels();
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
            RecordingOptionsPanel.Visibility = Visibility.Collapsed;
            HideToolPanels();
            AnnotationCanvas.Visibility = Visibility.Collapsed;
            _toolbarBelowSelection = null;
            _toolbarPositioned = false;
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

        if (_purpose == SelectionPurpose.Screenshot &&
            ActionToolbar.Visibility == Visibility.Visible)
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
                ShapeScreenshotAnnotation shape => shape with
                {
                    Bounds = new Rect(
                        shape.Bounds.X + offsetX,
                        shape.Bounds.Y + offsetY,
                        shape.Bounds.Width,
                        shape.Bounds.Height)
                },
                LineScreenshotAnnotation line => line with
                {
                    Start = new Point(line.Start.X + offsetX, line.Start.Y + offsetY),
                    End = new Point(line.End.X + offsetX, line.End.Y + offsetY)
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
        if (_toolbarPositioned && !ToolbarIntersectsSelection(toolbarWidth, toolbarHeight))
        {
            return;
        }

        var x = Math.Clamp(_selection.Right - toolbarWidth, 8, Math.Max(8, CaptureSurface.ActualWidth - toolbarWidth - 8));
        var preferredBelow = _selection.Bottom + 10;
        var preferredAbove = _selection.Top - toolbarHeight - 10;
        var surfaceHeight = CaptureSurface.ActualHeight;

        // 常规情况下与选区保留 10 像素间距；向下扩展选区后，
        // 若下方空间不足，允许紧贴上边放置，绝不能再把工具栏夹回选区内部。
        var belowY = preferredBelow + toolbarHeight <= surfaceHeight - 8
            ? preferredBelow
            : _selection.Bottom + toolbarHeight <= surfaceHeight
                ? _selection.Bottom
                : double.NaN;
        var aboveY = preferredAbove >= 8
            ? preferredAbove
            : _selection.Top >= toolbarHeight
                ? _selection.Top - toolbarHeight
                : double.NaN;
        var canPlaceBelow = !double.IsNaN(belowY);
        var canPlaceAbove = !double.IsNaN(aboveY);

        // 首次确定位置后保持在同一侧，避免拖动选区经过临界点时反复跳动。
        _toolbarBelowSelection ??= canPlaceBelow || !canPlaceAbove;
        if (_toolbarBelowSelection == true && !canPlaceBelow && canPlaceAbove)
        {
            _toolbarBelowSelection = false;
        }
        else if (_toolbarBelowSelection == false && !canPlaceAbove && canPlaceBelow)
        {
            _toolbarBelowSelection = true;
        }

        double y;
        if (_toolbarBelowSelection == true && canPlaceBelow)
        {
            y = belowY;
        }
        else if (_toolbarBelowSelection == false && canPlaceAbove)
        {
            y = aboveY;
        }
        else
        {
            // 选区占满几乎整个屏幕时上下两侧都无法完整容纳工具栏，
            // 选择与选区交叠面积更小的一侧作为最后兜底。
            var maximumY = Math.Max(0, surfaceHeight - toolbarHeight);
            var fallbackBelow = Math.Clamp(_selection.Bottom, 0, maximumY);
            var fallbackAbove = Math.Clamp(_selection.Top - toolbarHeight, 0, maximumY);
            y = GetToolbarSelectionIntersectionArea(x, fallbackBelow, toolbarWidth, toolbarHeight)
                <= GetToolbarSelectionIntersectionArea(x, fallbackAbove, toolbarWidth, toolbarHeight)
                ? fallbackBelow
                : fallbackAbove;
        }

        ActionToolbar.Margin = new Thickness(x, y, 0, 0);
        if (RecordingOptionsPanel.Visibility == Visibility.Visible)
        {
            PositionRecordingOptionsPanel();
        }
        _toolbarPositioned = true;
    }

    private void PositionRecordingOptionsPanel()
    {
        RecordingOptionsPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ActionToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var panelWidth = RecordingOptionsPanel.DesiredSize.Width;
        var panelHeight = RecordingOptionsPanel.DesiredSize.Height;
        var toolbarTop = ActionToolbar.Margin.Top;
        var toolbarHeight = ActionToolbar.DesiredSize.Height;
        var x = Math.Clamp(ActionToolbar.Margin.Left, 8, Math.Max(8, CaptureSurface.ActualWidth - panelWidth - 8));
        var belowY = toolbarTop + toolbarHeight + 8;
        var aboveY = toolbarTop - panelHeight - 8;
        var panelRect = new Rect(x, belowY, panelWidth, panelHeight);

        // 默认在工具栏下方；若会遮挡选区或超出屏幕，才切换到工具栏上方。
        var y = belowY;
        if (belowY + panelHeight > CaptureSurface.ActualHeight - 8 || panelRect.IntersectsWith(_selection))
        {
            y = aboveY >= 8 ? aboveY : Math.Clamp(belowY, 8, Math.Max(8, CaptureSurface.ActualHeight - panelHeight - 8));
        }

        RecordingOptionsPanel.Margin = new Thickness(x, y, 0, 0);
    }

    private bool ToolbarIntersectsSelection(double toolbarWidth, double toolbarHeight)
    {
        var toolbarBounds = new Rect(
            ActionToolbar.Margin.Left,
            ActionToolbar.Margin.Top,
            toolbarWidth,
            toolbarHeight);
        var protectedSelection = _selection;
        protectedSelection.Inflate(4, 4);
        return toolbarBounds.IntersectsWith(protectedSelection);
    }

    private double GetToolbarSelectionIntersectionArea(double x, double y, double width, double height)
    {
        var intersection = Rect.Intersect(new Rect(x, y, width, height), _selection);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
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
        SetButtonSelected(ShapeToolButton, _activeAnnotationTool == ScreenshotAnnotationTool.Shape);
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
        if (StrokePreviewPath is not null)
        {
            StrokePreviewPath.Stroke = new SolidColorBrush(_annotationColor);
        }
        if (ShapeStrokePreviewPath is not null)
        {
            ShapeStrokePreviewPath.Stroke = new SolidColorBrush(_annotationColor);
        }
    }

    private void SetAnnotationThickness(double thickness)
    {
        _annotationThickness = thickness;
        _isSynchronizingAnnotationThickness = true;
        UpdateThicknessStates();
        _isSynchronizingAnnotationThickness = false;
    }

    private void UpdateThicknessStates()
    {
        if (ThicknessSlider is not null && Math.Abs(ThicknessSlider.Value - _annotationThickness) > 0.01)
        {
            ThicknessSlider.Value = _annotationThickness;
        }
        if (ShapeThicknessSlider is not null && Math.Abs(ShapeThicknessSlider.Value - _annotationThickness) > 0.01)
        {
            ShapeThicknessSlider.Value = _annotationThickness;
        }
        if (StrokePreviewPath is not null)
        {
            StrokePreviewPath.StrokeThickness = _annotationThickness;
        }
        if (ShapeStrokePreviewPath is not null)
        {
            ShapeStrokePreviewPath.StrokeThickness = _annotationThickness;
        }
    }

    private void ResetAnnotations()
    {
        _annotations.Clear();
        _annotationVisuals.Clear();
        AnnotationCanvas.Children.Clear();
        AnnotationCanvas.Clip = null;
        AnnotationCanvas.Visibility = Visibility.Collapsed;
        _workingPolyline = null;
        _workingShape = null;
        _workingLine = null;
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

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button or Thumb)
            {
                return true;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
