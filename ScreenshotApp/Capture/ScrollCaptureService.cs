using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 自由长截图会话。由用户手动控制滚动和结束位置。
/// 每一帧都定位到统一的内容坐标，向上回滚或重复经过已有区域不会增加输出高度。
/// </summary>
public sealed class ScrollCaptureService
{
    private const int MaximumContentFrames = 160;
    private const int MaximumAnchors = 72;
    private static readonly TimeSpan ManualSampleDelay = TimeSpan.FromMilliseconds(45);

    private readonly ICaptureBackend _captureBackend;

    private sealed record Anchor(
        BitmapSource Bitmap,
        int ContentOffset,
        CaptureFrameSignature Signature);

    private sealed record LocatedFrame(
        int ContentOffset,
        VerticalOverlapDetector.Match Match,
        bool FromHistory);

    public ScrollCaptureService(ICaptureBackend captureBackend)
    {
        _captureBackend = captureBackend;
    }

    public async Task<ScrollCaptureResult> CaptureInteractiveAsync(
        Int32Rect screenRegion,
        CancellationToken cancellationToken = default)
    {
        if (screenRegion.Width < 80 || screenRegion.Height < 100)
        {
            throw new InvalidOperationException("长截图区域过小，请重新框选可滚动内容区域。");
        }

        NativeMethods.GetCursorPos(out var originalCursor);
        var centerX = screenRegion.X + screenRegion.Width / 2;
        var centerY = screenRegion.Y + screenRegion.Height / 2;
        var frameWindow = new ScrollCaptureFrameWindow(screenRegion);
        var toolbarWindow = new ScrollCaptureToolbarWindow(screenRegion);
        var previewWindow = new ScrollCapturePreviewWindow(screenRegion);
        using var inputDirectionMonitor = new ScrollInputDirectionMonitor();
        var contentFrames = new List<VerticalBitmapStitcher.PositionedFrame>();
        var anchors = new List<Anchor>();
        var fixedRegionEstimator = new FixedViewportRegionEstimator();
        var stopReason = "用户完成采集";
#if DEBUG
        var debugSessionDirectory = Path.Combine(
            @"E:\截影\Diagnostics",
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
        Directory.CreateDirectory(debugSessionDirectory);
        var savedFailurePair = false;
#endif

        try
        {
            if (!NativeMethods.SetCursorPos(centerX, centerY))
            {
                throw new InvalidOperationException("无法将鼠标定位到长截图区域。");
            }

            if (!NativeMethods.ActivateWindowAtPoint(centerX, centerY))
            {
                throw new InvalidOperationException("无法激活框选区域下方的滚动窗口。");
            }

            frameWindow.Show();
            toolbarWindow.Show();
            await Task.Delay(140, cancellationToken);

            var first = await CaptureRegionAsync(screenRegion, cancellationToken);
#if DEBUG
            SaveDebugBitmap(first, Path.Combine(debugSessionDirectory, "000_first.png"));
#endif
            var lastAnchor = CreateAnchor(first, 0);
            anchors.Add(lastAnchor);
            contentFrames.Add(new VerticalBitmapStitcher.PositionedFrame(first, 0));
            previewWindow.UpdatePreview(contentFrames, first.PixelHeight);
            previewWindow.Show();

            var minimumCoveredOffset = 0;
            var maximumCoveredOffset = first.PixelHeight;
            var waitingForOverlapRecovery = false;
            var consecutiveUnreliableFrames = 0;
            var pendingInputDirection = 0;

            toolbarWindow.SetProgress(
                "手动滚动中 · 随时点击完成",
                $"已采集 {first.PixelHeight} 像素 · 可向上回滚且不会重复拼接");

            var nextCaptureTask = CaptureAfterDelayAsync(screenRegion, cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (toolbarWindow.IsCancelRequested || NativeMethods.IsEscapePressed())
                {
                    throw new OperationCanceledException("用户取消了长截图。", cancellationToken);
                }

                if (toolbarWindow.IsFinishRequested)
                {
                    stopReason = "用户完成采集";
                    break;
                }

                var current = await nextCaptureTask;
                // 当前帧交给后台匹配时，下一次屏幕采样已经开始等待，避免把
                // 图像分析耗时叠加到采样间隔上而漏过中间内容。
                nextCaptureTask = CaptureAfterDelayAsync(screenRegion, cancellationToken);
                var sampledInputDirection = inputDirectionMonitor.ConsumeDirection();
                if (sampledInputDirection != 0)
                {
                    pendingInputDirection = sampledInputDirection;
                }

                var located = await Task.Run(
                    () => LocateCurrentFrame(
                        lastAnchor,
                        anchors,
                        current,
                        pendingInputDirection),
                    cancellationToken);

                if (located.Match.IsDuplicate)
                {
                    if (waitingForOverlapRecovery)
                    {
                        waitingForOverlapRecovery = false;
                        consecutiveUnreliableFrames = 0;
                        toolbarWindow.SetProgress(
                            "已重新识别重叠内容",
                            "可以继续缓慢滚动，已截取内容不会重复加入");
                    }

                    if (located.FromHistory)
                    {
                        lastAnchor = CreateAnchor(current, located.ContentOffset);
                        AddAnchor(anchors, lastAnchor);
                        pendingInputDirection = 0;
                        toolbarWindow.SetProgress(
                            "已回到截取过的位置",
                            "当前位置已识别，不会把相同内容再次加入长截图");
                    }

                    continue;
                }

                if (!located.Match.IsReliable)
                {
                    waitingForOverlapRecovery = true;
                    consecutiveUnreliableFrames++;

#if DEBUG
                    if (!savedFailurePair)
                    {
                        SaveDebugBitmap(lastAnchor.Bitmap, Path.Combine(debugSessionDirectory, "failure_previous.png"));
                        SaveDebugBitmap(current, Path.Combine(debugSessionDirectory, "failure_current.png"));
                        File.WriteAllText(
                            Path.Combine(debugSessionDirectory, "failure.txt"),
                            $"方向={pendingInputDirection}{Environment.NewLine}" +
                            $"上次坐标={lastAnchor.ContentOffset}{Environment.NewLine}" +
                            $"检测位移={located.Match.ScrollDelta}{Environment.NewLine}" +
                            $"分数={located.Match.Score:F6}{Environment.NewLine}" +
                            $"边缘一致率={located.Match.MatchedEdgeRatio:P4}{Environment.NewLine}" +
                            $"稳定网格={located.Match.StableCellCount}/{located.Match.EligibleCellCount}{Environment.NewLine}" +
                            $"稳定行={located.Match.StableRowCount}{Environment.NewLine}" +
                            $"变化率={located.Match.ChangedRatio:P4}{Environment.NewLine}",
                            System.Text.Encoding.UTF8);
                        savedFailurePair = true;
                    }

                    var recoveryDetail = $"动态内容较多，正在重新识别 · 匹配 {located.Match.Score:F2} / 边缘 {located.Match.MatchedEdgeRatio:P0} / 稳定区 {located.Match.StableCellCount}";
#else
                    var recoveryDetail = consecutiveUnreliableFrames < 4
                        ? "检测到封面或动画变化，请稍慢滚动，稳定后会自动继续"
                        : "暂未找到足够重叠，请向回滚动一点，识别后会自动恢复";
#endif
                    var status = consecutiveUnreliableFrames < 4
                        ? "正在等待页面内容稳定"
                        : "暂未识别到连续内容";
                    toolbarWindow.SetProgress(status, recoveryDetail, consecutiveUnreliableFrames >= 4);
                    continue;
                }

                waitingForOverlapRecovery = false;
                consecutiveUnreliableFrames = 0;
                var currentOffset = located.ContentOffset;
                var previousAnchor = lastAnchor;
                pendingInputDirection = 0;
                lastAnchor = CreateAnchor(current, currentOffset);
                AddAnchor(anchors, lastAnchor);
                var fixedInsets = await Task.Run(
                    () => fixedRegionEstimator.Observe(
                        previousAnchor.Bitmap,
                        current,
                        currentOffset - previousAnchor.ContentOffset),
                    cancellationToken);

                var frameTop = currentOffset;
                var frameBottom = currentOffset + current.PixelHeight;
                var extendsTop = frameTop < minimumCoveredOffset;
                var extendsBottom = frameBottom > maximumCoveredOffset;

                if (!extendsTop && !extendsBottom)
                {
#if DEBUG
                    var trackingDetail = $"坐标 {currentOffset} · 位移 {located.Match.ScrollDelta} · 匹配 {located.Match.Score:F3} · 边缘 {located.Match.MatchedEdgeRatio:P0}";
#else
                    const string trackingDetail = "当前位置只用于跟踪，不会重复加入最终图片";
#endif
                    toolbarWindow.SetProgress(
                        "正在已截取区域内移动",
                        trackingDetail);
                    continue;
                }

                var projectedMinimum = Math.Min(minimumCoveredOffset, frameTop);
                var projectedMaximum = Math.Max(maximumCoveredOffset, frameBottom);
                if (projectedMaximum - projectedMinimum > VerticalBitmapStitcher.MaximumBitmapHeight)
                {
                    stopReason = "已达到 30000 像素高度上限";
                    break;
                }

                if (contentFrames.Count >= MaximumContentFrames)
                {
                    stopReason = "已达到最大采集帧数";
                    break;
                }

                contentFrames.Add(new VerticalBitmapStitcher.PositionedFrame(current, currentOffset));
                minimumCoveredOffset = projectedMinimum;
                maximumCoveredOffset = projectedMaximum;
                var capturedHeight = maximumCoveredOffset - minimumCoveredOffset;
                previewWindow.UpdatePreview(contentFrames, capturedHeight, fixedInsets);
                var direction = located.Match.ScrollDelta < 0 ? "向上扩展" : "向下扩展";
#if DEBUG
                var capturedDetail = $"{direction} · 已采集 {capturedHeight} 像素 · 坐标 {currentOffset} · 位移 {located.Match.ScrollDelta}";
#else
                var capturedDetail = $"{direction} · 已采集 {capturedHeight} 像素 · {contentFrames.Count} 个有效片段";
#endif
                toolbarWindow.SetProgress(
                    "手动滚动中 · 随时点击完成",
                    capturedDetail);
            }

            // 接缝搜索和最终像素写入可能涉及数十张高分辨率截图，
            // 放到后台线程避免完成长截图时阻塞主界面和剪贴板响应。
            var stitched = await Task.Run(
                () => VerticalBitmapStitcher.StitchPositioned(
                    contentFrames,
                    fixedRegionEstimator.Current),
                cancellationToken);
            return new ScrollCaptureResult(stitched, contentFrames.Count, stopReason);
        }
        finally
        {
            previewWindow.Close();
            toolbarWindow.Close();
            frameWindow.Close();
            _ = NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static LocatedFrame LocateCurrentFrame(
        Anchor lastAnchor,
        IReadOnlyList<Anchor> anchors,
        BitmapSource current,
        int inputDirection)
    {
        var directMatch = VerticalOverlapDetector.FindWithDirection(
            lastAnchor.Bitmap,
            current,
            inputDirection);
        if (directMatch.IsDuplicate || directMatch.IsReliable)
        {
            return new LocatedFrame(
                lastAnchor.ContentOffset + directMatch.ScrollDelta,
                directMatch,
                false);
        }

        // 完整的位移搜索成本较高，逐个扫描几十个历史锚点会让采样停顿数秒，
        // 用户继续滚动时就会产生真正的内容缺口。历史回跳只做低成本的视口
        // 签名识别；普通向上/向下回滚仍由相邻帧精确匹配定位。
        var currentSignature = CaptureFrameSignature.Create(current);
        Anchor? duplicateAnchor = null;
        var bestSignatureDifference = double.MaxValue;
        foreach (var anchor in anchors)
        {
            if (ReferenceEquals(anchor, lastAnchor))
            {
                continue;
            }

            var signatureDifference = anchor.Signature.DifferenceFrom(currentSignature);
            var movementFromLastAnchor = anchor.ContentOffset - lastAnchor.ContentOffset;
            if (inputDirection != 0 &&
                movementFromLastAnchor != 0 &&
                Math.Sign(movementFromLastAnchor) != inputDirection)
            {
                // 历史锚点可能包含高度相似的代码段。候选坐标若与本次真实滚动
                // 方向相反，应视为重复内容造成的误匹配，而不是跳转到错误位置。
                continue;
            }

            if (signatureDifference < bestSignatureDifference)
            {
                bestSignatureDifference = signatureDifference;
                duplicateAnchor = anchor;
            }
        }

        if (duplicateAnchor is not null && bestSignatureDifference < 0.35)
        {
            return new LocatedFrame(
                duplicateAnchor.ContentOffset,
                new VerticalOverlapDetector.Match(
                    0,
                    bestSignatureDifference,
                    true,
                    true,
                    0),
                true);
        }

        return new LocatedFrame(lastAnchor.ContentOffset, directMatch, false);
    }

    private static Anchor CreateAnchor(BitmapSource bitmap, int contentOffset)
    {
        return new Anchor(bitmap, contentOffset, CaptureFrameSignature.Create(bitmap));
    }

    private static void AddAnchor(List<Anchor> anchors, Anchor anchor)
    {
        anchors.Add(anchor);
        if (anchors.Count > MaximumAnchors)
        {
            // 始终保留第一帧和最近帧，便于快速回到开头或从当前位置继续。
            anchors.RemoveAt(1);
        }
    }

    private async Task<BitmapSource> CaptureRegionAsync(Int32Rect screenRegion, CancellationToken cancellationToken)
    {
        // 与桌面合成器同步，减少恰好在滚动动画换帧中间抓取到半帧的概率。
        _ = NativeMethods.DwmFlush();
        var frame = await _captureBackend.CaptureCurrentMonitorAsync(cancellationToken);
        var relativeX = screenRegion.X - frame.ScreenBounds.X;
        var relativeY = screenRegion.Y - frame.ScreenBounds.Y;
        if (relativeX < 0 || relativeY < 0 ||
            relativeX + screenRegion.Width > frame.Bitmap.PixelWidth ||
            relativeY + screenRegion.Height > frame.Bitmap.PixelHeight)
        {
            throw new InvalidOperationException("长截图区域超出了当前显示器范围。");
        }

        var cropped = new CroppedBitmap(
            frame.Bitmap,
            new Int32Rect(relativeX, relativeY, screenRegion.Width, screenRegion.Height));
        cropped.Freeze();
        return cropped;
    }

    private async Task<BitmapSource> CaptureAfterDelayAsync(
        Int32Rect screenRegion,
        CancellationToken cancellationToken)
    {
        await Task.Delay(ManualSampleDelay, cancellationToken);
        return await CaptureRegionAsync(screenRegion, cancellationToken);
    }

#if DEBUG
    private static void SaveDebugBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
#endif
}
