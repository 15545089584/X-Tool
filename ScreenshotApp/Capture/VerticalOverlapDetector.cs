using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// 通过比较两帧中央内容的亮度边缘，估算页面向上滚动的像素距离。
/// 左右边缘会被忽略，减少滚动条和窗口阴影对匹配的影响。
/// </summary>
internal static class VerticalOverlapDetector
{
    private sealed record ShiftEvaluation(
        double Score,
        double MatchedEdgeRatio,
        int EdgeSampleCount,
        int StableCellCount,
        int EligibleCellCount,
        int StableRowCount);

    internal sealed record Match(
        int ScrollDelta,
        double Score,
        bool IsReliable,
        bool IsDuplicate,
        double ChangedRatio = 1,
        double MatchedEdgeRatio = 0,
        int EdgeSampleCount = 0,
        int StableCellCount = 0,
        int EligibleCellCount = 0,
        int StableRowCount = 0);

    /// <summary>
    /// 同时识别向下和向上滚动。正数表示当前视口位于参考帧下方，负数表示位于上方。
    /// </summary>
    internal static Match FindBidirectional(BitmapSource reference, BitmapSource current)
    {
        var downward = Find(reference, current);
        if (downward.IsDuplicate)
        {
            return downward;
        }

        var upward = Find(current, reference);
        // 暗色代码编辑器、表格以及重复段落中，上下两个方向偶尔都会得到
        // “看起来不错”的匹配。若两者分数过于接近，不能仅凭略小的分数猜方向，
        // 否则一次向上回滚就可能被追加到图片底部，造成行号倒序。
        if (downward.IsReliable && upward.IsReliable)
        {
            var bestScore = Math.Min(downward.Score, upward.Score);
            var scoreGap = Math.Abs(downward.Score - upward.Score);
            var ambiguityThreshold = Math.Max(0.40, bestScore * 0.35);
            if (scoreGap < ambiguityThreshold)
            {
                return downward.Score <= upward.Score
                    ? downward with { IsReliable = false }
                    : upward with { ScrollDelta = -upward.ScrollDelta, IsReliable = false };
            }
        }

        if (downward.IsReliable && (!upward.IsReliable || downward.Score <= upward.Score))
        {
            return downward;
        }

        if (upward.IsReliable)
        {
            return upward with { ScrollDelta = -upward.ScrollDelta };
        }

        return downward.Score <= upward.Score
            ? downward with { IsReliable = false }
            : upward with { ScrollDelta = -upward.ScrollDelta, IsReliable = false };
    }

    /// <summary>
    /// 按用户实际输入的滚动方向匹配。direction 大于 0 表示向页面下方滚动，
    /// 小于 0 表示向页面上方滚动；为 0 时才回退到纯图像双向判断。
    /// </summary>
    internal static Match FindWithDirection(BitmapSource reference, BitmapSource current, int direction)
    {
        if (direction > 0)
        {
            return Find(reference, current);
        }

        if (direction < 0)
        {
            var upward = Find(current, reference);
            return upward with { ScrollDelta = -upward.ScrollDelta };
        }

        return FindBidirectional(reference, current);
    }

    internal static Match Find(BitmapSource previous, BitmapSource current)
    {
        if (previous.PixelWidth != current.PixelWidth || previous.PixelHeight != current.PixelHeight)
        {
            return new Match(0, double.MaxValue, false, false);
        }

        var previousPixels = CopyBgraPixels(previous);
        var currentPixels = CopyBgraPixels(current);
        var width = previous.PixelWidth;
        var height = previous.PixelHeight;
        var stride = width * 4;

        var directComparison = CalculateDirectDifference(previousPixels, currentPixels, width, height, stride);
        // 鼠标光晕、文本光标闪烁、IDEA 悬浮提示消失等局部瞬态变化，并不代表
        // 页面真的滚动了。若只接受逐像素完全相同，候选搜索会在大面积暗色背景中
        // 找到一个接近整页高度的伪位移，随后错误提示“滚动过快”。
        //
        // 这里同时约束全帧平均色差和变化像素占比：局部干扰会被当作重复帧跳过，
        // 而真实的一行滚动会让正文中的边缘在大范围内移动，不会落入此阈值。
        var isSmallTransient = directComparison.MeanDifference < 1.25 &&
                               directComparison.ChangedRatio < 0.02;
        // 网页表格的 :hover 高亮、编辑器当前行底色等干扰可能横跨整行，
        // 变化像素占比会明显超过鼠标光晕，但仍只集中在一个较窄的水平带内。
        // 真实纵向滚动会让差异贯穿视口大部分高度，因此可用垂直跨度安全区分。
        var isHorizontalBandTransient = directComparison.MeanDifference < 2.5 &&
                                        directComparison.ChangedRatio < 0.12 &&
                                        directComparison.ChangedVerticalSpanRatio < 0.25;
        var isNearDuplicate = isSmallTransient || isHorizontalBandTransient;
        if (isNearDuplicate)
        {
            return new Match(0, directComparison.MeanDifference, true, true, directComparison.ChangedRatio);
        }

        // 慢速手动滚动经常一次只移动一行（十几到二十像素）。下限过大时，
        // 真实位移会被排除，算法反而可能在重复段落中命中一个很远的假位置。
        // 触控板和平滑滚轮在高频采样时可能只移动 1～2 像素。
        // 从 3 像素起搜会跳过真实候选，并可能在周期性列表里命中远处假接缝。
        var minimumShift = 1;
        // PageDown 一类操作通常会移动接近一整页。保留至少约 10% 的重叠区域，
        // 既允许较快滚动，也避免在完全没有共同内容时盲目拼接。
        var maximumShift = Math.Min(height - 48, (int)Math.Round(height * 0.90));
        if (maximumShift <= minimumShift)
        {
            return new Match(0, double.MaxValue, false, false);
        }

        var coarseStep = Math.Max(2, height / 140);
        var candidates = new List<(int Shift, ShiftEvaluation Evaluation)>();
        for (var shift = minimumShift; shift <= maximumShift; shift += coarseStep)
        {
            candidates.Add((shift, CalculateShiftScore(
                previousPixels,
                currentPixels,
                width,
                height,
                stride,
                shift)));
        }

        // 不能只细化一个粗略最优点。粗采样步长可能刚好跳过真实整数位移，
        // 而代码行、表格行和网页卡片的周期性会在远处产生另一个低分候选。
        // 同时细化多个局部低点，才能找回被粗步长跨过的真实匹配。
        var evaluated = candidates.ToDictionary(candidate => candidate.Shift, candidate => candidate.Evaluation);
        var refineSeeds = candidates
            .OrderBy(candidate => candidate.Evaluation.Score)
            // 规则表格可能产生大量周期性低点；保留较宽的候选集，避免真实位移
            // 只因粗采样恰好落在相邻像素而没有进入细化阶段。
            .Take(Math.Min(64, candidates.Count))
            .ToList();
        foreach (var seed in refineSeeds)
        {
            var refineStart = Math.Max(minimumShift, seed.Shift - coarseStep);
            var refineEnd = Math.Min(maximumShift, seed.Shift + coarseStep);
            for (var shift = refineStart; shift <= refineEnd; shift++)
            {
                if (!evaluated.ContainsKey(shift))
                {
                    evaluated[shift] = CalculateShiftScore(
                        previousPixels,
                        currentPixels,
                        width,
                        height,
                        stride,
                        shift);
                }
            }
        }

        var rankedCandidates = evaluated
            .Select(candidate => (Shift: candidate.Key, Evaluation: candidate.Value))
            .OrderBy(candidate => candidate.Evaluation.Score)
            // 周期内容出现同分时优先较小位移；高频采样下它也是更合理的运动先验。
            .ThenBy(candidate => candidate.Shift)
            .ToList();

        // 不同候选必须在同一种证据尺度上排名。重叠只剩几十像素时，采样区域
        // 可能全是代码编辑器的暗色空白，EdgeSampleCount 很低，此时返回的是
        // “全像素平均差”；真实位移则通常返回“边缘平均差”。两种分数直接比较，
        // 纯背景假候选会以较小数字压过真实文本候选（例如 71px 被 1031px 抢走）。
        // 可靠性最终本来就要求足够的边缘证据，因此先在有证据的候选中排名；
        // 只有完全没有这类候选时才保留原始最优项用于诊断和暂停提示。
        var evidenceCandidates = rankedCandidates
            .Where(candidate => HasEnoughEdgeEvidence(candidate.Evaluation))
            .ToList();
        var comparableCandidates = evidenceCandidates.Count > 0
            ? evidenceCandidates
            : rankedCandidates;
        var best = comparableCandidates[0];

        var exclusionRadius = Math.Max(8, coarseStep * 3);
        var secondScore = comparableCandidates
            .Where(candidate => Math.Abs(candidate.Shift - best.Shift) > exclusionRadius)
            .Select(candidate => candidate.Evaluation.Score)
            .DefaultIfEmpty(double.MaxValue)
            .Min();

        var scoreGap = secondScore - best.Evaluation.Score;
        // 完全相同的滚动内容通常会得到接近 0 的分数。重复段落也可能产生一个
        // 看似较好的候选。除误差和唯一性外，还必须有足够多的彩色边缘真正一致。
        // 大面积暗色背景不会再让两个完全不同的代码段通过校验。
        var hasEnoughEdgeEvidence = HasEnoughEdgeEvidence(best.Evaluation);
        var hasConsistentEdges = best.Evaluation.MatchedEdgeRatio >= 0.55;
        // B 站首页一类卡片流中，视频封面、轮播图和懒加载占据的面积很大，
        // 但标题、卡片边界及部分已加载图片仍会在多个分散区域保持稳定。
        // 不能要求整屏多数边缘都一致；同时也不能只凭一个小角落就放行。
        // 因此动态页面路径要求稳定证据跨越至少三个水平分区，并由多个网格共同支持。
        var minimumStableCells = Math.Max(
            5,
            (int)Math.Ceiling(best.Evaluation.EligibleCellCount * 0.18));
        var hasSufficientOverlapForDynamicPage =
            height - best.Shift >= Math.Max(96, height / 5);
        var hasDistributedStableRegions =
            hasSufficientOverlapForDynamicPage &&
            best.Evaluation.MatchedEdgeRatio >= 0.28 &&
            best.Evaluation.StableCellCount >= minimumStableCells &&
            best.Evaluation.StableRowCount >= 3;
        // 首屏大横幅切换成吸顶导航时，真正重叠的正文可能只剩两排卡片。
        // 这种情况下允许更紧凑的证据，但必须同时满足极低误差和很大的
        // 候选分差；真实 B 站帧会形成一个尖锐低谷，重复网格则通常有多个近似低点。
        var hasCompactStrongRegions =
            hasSufficientOverlapForDynamicPage &&
            best.Evaluation.MatchedEdgeRatio >= 0.30 &&
            best.Evaluation.StableCellCount >= 6 &&
            best.Evaluation.StableRowCount >= 2;
        var hasSparseStrongRegions =
            hasSufficientOverlapForDynamicPage &&
            best.Evaluation.MatchedEdgeRatio >= 0.18 &&
            best.Evaluation.StableCellCount >= 4 &&
            best.Evaluation.StableRowCount >= 2 &&
            // 稀疏容错只适用于图片/卡片遍布视口的高密度页面。
            // 代码窗口的边缘通常集中在少数网格，重复行可能形成尖锐假低点。
            best.Evaluation.EligibleCellCount >= 42;
        var isDistinctCandidate = best.Evaluation.Score < 0.55 || scoreGap > 1.5;
        var isStronglyDistinctCandidate = best.Evaluation.Score < 0.55 ||
                                          scoreGap > Math.Max(2.2, best.Evaluation.Score * 0.08);
        var isExceptionallyDistinctCandidate = best.Evaluation.Score < 0.55 ||
                                               scoreGap > Math.Max(8.0, best.Evaluation.Score * 0.35);
        var isIsolatedSparseCandidate = best.Evaluation.Score < 0.55 ||
                                        scoreGap > Math.Max(12.0, best.Evaluation.Score * 0.50);
        // 越接近整屏跳转，可用于证明连续性的重叠内容越少。此时必须要求
        // 更充分的边缘和稳定网格，避免广告、轮播图或重复列表制造远距离假低点。
        var isVeryLargeShift = best.Shift > height * 0.72;
        var hasStrongLargeShiftEvidence = !isVeryLargeShift ||
                                          (height - best.Shift >= Math.Max(96, height / 5) &&
                                           best.Evaluation.MatchedEdgeRatio >= 0.72 &&
                                           best.Evaluation.StableCellCount >= 4 &&
                                           best.Evaluation.StableRowCount >= 2 &&
                                           (best.Evaluation.Score < 18.0 ||
                                            scoreGap > Math.Max(4.0, best.Evaluation.Score * 0.12)));
        // IDEA/浏览器平滑滚动会以亚像素位置重新栅格化文字。真实帧中即使
        // 61% 的边缘逐点一致，剩余抗锯齿边缘仍可能把均值抬到 20～30。
        // 可靠性以“多数边缘一致 + 候选唯一”为主，分数上限只负责拦截
        // 极端异常；无重叠画面仍会因边缘一致率低而被拒绝。
        var reliable = hasEnoughEdgeEvidence &&
                       hasStrongLargeShiftEvidence &&
                       ((best.Evaluation.Score < 42.0 &&
                         hasConsistentEdges &&
                         isDistinctCandidate) ||
                        (best.Evaluation.Score < 60.0 &&
                         hasDistributedStableRegions &&
                         isStronglyDistinctCandidate) ||
                        (best.Evaluation.Score < 18.0 &&
                         hasCompactStrongRegions &&
                         isExceptionallyDistinctCandidate) ||
                        (best.Evaluation.Score < 20.0 &&
                         hasSparseStrongRegions &&
                         isIsolatedSparseCandidate));
        return new Match(
            best.Shift,
            best.Evaluation.Score,
            reliable,
            false,
            directComparison.ChangedRatio,
            best.Evaluation.MatchedEdgeRatio,
            best.Evaluation.EdgeSampleCount,
            best.Evaluation.StableCellCount,
            best.Evaluation.EligibleCellCount,
            best.Evaluation.StableRowCount);
    }

    private static bool HasEnoughEdgeEvidence(ShiftEvaluation evaluation)
    {
        return evaluation.EdgeSampleCount >= 80 ||
               (evaluation.Score < 0.10 && evaluation.EdgeSampleCount >= 20);
    }

    /// <summary>
    /// 回归测试和诊断使用：直接评估一个已知位移，不执行候选搜索。
    /// </summary>
    internal static Match EvaluateKnownShift(BitmapSource previous, BitmapSource current, int shift)
    {
        if (shift <= 0 ||
            previous.PixelWidth != current.PixelWidth ||
            previous.PixelHeight != current.PixelHeight ||
            shift >= previous.PixelHeight)
        {
            return new Match(shift, double.MaxValue, false, false);
        }

        var previousPixels = CopyBgraPixels(previous);
        var currentPixels = CopyBgraPixels(current);
        var evaluation = CalculateShiftScore(
            previousPixels,
            currentPixels,
            previous.PixelWidth,
            previous.PixelHeight,
            previous.PixelWidth * 4,
            shift);
        return new Match(
            shift,
            evaluation.Score,
            false,
            false,
            1,
            evaluation.MatchedEdgeRatio,
            evaluation.EdgeSampleCount,
            evaluation.StableCellCount,
            evaluation.EligibleCellCount,
            evaluation.StableRowCount);
    }

    private static (
        double MeanDifference,
        double ChangedRatio,
        double ChangedVerticalSpanRatio) CalculateDirectDifference(
        byte[] previous,
        byte[] current,
        int width,
        int height,
        int stride)
    {
        var xStep = Math.Max(1, width / 420);
        var yStep = Math.Max(1, height / 300);
        var xStart = Math.Max(2, width / 48);
        var xEnd = width - xStart;
        var yStart = height / 8;
        var yEnd = height - height / 16;
        long difference = 0;
        var changedCount = 0;
        var count = 0;
        var firstChangedY = int.MaxValue;
        var lastChangedY = int.MinValue;

        for (var y = yStart; y < yEnd; y += yStep)
        {
            for (var x = xStart; x < xEnd; x += xStep)
            {
                var sampleDifference = Math.Abs(
                    GetLuminance(previous, stride, x, y) -
                    GetLuminance(current, stride, x, y));
                difference += sampleDifference;
                if (sampleDifference >= 3)
                {
                    changedCount++;
                    firstChangedY = Math.Min(firstChangedY, y);
                    lastChangedY = Math.Max(lastChangedY, y);
                }

                count++;
            }
        }

        if (count == 0)
        {
            return (double.MaxValue, 1, 1);
        }

        var sampledHeight = Math.Max(1, yEnd - yStart);
        var changedVerticalSpanRatio = firstChangedY == int.MaxValue
            ? 0
            : (double)(lastChangedY - firstChangedY + yStep) / sampledHeight;
        return (
            (double)difference / count,
            (double)changedCount / count,
            changedVerticalSpanRatio);
    }

    private static ShiftEvaluation CalculateShiftScore(
        byte[] previous,
        byte[] current,
        int width,
        int height,
        int stride,
        int shift)
    {
        var xStep = Math.Max(1, width / 96);
        var yStep = Math.Max(1, height / 120);
        // 行号、列表序号等位于左侧的内容通常是最可靠的顺序证据。
        // 仅避开极窄的选区边框，不再像旧实现那样裁掉整个左侧行号区。
        var xStart = Math.Max(2, width / 120);
        var xEnd = Math.Min(width - 2, width - width / 12);
        var overlapHeight = height - shift;
        // 页级滚动只剩较短重叠区时，不能继续按整帧高度裁掉上下各一大块，
        // 否则真正参与比较的可能只剩几十行像素，重复段落会把方向判反。
        // 边距改为随“实际重叠高度”缩放，仍避开固定顶栏/底栏，同时保留足够证据。
        // 现代内容站点常有两层吸顶导航（B 站实测约占视口六分之一）。
        // 顶部固定区在滚动前后不属于同一内容坐标，底部也可能有登录提示等浮层，
        // 因而从重叠区两端各避开约六分之一视口；重叠很小时仍按实际高度收缩。
        var preferredFixedRegionMargin = Math.Max(height / 6, overlapHeight / 12);
        var overlapMargin = Math.Min(
            preferredFixedRegionMargin,
            Math.Max(2, overlapHeight / 3));
        var yStart = overlapMargin;
        var yEnd = overlapHeight - overlapMargin;
        if (yEnd <= yStart)
        {
            return new ShiftEvaluation(double.MaxValue, 0, 0, 0, 0, 0);
        }

        const int columnCount = 8;
        const int rowCount = 6;
        var cellEdgeDifference = new double[columnCount * rowCount];
        var cellEdgeCount = new int[columnCount * rowCount];
        var cellMatchedEdgeCount = new int[columnCount * rowCount];
        double edgeDifference = 0;
        double rawDifference = 0;
        var edgeCount = 0;
        var matchedEdgeCount = 0;
        var rawCount = 0;

        for (var y = yStart; y < yEnd; y += yStep)
        {
            for (var x = xStart; x < xEnd; x += xStep)
            {
                var previousY = y + shift;
                var previousLuminance = GetLuminance(previous, stride, x, previousY);
                var currentLuminance = GetLuminance(current, stride, x, y);
                var luminanceDifference = Math.Abs(previousLuminance - currentLuminance);
                var colorDifference = GetColorDifference(
                    previous,
                    current,
                    stride,
                    x,
                    previousY,
                    y);
                rawDifference += colorDifference;
                rawCount++;

                var previousGradient = GetGradient(previous, stride, x, previousY);
                var currentGradient = GetGradient(current, stride, x, y);
                if (previousGradient + currentGradient < 20)
                {
                    continue;
                }

                var gradientDifference = Math.Abs(previousGradient - currentGradient);
                // 语法高亮代码中，不同 token 可能具有相近亮度但颜色不同。
                // 采用 RGB 差异后，橙色关键字与灰色正文不会再被当作同一像素。
                edgeDifference += colorDifference + luminanceDifference * 0.20 + gradientDifference * 0.35;
                edgeCount++;
                var column = Math.Clamp(
                    (x - xStart) * columnCount / Math.Max(1, xEnd - xStart),
                    0,
                    columnCount - 1);
                var row = Math.Clamp(
                    (y - yStart) * rowCount / Math.Max(1, yEnd - yStart),
                    0,
                    rowCount - 1);
                var cellIndex = row * columnCount + column;
                var sampleScore = colorDifference + luminanceDifference * 0.20 + gradientDifference * 0.35;
                cellEdgeDifference[cellIndex] += sampleScore;
                cellEdgeCount[cellIndex]++;
                if (colorDifference <= 5 && luminanceDifference <= 4 && gradientDifference <= 10)
                {
                    matchedEdgeCount++;
                    cellMatchedEdgeCount[cellIndex]++;
                }
            }
        }

        var matchedEdgeRatio = edgeCount == 0 ? 0 : (double)matchedEdgeCount / edgeCount;
        var eligibleCellCount = 0;
        var stableCellCount = 0;
        var stableRows = new bool[rowCount];
        var cellScores = new List<(double Score, int EdgeCount)>();
        for (var cellIndex = 0; cellIndex < cellEdgeCount.Length; cellIndex++)
        {
            var cellSamples = cellEdgeCount[cellIndex];
            if (cellSamples < 6)
            {
                continue;
            }

            eligibleCellCount++;
            var cellMatchedRatio = (double)cellMatchedEdgeCount[cellIndex] / cellSamples;
            var cellScore = cellEdgeDifference[cellIndex] / cellSamples;
            cellScores.Add((cellScore, cellSamples));
            if (cellMatchedRatio >= 0.58 && cellScore < 26)
            {
                stableCellCount++;
                stableRows[cellIndex / columnCount] = true;
            }
        }

        var stableRowCount = stableRows.Count(value => value);
        if (edgeCount >= 80)
        {
            // 动态卡片流可能只有约 70% 的网格仍稳定。候选排序使用稳定网格的
            // 截尾均值，避免少量正在换图的封面把真实位移推到错误候选之后；
            // 最终可靠性仍会检查全局一致率、稳定网格数量和空间分布。
            var robustCellCount = Math.Max(1, (int)Math.Ceiling(cellScores.Count * 0.72));
            var robustCells = cellScores
                .OrderBy(cell => cell.Score)
                .Take(robustCellCount)
                .ToList();
            var robustEdgeCount = robustCells.Sum(cell => cell.EdgeCount);
            var robustScore = robustEdgeCount == 0
                ? edgeDifference / edgeCount
                : robustCells.Sum(cell => cell.Score * cell.EdgeCount) / robustEdgeCount;
            return new ShiftEvaluation(
                robustScore,
                matchedEdgeRatio,
                edgeCount,
                stableCellCount,
                eligibleCellCount,
                stableRowCount);
        }

        var fallbackScore = rawCount == 0 ? double.MaxValue : rawDifference / rawCount;
        return new ShiftEvaluation(
            fallbackScore,
            matchedEdgeRatio,
            edgeCount,
            stableCellCount,
            eligibleCellCount,
            stableRowCount);
    }

    private static int GetColorDifference(
        byte[] previous,
        byte[] current,
        int stride,
        int x,
        int previousY,
        int currentY)
    {
        var previousOffset = previousY * stride + x * 4;
        var currentOffset = currentY * stride + x * 4;
        return (
            Math.Abs(previous[previousOffset] - current[currentOffset]) +
            Math.Abs(previous[previousOffset + 1] - current[currentOffset + 1]) +
            Math.Abs(previous[previousOffset + 2] - current[currentOffset + 2])) / 3;
    }

    private static int GetGradient(byte[] pixels, int stride, int x, int y)
    {
        var horizontal = Math.Abs(GetLuminance(pixels, stride, x + 1, y) - GetLuminance(pixels, stride, x - 1, y));
        var vertical = Math.Abs(GetLuminance(pixels, stride, x, y + 1) - GetLuminance(pixels, stride, x, y - 1));
        return horizontal + vertical;
    }

    private static int GetLuminance(byte[] pixels, int stride, int x, int y)
    {
        var offset = y * stride + x * 4;
        return (pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8;
    }

    private static byte[] CopyBgraPixels(BitmapSource bitmap)
    {
        BitmapSource source = bitmap;
        if (bitmap.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            source = converted;
        }

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
