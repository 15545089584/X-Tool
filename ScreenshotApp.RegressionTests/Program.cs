using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using SherpaOnnx;
using ScreenshotApp;
using ScreenshotApp.Capture;
using ScreenshotApp.FileWorkbench;
using ScreenshotApp.Ocr;
using ScreenshotApp.Translation;
using ScreenshotApp.Converters;
using ScreenshotApp.StorageAnalysis;
using ScreenshotApp.NetworkWorkbench;
using ScreenshotApp.SystemTools;
using ScreenshotApp.DesktopPet;
using Microsoft.Data.Sqlite;

const int Width = 720;
const int FrameHeight = 520;
const int ContentHeight = 4200;

var failures = new List<string>();

if (args.Length >= 1 && args[0] == "--sqlite-smoke")
{
    var tempDatabase = Path.Combine(Path.GetTempPath(), "XTool-Sqlite-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        string sqliteVersion;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDatabase }.ToString()))
        {
            connection.Open();
            using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.CommandText = "SELECT sqlite_version();";
                sqliteVersion = versionCommand.ExecuteScalar()?.ToString() ?? string.Empty;
            }

            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"CREATE TABLE migration_probe (
                    id INTEGER PRIMARY KEY,
                    value TEXT NOT NULL);
                INSERT INTO migration_probe (value) VALUES ($value);";
            command.Parameters.AddWithValue("$value", "X-Tool · 中文 SQLite 回归");
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = tempDatabase,
                   Mode = SqliteOpenMode.ReadOnly
               }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM migration_probe WHERE id = 1;";
            if (!string.Equals(command.ExecuteScalar()?.ToString(), "X-Tool · 中文 SQLite 回归", StringComparison.Ordinal))
            {
                Console.WriteLine("SQLite 回归 | 失败 | 临时数据库读写结果不一致");
                return 5;
            }
        }

        var existingDatabases = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "System", "disk-history.db"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-Tool", "Network", "network-history.db")
        };
        var checkedDatabases = 0;
        foreach (var databasePath in existingDatabases.Where(File.Exists))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = command.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"SQLite 回归 | 失败 | {databasePath} | quick_check={result}");
                return 6;
            }
            checkedDatabases++;
        }

        await DiskHistoryStore.EnsureTodaySnapshotAsync();
        var diskHistory = await DiskHistoryStore.LoadHistoryAsync("C:", 7);
        await using var networkHistoryStore = new NetworkHistoryStore();
        var retentionDays = await networkHistoryStore.GetRetentionDaysAsync();
        var networkEvents = await networkHistoryStore.GetEventsAsync(1);

        var secureVersion = Version.TryParse(sqliteVersion, out var parsedVersion)
                            && parsedVersion >= new Version(3, 50, 2);
        Console.WriteLine($"SQLite 回归 | {(secureVersion ? "通过" : "失败")} | SQLite {sqliteVersion} | " +
                          $"临时库读写通过 | 既有数据库检查 {checkedDatabases} | " +
                          $"磁盘历史 {diskHistory.Count} | 网络保留 {retentionDays} 天 | 网络事件样本 {networkEvents.Count}");
        return secureVersion ? 0 : 7;
    }
    finally
    {
        try { File.Delete(tempDatabase); } catch { }
    }
}

if (args.Length >= 1 && args[0] == "--storage-analysis")
{
    var root = args.Length >= 2 ? Path.GetPathRoot(args[1]) ?? args[1] : "C:\\";
    var drive = new DriveInfo(root);
    var target = new StorageAnalysisTarget(root, drive.VolumeLabel, drive.TotalSize, drive.AvailableFreeSpace);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var result = StorageAnalysisService.Analyze(target, StorageAnalysisService.DefaultLargeFileLimit,
        progress: null, CancellationToken.None, forceRefresh: true);
    stopwatch.Stop();
    var usedBytes = Math.Max(1, drive.TotalSize - drive.AvailableFreeSpace);
    Console.WriteLine($"存储分析 | 来源 {result.SourceText} | {stopwatch.Elapsed.TotalSeconds:F2} 秒 | " +
                      $"文件 {result.FilesScanned:N0} | 目录 {result.DirectoriesScanned:N0} | " +
                      $"逻辑大小 {result.ScannedBytes:N0} | 已用覆盖 {result.ScannedBytes / (double)usedBytes:P1} | " +
                      $"分组 {result.DirectoryUsages.Count} | 大文件 {result.LargeFiles.Count}");
    foreach (var usage in result.DirectoryUsages.Take(8))
        Console.WriteLine($"  {usage.Name} | {usage.SizeText} | 文件 {usage.FilesScanned:N0} | 目录 {usage.DirectoriesScanned:N0}");
    return result.FilesScanned > 0 && result.DirectoryUsages.Count > 0 && result.LargeFiles.Count > 0 ? 0 : 3;
}

if (args.Length >= 1 && args[0] == "--desktop-pet-smoke")
{
    var assetRoot = args.Length >= 2
        ? Path.GetFullPath(args[1])
        : Path.Combine(AppContext.BaseDirectory, "assets", "desktop-pet");
    try
    {
        var catalog = DesktopPetAnimationCatalog.Load(assetRoot);
        var expectedFrameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["state-01"] = 80,
            ["state-02"] = 107,
            ["state-03"] = 107,
            ["state-04"] = 22,
            ["state-05"] = 40,
            ["state-06"] = 43
        };
        var manifestPassed = catalog.CanvasWidth == 512 && catalog.CanvasHeight == 512 &&
                             catalog.Anchor == new DesktopPetAnchor(256, 508) &&
                             catalog.States.Count == expectedFrameCounts.Count &&
                             catalog.States.All(state =>
                                 expectedFrameCounts.TryGetValue(state.Id, out var expectedCount) &&
                                 state.FramePaths.Count == expectedCount && state.Fps == 20 && state.Loop);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var memoryBefore = process.PrivateMemorySize64;

        var framePassed = true;
        var playbackPassed = true;
        var stateMetrics = new List<string>();
        long largestDecodedBytes = 0;
        foreach (var state in catalog.States)
        {
            var loadWatch = System.Diagnostics.Stopwatch.StartNew();
            var frames = DesktopPetAnimationPlayer.LoadFrames(
                state, catalog.DisplaySize, CancellationToken.None);
            loadWatch.Stop();
            var decodedBytes = DesktopPetAnimationPlayer.CalculateDecodedBytes(frames);
            largestDecodedBytes = Math.Max(largestDecodedBytes, decodedBytes);
            framePassed &= frames.Count == expectedFrameCounts[state.Id] &&
                           frames.All(frame => frame.IsFrozen &&
                                               frame.PixelWidth == catalog.DisplaySize &&
                                               frame.PixelHeight == catalog.DisplaySize);
            playbackPassed &=
                DesktopPetPlaybackMath.GetFrameIndex(TimeSpan.Zero, state.Fps, frames.Count, state.Loop) == 0 &&
                DesktopPetPlaybackMath.GetFrameIndex(
                    TimeSpan.FromMilliseconds(1000d / state.Fps - 1), state.Fps, frames.Count, state.Loop) == 0 &&
                DesktopPetPlaybackMath.GetFrameIndex(
                    TimeSpan.FromMilliseconds(1000d / state.Fps), state.Fps, frames.Count, state.Loop) == 1 &&
                DesktopPetPlaybackMath.GetFrameIndex(
                    TimeSpan.FromMilliseconds(state.DurationMs), state.Fps, frames.Count, state.Loop) == 0;
            stateMetrics.Add(
                $"{state.Id} {frames.Count} 帧 / {decodedBytes / 1024d / 1024d:F2} MiB / {loadWatch.Elapsed.TotalMilliseconds:F0} ms");
        }

        process.Refresh();
        var memoryAfterAllStates = process.PrivateMemorySize64;

        var workArea = new Rect(0, 0, 1920, 1040);
        var right = DesktopPetPlacement.SnapToNearestEdge(new Rect(1600, 300, 288, 288), workArea, 12);
        var bottom = DesktopPetPlacement.SnapToNearestEdge(new Rect(800, 720, 288, 288), workArea, 12);
        var secondaryArea = new Rect(-1920, 0, 1920, 1040);
        var secondaryLeft = DesktopPetPlacement.SnapToNearestEdge(
            new Rect(-1880, 280, 288, 288), secondaryArea, 12);
        var placementPassed = right == new System.Windows.Point(1620, 300) &&
                              bottom == new System.Windows.Point(800, 740) &&
                              secondaryLeft == new System.Windows.Point(-1908, 280);

        var smallSize = catalog.DisplaySize * 0.6;
        var largeSize = catalog.DisplaySize * 1.6;
        var scalePlacementPassed =
            DesktopPetPlacement.SnapToNearestEdge(new Rect(1700, 400, smallSize, smallSize), workArea, 12).X ==
            workArea.Right - smallSize - 12 &&
            DesktopPetPlacement.SnapToNearestEdge(new Rect(1500, 400, largeSize, largeSize), workArea, 12).X ==
            workArea.Right - largeSize - 12;

        var passed = manifestPassed && framePassed && playbackPassed && placementPassed && scalePlacementPassed;
        Console.WriteLine($"桌面宠物清单 | {(manifestPassed ? "通过" : "失败")} | " +
                          $"共 {catalog.States.Count} 个状态 | " +
                          $"锚点 ({catalog.Anchor.X}, {catalog.Anchor.Y})");
        Console.WriteLine($"桌面宠物帧解码 | {(framePassed ? "通过" : "失败")} | " +
                          string.Join(" | ", stateMetrics));
        Console.WriteLine($"桌面宠物时序与贴边 | {(playbackPassed && placementPassed ? "通过" : "失败")} | " +
                          $"20 FPS 循环边界 {(playbackPassed ? "正确" : "错误")} | " +
                          $"主副屏工作区贴边 {(placementPassed ? "正确" : "错误")} | " +
                          $"60%/160% 尺寸贴边 {(scalePlacementPassed ? "正确" : "错误")}");
        Console.WriteLine($"桌面宠物进程私有内存 | 基线 {memoryBefore / 1024d / 1024d:F1} MiB | " +
                          $"六状态依次解码后 {memoryAfterAllStates / 1024d / 1024d:F1} MiB | " +
                          $"单状态最大解码量 {largestDecodedBytes / 1024d / 1024d:F2} MiB");
        return passed ? 0 : 8;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"桌面宠物回归失败：{exception}");
        return 9;
    }
}

if (args.Length >= 1 && args[0] == "--storage-analysis-native-smoke")
{
    var root = Path.Combine(Path.GetTempPath(), "XTool-StorageAnalysis-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "Alpha", "Nested"));
        Directory.CreateDirectory(Path.Combine(root, "Beta"));
        File.WriteAllBytes(Path.Combine(root, "Alpha", "large.bin"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(root, "Alpha", "Nested", "small.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(root, "Beta", "medium.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(root, "root.bin"), new byte[512]);
        var target = new StorageAnalysisTarget(root, "原生扫描回归", 20_000, 6_176);
        var first = StorageAnalysisService.Analyze(target, 100, null, CancellationToken.None,
            forceRefresh: true, allowEverything: false);
        var cached = StorageAnalysisService.Analyze(target, 100, null, CancellationToken.None,
            forceRefresh: false, allowEverything: false);
        var alpha = first.DirectoryUsages.Single(item => item.Name == "Alpha");
        var passed = first.SourceText == "优化原生扫描" && first.FilesScanned == 4 &&
                     first.ScannedBytes == 13_824 && alpha.LogicalBytes == 9_216 &&
                     first.LargeFiles.First().Size == 8_192 && cached.IsCached;
        Console.WriteLine($"原生存储扫描 | {(passed ? "通过" : "失败")} | 文件 {first.FilesScanned} | " +
                          $"总量 {first.ScannedBytes} | Alpha {alpha.LogicalBytes} | 缓存 {cached.IsCached}");
        return passed ? 0 : 4;
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

if (args.Length == 4 && args[0] == "--pair")
{
    var previousPath = args[1];
    var currentPath = args[2];
    var expectedDelta = int.Parse(args[3]);
    var previousFrame = LoadBitmap(previousPath);
    var currentFrame = LoadBitmap(currentPath);
    var detected = VerticalOverlapDetector.FindWithDirection(
        previousFrame,
        currentFrame,
        Math.Sign(expectedDelta));
    var known = expectedDelta > 0
        ? VerticalOverlapDetector.EvaluateKnownShift(previousFrame, currentFrame, expectedDelta)
        : VerticalOverlapDetector.EvaluateKnownShift(currentFrame, previousFrame, -expectedDelta);
    Console.WriteLine(
        $"真实诊断帧 | 期望 {expectedDelta} | 检测 {detected.ScrollDelta} | " +
        $"分数 {detected.Score:F3} | 边缘 {detected.MatchedEdgeRatio:P1} | " +
        $"稳定网格 {detected.StableCellCount}/{detected.EligibleCellCount} | " +
        $"稳定行 {detected.StableRowCount} | 可靠 {detected.IsReliable}");
    Console.WriteLine(
        $"已知位移评分 | 分数 {known.Score:F3} | 边缘 {known.MatchedEdgeRatio:P1} | " +
        $"样本 {known.EdgeSampleCount} | 稳定网格 {known.StableCellCount}/{known.EligibleCellCount} | " +
        $"稳定行 {known.StableRowCount}");
    if (expectedDelta > 0)
    {
        for (var shift = Math.Max(3, expectedDelta - 8); shift <= expectedDelta + 8; shift++)
        {
            var evaluated = VerticalOverlapDetector.EvaluateKnownShift(previousFrame, currentFrame, shift);
            Console.WriteLine(
                $"  候选 {shift,4} | 分数 {evaluated.Score,7:F3} | 边缘 {evaluated.MatchedEdgeRatio,6:P1} | " +
                $"稳定 {evaluated.StableCellCount,2}/{evaluated.EligibleCellCount,2} | 行 {evaluated.StableRowCount}");
        }
    }
    return detected.IsReliable && Math.Abs(detected.ScrollDelta - expectedDelta) <= 2 ? 0 : 2;
}

var code = CreateCodeContent();
var web = CreateWebContent();
var table = CreateTableContent();
var cardStream = CreateCardStreamContent();

RunSequence("代码向下", code, new[] { 0, 96, 238, 410, 655, 930, 1260 }, stickyHeader: true);
RunSequence("代码向上", code, new[] { 1600, 1390, 1110, 820, 520, 260 }, stickyHeader: true);
RunSequence("代码往返", code, new[] { 0, 180, 390, 610, 440, 270, 500, 760 }, stickyHeader: true);
RunSequence("网页固定顶栏", web, new[] { 0, 130, 320, 570, 860, 1190 }, stickyHeader: true);
RunSequence("网页普通滚动", web, new[] { 0, 120, 300, 540, 810, 1130 }, stickyHeader: false);
RunSequence("表格重复行", table, new[] { 0, 88, 210, 375, 590, 850, 1160 }, stickyHeader: true);
RunSequence("快速但有重叠", code, new[] { 0, FrameHeight - 160, (FrameHeight - 160) * 2 }, stickyHeader: true);
RunRasterizedTextCase();
RunSubpixelRasterizedTextCase();
RunInsufficientOverlapCase();
RunNoOverlapCase();
RunDuplicateCase();
RunLocalizedInterferenceCase();
RunHorizontalBandInterferenceCase();
RunDynamicCardStreamCase();
RunDynamicCardNoOverlapCase();
RunTinyScrollCase();
RunSignatureCase();
RunStitchCase();
RunFixedChromeStitchCase();
RunPreviewGrowthCase();
RunAnnotationRenderCase();
RunScreenColorSamplerCase();
RunOcrTextLayoutCase();
await RunOcrSmokeCase();
RunVoiceInputModelSmokeCase();
await RunChineseEnglishTranslationSmokeCase();
RunFileWorkbenchPlanningCase();
RunEncodingConversionCase();
RunSingleInstanceCase();
RunNativeWindowAnimationStyleCase();

if (failures.Count > 0)
{
    Console.Error.WriteLine($"长截图回归失败：{failures.Count} 项");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine("长截图回归通过：代码、网页、固定顶栏、表格、往返滚动、快速滚动与重复帧。 ");
return 0;

void RunSequence(string name, BitmapSource source, IReadOnlyList<int> offsets, bool stickyHeader)
{
    var previous = CreateViewport(source, offsets[0], stickyHeader, 0);
    for (var index = 1; index < offsets.Count; index++)
    {
        var current = CreateViewport(source, offsets[index], stickyHeader, index);
        var expectedDelta = offsets[index] - offsets[index - 1];
        var direction = Math.Sign(expectedDelta);
        var match = VerticalOverlapDetector.FindWithDirection(previous, current, direction);
        var expectedMatch = expectedDelta > 0
            ? VerticalOverlapDetector.EvaluateKnownShift(previous, current, expectedDelta)
            : VerticalOverlapDetector.EvaluateKnownShift(current, previous, -expectedDelta);
        Console.WriteLine(
            $"{name} {offsets[index - 1],4}->{offsets[index],4} | " +
            $"检测 {match.ScrollDelta,4} | 分数 {match.Score,7:F3} | " +
            $"边缘 {match.MatchedEdgeRatio,6:P1} | 可靠 {match.IsReliable} | " +
            $"真实分数 {expectedMatch.Score,7:F3} / {expectedMatch.MatchedEdgeRatio,6:P1}");
        if (!match.IsReliable || Math.Abs(match.ScrollDelta - expectedDelta) > 2)
        {
            failures.Add(
                $"{name}：期望位移 {expectedDelta}，实际 {match.ScrollDelta}，" +
                $"可靠={match.IsReliable}，分数={match.Score:F3}");
        }

        previous = current;
    }
}

void RunNoOverlapCase()
{
    var first = CreateViewport(code, 0, true, 0);
    var unrelated = CreateViewport(web, 2000, true, 1);
    var match = VerticalOverlapDetector.FindWithDirection(first, unrelated, 1);
    Console.WriteLine($"无重叠内容 | 分数 {match.Score:F3} | 可靠 {match.IsReliable}");
    if (match.IsReliable)
    {
        failures.Add($"无重叠内容被误判为可靠匹配，位移={match.ScrollDelta}，分数={match.Score:F3}");
    }
}

void RunInsufficientOverlapCase()
{
    var first = CreateViewport(code, 0, true, 0);
    var current = CreateViewport(code, FrameHeight - 56, true, 1);
    var match = VerticalOverlapDetector.FindWithDirection(first, current, 1);
    Console.WriteLine(
        $"重叠证据不足 | 检测 {match.ScrollDelta} | 分数 {match.Score:F3} | " +
        $"边缘 {match.MatchedEdgeRatio:P1} | 稳定 {match.StableCellCount}/{match.EligibleCellCount} | " +
        $"行 {match.StableRowCount} | 可靠 {match.IsReliable}");
    if (match.IsReliable)
    {
        failures.Add("仅剩固定顶栏附近的极小重叠区域仍被判为可靠，应暂停并等待用户回滚。");
    }
}

void RunRasterizedTextCase()
{
    var previous = CreateViewport(code, 480, true, 0);
    var current = AddRasterizationNoise(CreateViewport(code, 735, true, 1), 6);
    var match = VerticalOverlapDetector.FindWithDirection(previous, current, 1);
    Console.WriteLine(
        $"文字重栅格化 | 检测 {match.ScrollDelta} | 分数 {match.Score:F3} | " +
        $"边缘 {match.MatchedEdgeRatio:P1} | 可靠 {match.IsReliable}");
    if (!match.IsReliable || Math.Abs(match.ScrollDelta - 255) > 2)
    {
        failures.Add(
            $"文字抗锯齿变化后匹配失败：期望 255，实际 {match.ScrollDelta}，" +
            $"分数={match.Score:F3}，边缘={match.MatchedEdgeRatio:P1}。");
    }
}

void RunSubpixelRasterizedTextCase()
{
    var previous = CreateViewport(code, 480, true, 0);
    var current = BlendWithVerticalNeighbor(CreateViewport(code, 551, true, 1), 0.08);
    var match = VerticalOverlapDetector.FindWithDirection(previous, current, 1);
    Console.WriteLine(
        $"亚像素文字重栅格化 | 检测 {match.ScrollDelta} | 分数 {match.Score:F3} | " +
        $"边缘 {match.MatchedEdgeRatio:P1} | 可靠 {match.IsReliable}");
    if (!match.IsReliable || Math.Abs(match.ScrollDelta - 71) > 2)
    {
        failures.Add(
            $"亚像素平滑滚动匹配失败：期望 71，实际 {match.ScrollDelta}，" +
            $"分数={match.Score:F3}，边缘={match.MatchedEdgeRatio:P1}。");
    }
}

void RunDuplicateCase()
{
    var first = CreateViewport(table, 480, true, 0);
    var duplicate = CreateViewport(table, 480, true, 0);
    var match = VerticalOverlapDetector.FindWithDirection(first, duplicate, 1);
    Console.WriteLine($"重复帧 | 重复 {match.IsDuplicate} | 可靠 {match.IsReliable}");
    if (!match.IsDuplicate || !match.IsReliable)
    {
        failures.Add("完全相同的帧未识别为重复帧。");
    }
}

void RunLocalizedInterferenceCase()
{
    var first = CreateViewport(code, 480, true, 0);
    var interfered = AddLocalizedInterference(first);
    var match = VerticalOverlapDetector.FindWithDirection(first, interfered, 1);
    Console.WriteLine(
        $"局部瞬态干扰 | 变化 {match.ChangedRatio:P2} | 重复 {match.IsDuplicate} | 可靠 {match.IsReliable}");
    if (!match.IsDuplicate || !match.IsReliable)
    {
        failures.Add(
            $"鼠标光晕/悬浮提示一类局部变化未被跳过：变化={match.ChangedRatio:P2}，" +
            $"位移={match.ScrollDelta}，分数={match.Score:F3}。");
    }
}

void RunHorizontalBandInterferenceCase()
{
    var first = CreateViewport(table, 480, true, 0);
    var interfered = AddHorizontalBandInterference(first);
    var match = VerticalOverlapDetector.FindWithDirection(first, interfered, 1);
    Console.WriteLine(
        $"表格悬停整行干扰 | 变化 {match.ChangedRatio:P2} | 重复 {match.IsDuplicate} | 可靠 {match.IsReliable}");
    if (!match.IsDuplicate || !match.IsReliable)
    {
        failures.Add(
            $"表格悬停/当前行高亮未被跳过：变化={match.ChangedRatio:P2}，" +
            $"位移={match.ScrollDelta}，分数={match.Score:F3}。");
    }
}

void RunDynamicCardStreamCase()
{
    const int previousOffset = 510;
    const int currentOffset = 748;
    var previous = AddCardStreamDynamicInterference(
        CreateViewport(cardStream, previousOffset, true, 1),
        1);
    var current = AddCardStreamDynamicInterference(
        CreateViewport(cardStream, currentOffset, true, 2),
        2);
    var match = VerticalOverlapDetector.FindWithDirection(previous, current, 1);
    Console.WriteLine(
        $"动态卡片流 | 检测 {match.ScrollDelta} | 分数 {match.Score:F3} | " +
        $"边缘 {match.MatchedEdgeRatio:P1} | 稳定网格 {match.StableCellCount}/{match.EligibleCellCount} | " +
        $"稳定行 {match.StableRowCount} | 可靠 {match.IsReliable}");
    if (!match.IsReliable || Math.Abs(match.ScrollDelta - (currentOffset - previousOffset)) > 2)
    {
        failures.Add(
            $"动态卡片/懒加载干扰后匹配失败：期望 {currentOffset - previousOffset}，" +
            $"实际 {match.ScrollDelta}，边缘={match.MatchedEdgeRatio:P1}，" +
            $"稳定网格={match.StableCellCount}/{match.EligibleCellCount}。 ");
    }
}

void RunDynamicCardNoOverlapCase()
{
    var previous = AddCardStreamDynamicInterference(
        CreateViewport(cardStream, 0, true, 1),
        1);
    var unrelated = AddCardStreamDynamicInterference(
        CreateViewport(cardStream, 2700, true, 7),
        7);
    var match = VerticalOverlapDetector.FindWithDirection(previous, unrelated, 1);
    Console.WriteLine(
        $"动态卡片无重叠 | 检测 {match.ScrollDelta} | 分数 {match.Score:F3} | " +
        $"边缘 {match.MatchedEdgeRatio:P1} | 稳定行 {match.StableRowCount} | 可靠 {match.IsReliable}");
    if (match.IsReliable)
    {
        failures.Add(
            $"重复卡片网格的无重叠画面被误判为可靠：位移={match.ScrollDelta}，" +
            $"分数={match.Score:F3}，边缘={match.MatchedEdgeRatio:P1}。 ");
    }
}

void RunTinyScrollCase()
{
    var first = CreateViewport(code, 480, true, 0);
    var current = CreateViewport(code, 498, true, 1);
    var match = VerticalOverlapDetector.FindWithDirection(first, current, 1);
    Console.WriteLine(
        $"单行微滚动 | 检测 {match.ScrollDelta} | 变化 {match.ChangedRatio:P2} | " +
        $"重复 {match.IsDuplicate} | 可靠 {match.IsReliable}");
    if (match.IsDuplicate || !match.IsReliable || Math.Abs(match.ScrollDelta - 18) > 2)
    {
        failures.Add(
            $"真实单行滚动被当成局部干扰：期望 18，实际 {match.ScrollDelta}，" +
            $"变化={match.ChangedRatio:P2}，重复={match.IsDuplicate}。");
    }
}

void RunSignatureCase()
{
    var first = CreateViewport(code, 720, true, 0);
    var samePosition = CreateViewport(code, 720, true, 4);
    var differentPosition = CreateViewport(code, 900, true, 4);
    var firstSignature = CaptureFrameSignature.Create(first);
    var sameDifference = firstSignature.DifferenceFrom(CaptureFrameSignature.Create(samePosition));
    var differentDifference = firstSignature.DifferenceFrom(CaptureFrameSignature.Create(differentPosition));
    Console.WriteLine(
        $"历史视口签名 | 同位置 {sameDifference:F3} | 不同位置 {differentDifference:F3}");
    if (sameDifference >= 0.35 || differentDifference < 0.35)
    {
        failures.Add(
            $"历史视口签名区分失败：同位置={sameDifference:F3}，不同位置={differentDifference:F3}。");
    }
}

void RunStitchCase()
{
    var offsets = new[] { 0, 280, 610, 940, 1270 };
    var frames = offsets
        .Select(offset => new VerticalBitmapStitcher.PositionedFrame(
            CreateViewport(web, offset, false, 0),
            offset))
        .ToList();
    var stitched = VerticalBitmapStitcher.StitchPositioned(frames);
    var expectedHeight = offsets[^1] + FrameHeight;
    if (stitched.PixelWidth != Width || stitched.PixelHeight != expectedHeight)
    {
        failures.Add(
            $"拼接尺寸错误：期望 {Width}×{expectedHeight}，" +
            $"实际 {stitched.PixelWidth}×{stitched.PixelHeight}。");
        return;
    }

    var expected = new CroppedBitmap(web, new Int32Rect(0, 0, Width, expectedHeight));
    expected.Freeze();
    var difference = CalculateMeanColorDifference(expected, stitched);
    Console.WriteLine($"像素拼接 | 平均色差 {difference:F4}");
    if (difference > 0.01)
    {
        failures.Add($"无固定区域拼接产生像素缺失或错位，平均色差={difference:F4}。");
    }
}

void RunPreviewGrowthCase()
{
    var firstFrame = new List<VerticalBitmapStitcher.PositionedFrame>
    {
        new(CreateViewport(web, 0, false, 0), 0)
    };
    var mediumFrames = new List<VerticalBitmapStitcher.PositionedFrame>(firstFrame)
    {
        new(CreateViewport(web, 360, false, 0), 360),
        new(CreateViewport(web, 720, false, 0), 720)
    };
    var longFrames = new List<VerticalBitmapStitcher.PositionedFrame>(mediumFrames)
    {
        new(CreateViewport(web, 1440, false, 0), 1440),
        new(CreateViewport(web, 2280, false, 0), 2280),
        new(CreateViewport(web, 3120, false, 0), 3120)
    };

    var firstPreview = ScrollCapturePreviewRenderer.Render(firstFrame);
    var mediumPreview = ScrollCapturePreviewRenderer.Render(mediumFrames);
    var longPreview = ScrollCapturePreviewRenderer.Render(longFrames);
    Console.WriteLine(
        $"实时缩略预览 | 初始 {firstPreview.PixelWidth}×{firstPreview.PixelHeight} | " +
        $"中段 {mediumPreview.PixelWidth}×{mediumPreview.PixelHeight} | " +
        $"长图 {longPreview.PixelWidth}×{longPreview.PixelHeight}");
    if (firstPreview.PixelWidth != ScrollCapturePreviewRenderer.DefaultPreviewWidth ||
        mediumPreview.PixelHeight <= firstPreview.PixelHeight ||
        longPreview.PixelHeight <= mediumPreview.PixelHeight ||
        longPreview.PixelHeight > ScrollCapturePreviewRenderer.MaximumPreviewHeight)
    {
        failures.Add(
            $"实时缩略图没有随采集高度正确增长或超过上限：" +
            $"{firstPreview.PixelHeight}->{mediumPreview.PixelHeight}->{longPreview.PixelHeight}。 ");
    }
}

void RunFixedChromeStitchCase()
{
    const int headerHeight = 64;
    const int footerHeight = 36;
    var offsets = new[] { 0, 210, 460, 735, 1015 };
    var frames = offsets
        .Select((offset, index) => new VerticalBitmapStitcher.PositionedFrame(
            CreateViewportWithFixedChrome(web, offset, index),
            offset))
        .ToList();
    var estimator = new FixedViewportRegionEstimator();
    for (var index = 1; index < frames.Count; index++)
    {
        estimator.Observe(
            frames[index - 1].Bitmap,
            frames[index].Bitmap,
            offsets[index] - offsets[index - 1]);
    }

    var insets = estimator.Current;
    Console.WriteLine(
        $"固定区域识别 | 顶部 {insets.Top} | 底部 {insets.Bottom} | 右侧 {insets.Right}");
    if (Math.Abs(insets.Top - headerHeight) > 4 ||
        Math.Abs(insets.Bottom - footerHeight) > 4 ||
        insets.Right < 3)
    {
        failures.Add(
            $"固定区域识别不准确：期望约 {headerHeight}/{footerHeight}，" +
            $"实际 {insets.Top}/{insets.Bottom}。 ");
        return;
    }

    var layout = VerticalBitmapStitcher.BuildPositionedLayout(frames, insets);
    var repeatedTopSlices = layout.Slices.Count(slice => slice.SourceRect.Y < insets.Top);
    var repeatedBottomSlices = layout.Slices.Count(slice =>
        slice.SourceRect.Y + slice.SourceRect.Height > FrameHeight - insets.Bottom);
    if (repeatedTopSlices != 1 || repeatedBottomSlices != 1)
    {
        failures.Add(
            $"固定栏切片次数错误：顶部 {repeatedTopSlices}，底部 {repeatedBottomSlices}。 ");
    }

    var stitched = VerticalBitmapStitcher.StitchPositioned(frames, insets);
    var preview = ScrollCapturePreviewRenderer.Render(frames, insets);
    var scaledStitched = ScaleBitmap(stitched, preview.PixelWidth, preview.PixelHeight);
    var previewDifference = CalculateMeanColorDifference(scaledStitched, preview);
    Console.WriteLine(
        $"固定栏拼接与预览 | 成图 {stitched.PixelWidth}×{stitched.PixelHeight} | " +
        $"缩略图差异 {previewDifference:F4}");
    if (previewDifference > 0.8)
    {
        failures.Add(
            $"缩略图没有复用最终拼接裁剪布局，平均色差={previewDifference:F4}。 ");
    }
}

void RunAnnotationRenderCase()
{
    var source = new CroppedBitmap(web, new Int32Rect(0, 0, 320, 180));
    source.Freeze();
    var annotations = new ScreenshotAnnotation[]
    {
        new PenScreenshotAnnotation(
            new[] { new System.Windows.Point(10, 10), new System.Windows.Point(70, 10) },
            System.Windows.Media.Color.FromRgb(255, 77, 94),
            4),
        new ShapeScreenshotAnnotation(
            AnnotationShape.Rectangle,
            new Rect(20, 30, 80, 40),
            System.Windows.Media.Color.FromRgb(62, 139, 255),
            3)
    };
    var rendered = ScreenshotAnnotationRenderer.Render(
        source,
        annotations,
        new System.Windows.Size(160, 90));
    var penPixel = ReadPixel(rendered, 80, 20);
    var rectanglePixel = ReadPixel(rendered, 80, 60);
    Console.WriteLine(
        $"普通截图标注 | 涂鸦 RGB({penPixel.Red},{penPixel.Green},{penPixel.Blue}) | " +
        $"矩形 RGB({rectanglePixel.Red},{rectanglePixel.Green},{rectanglePixel.Blue})");
    if (penPixel.Red < 220 || penPixel.Green > 115 || penPixel.Blue > 130)
    {
        failures.Add("普通截图涂鸦没有按原图像素比例绘制。 ");
    }

    if (rectanglePixel.Blue < 220 || rectanglePixel.Red > 105)
    {
        failures.Add("普通截图矩形描框没有按原图像素比例绘制。 ");
    }
}

void RunScreenColorSamplerCase()
{
    var bitmap = new WriteableBitmap(
        2,
        1,
        96,
        96,
        System.Windows.Media.PixelFormats.Bgra32,
        null);
    bitmap.WritePixels(
        new Int32Rect(0, 0, 2, 1),
        new byte[]
        {
            0x33, 0x22, 0x11, 0xFF,
            0xCC, 0xBB, 0xAA, 0xFF
        },
        8,
        0);
    bitmap.Freeze();

    var first = ScreenColorSampler.Sample(bitmap, 0, 0);
    var clamped = ScreenColorSampler.Sample(bitmap, 99, 0);
    Console.WriteLine($"普通截图取色 | {first.Hex} / {first.Rgb} | 边界 {clamped.Hex}");
    if (first.Hex != "#112233" || first.Rgb != "RGB 17, 34, 51")
    {
        failures.Add("普通截图取色器未按 BGRA 像素顺序生成正确的 HEX/RGB。 ");
    }

    if (clamped.Hex != "#AABBCC")
    {
        failures.Add("普通截图取色器未正确处理位图边界坐标。 ");
    }

    var buffer = new ScreenColorBuffer(bitmap);
    var neighborhood = buffer.CreateNeighborhood(0, 0, 1);
    var center = ScreenColorSampler.Sample(neighborhood, 1, 1);
    var repeatedEdge = ScreenColorSampler.Sample(neighborhood, 0, 0);
    Console.WriteLine($"像素放大镜 | {neighborhood.PixelWidth}×{neighborhood.PixelHeight} | 中心 {center.Hex}");
    if (neighborhood.PixelWidth != 3 || neighborhood.PixelHeight != 3 || center.Hex != "#112233")
    {
        failures.Add("像素放大镜未生成正确尺寸或中心像素不对应实际取色点。 ");
    }

    if (repeatedEdge.Hex != "#112233")
    {
        failures.Add("像素放大镜在屏幕边缘未正确重复最近像素。 ");
    }
}

void RunOcrTextLayoutCase()
{
    var blocks = new[]
    {
        new OcrTextBlock("截影", 0.98f, 10, 10, 60, 24),
        new OcrTextBlock("OCR", 0.97f, 82, 11, 52, 23),
        new OcrTextBlock("第二行", 0.96f, 10, 48, 72, 24)
    };
    var composed = OcrTextLayout.Compose(blocks);
    Console.WriteLine($"OCR 阅读顺序 | {composed.Replace(Environment.NewLine, " / ")}");
    if (composed != $"截影 OCR{Environment.NewLine}第二行")
    {
        failures.Add("OCR 文本框未按从上到下、从左到右的阅读顺序拼接。 ");
    }
}

async Task RunOcrSmokeCase()
{
    using var bitmap = new Bitmap(860, 230, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.White);
    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using var titleFont = new Font("Microsoft YaHei UI", 38f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
    using var bodyFont = new Font("Microsoft YaHei UI", 28f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
    using var bodyBrush = new SolidBrush(Color.FromArgb(45, 65, 90));
    using var accentBrush = new SolidBrush(Color.FromArgb(30, 95, 180));
    graphics.DrawString("截影 OCR 本地文字提取", titleFont, Brushes.Black, 28, 28);
    graphics.DrawString("Windows 11 · CPU · 2026", bodyFont, bodyBrush, 30, 110);
    graphics.DrawString("中文 English 123", bodyFont, accentBrush, 30, 162);
    var source = ToBitmapSource(bitmap);

    using var engine = new PaddleOnnxOcrEngine(OcrModelPaths.CreateDefault());
    var result = await engine.RecognizeAsync(
        OcrImage.FromBitmapSource(source),
        new OcrOptions { ConfidenceThreshold = 0.45f },
        CancellationToken.None);
    var compact = result.Text.Replace(Environment.NewLine, " / ");
    Console.WriteLine(
        $"OCR 模型冒烟 | {result.Blocks.Count} 区域 | {result.Elapsed.TotalMilliseconds:F0} ms | {compact}");
    if (result.Blocks.Count == 0 ||
        !result.Text.Contains("OCR", StringComparison.OrdinalIgnoreCase) ||
        !result.Text.Contains("2026", StringComparison.Ordinal))
    {
        failures.Add($"PP-OCRv5 未识别出预期的英文或数字文本，结果：{compact}");
    }
}

void RunVoiceInputModelSmokeCase()
{
    var modelDirectory = Path.Combine(
        Directory.GetCurrentDirectory(),
        "ScreenshotApp",
        "Models",
        "VoiceInput",
        "default");
    var modelPath = Path.Combine(modelDirectory, "model.int8.onnx");
    var tokenPath = Path.Combine(modelDirectory, "tokens.txt");
    var samplePath = Path.Combine(modelDirectory, "test_wavs", "zh.wav");
    if (!File.Exists(modelPath) || !File.Exists(tokenPath) || !File.Exists(samplePath))
    {
        Console.WriteLine("语音模型冒烟 | 跳过：本地模型或测试音频未随源码提供。");
        return;
    }

    using var reader = new WaveFileReader(samplePath);
    if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
        reader.WaveFormat.BitsPerSample != 16 ||
        reader.WaveFormat.Channels != 1 ||
        reader.WaveFormat.SampleRate != 16_000)
    {
        failures.Add($"语音模型测试音频格式异常：{reader.WaveFormat}。");
        return;
    }

    var buffer = new byte[checked((int)reader.Length)];
    var offset = 0;
    while (offset < buffer.Length)
    {
        var count = reader.Read(buffer, offset, buffer.Length - offset);
        if (count == 0)
        {
            break;
        }

        offset += count;
    }

    var samples = new float[offset / sizeof(short)];
    for (var index = 0; index < samples.Length; index++)
    {
        samples[index] = BitConverter.ToInt16(buffer, index * sizeof(short)) / 32768f;
    }

    var config = new OfflineRecognizerConfig();
    config.FeatConfig.SampleRate = 16_000;
    config.FeatConfig.FeatureDim = 80;
    config.ModelConfig.Tokens = tokenPath;
    config.ModelConfig.SenseVoice.Model = modelPath;
    config.ModelConfig.SenseVoice.Language = "auto";
    config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
    config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    using var recognizer = new OfflineRecognizer(config);
    using var stream = recognizer.CreateStream();
    stream.AcceptWaveform(16_000, samples);
    recognizer.Decode(stream);
    var text = System.Text.RegularExpressions.Regex.Replace(stream.Result.Text, "<[^>]+>", string.Empty).Trim();
    Console.WriteLine($"语音模型冒烟 | {samples.Length / 16_000d:F1} 秒 | {text}");
    if (string.IsNullOrWhiteSpace(text))
    {
        failures.Add("SenseVoice 未能识别模型自带的中文测试音频。");
    }
}

async Task RunChineseEnglishTranslationSmokeCase()
{
    var modelDirectory = Path.Combine(
        Directory.GetCurrentDirectory(),
        "ScreenshotApp",
        "Models",
        "Translation",
        "zh-en");
    var modelPaths = new TranslationModelPaths(
        Path.Combine(modelDirectory, "encoder_model.onnx"),
        Path.Combine(modelDirectory, "decoder_model.onnx"),
        Path.Combine(modelDirectory, "source.spm"),
        Path.Combine(modelDirectory, "target.spm"),
        Path.Combine(modelDirectory, "vocab.json"),
        Path.Combine(modelDirectory, "manifest.json"));
    var engine = new OnnxTranslationEngine(modelPaths, "中译英", "opus-mt-zh-en-onnx-int8");
    if (!engine.IsReady)
    {
        failures.Add("中译英离线语言包不完整。");
        return;
    }

    var result = await engine.TranslateAsync(
        new TranslationRequest("你好，欢迎使用 X-Tool。")
        {
            SourceLanguage = "zh-Hans",
            TargetLanguage = "en"
        },
        CancellationToken.None);
    Console.WriteLine($"中译英模型冒烟 | {result.Elapsed.TotalMilliseconds:F0} ms | {result.TranslatedText}");
    if (string.IsNullOrWhiteSpace(result.TranslatedText) || !result.TranslatedText.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
    {
        failures.Add($"中译英模型未输出预期英文文本，结果：{result.TranslatedText}");
    }
}

void RunFileWorkbenchPlanningCase()
{
    var root = Path.Combine(Path.GetTempPath(), "XToolFileWorkbenchPlan");
    var files = new[]
    {
        new FileWorkbenchItem(Path.Combine(root, "IMG_001.jpg"), "IMG_001.jpg", ".jpg", 1024, DateTime.Now),
        new FileWorkbenchItem(Path.Combine(root, "IMG_002.jpg"), "IMG_002.jpg", ".jpg", 2048, DateTime.Now)
    };
    var renamed = FileWorkbenchService.CreatePlans(files, FileBatchOperation.Rename, "旅行照片", 1, 3, string.Empty, string.Empty);
    var clampedDigits = FileWorkbenchService.CreatePlans(files, FileBatchOperation.Rename, "旅行照片", 1, 356, string.Empty, string.Empty);
    var classified = FileWorkbenchService.CreatePlans(files, FileBatchOperation.Classify, string.Empty, 1, 3, string.Empty, Path.Combine(root, "分类"));
    Console.WriteLine($"文件工作台规划 | {renamed[0].SourceName} -> {Path.GetFileName(renamed[0].DestinationPath)} | 分类 {classified[0].DestinationText}");
    if (!renamed[0].DestinationPath.EndsWith("旅行照片_001.jpg", StringComparison.Ordinal) ||
        !renamed[1].DestinationPath.EndsWith("旅行照片_002.jpg", StringComparison.Ordinal) ||
        !clampedDigits[0].DestinationPath.EndsWith("旅行照片_000001.jpg", StringComparison.Ordinal) ||
        !classified[0].DestinationPath.Contains($"{Path.DirectorySeparatorChar}图片{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    {
        failures.Add("文件工作台未能生成预期的重命名或分类操作预览。");
    }
}

void RunEncodingConversionCase()
{
    const string source = "X-Tool 中文";
    var base64 = EncodingConversionService.Base64Encode(source);
    var unicode = EncodingConversionService.UnicodeEncode(source);
    var restoredBase64 = EncodingConversionService.Base64Decode(base64);
    var restoredUnicode = EncodingConversionService.UnicodeDecode(unicode);
    var url = EncodingConversionService.UrlDecode(EncodingConversionService.UrlEncode(source));
    var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"none\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"sub\":\"xtool\",\"exp\":0}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var jwt = EncodingConversionService.ParseJwt($"{header}.{payload}.signature");
    var timestamp = EncodingConversionService.ConvertTimestamp("0");
    var uuids = EncodingConversionService.GenerateUuids(3);
    Console.WriteLine($"编码转换 | Base64={base64} | JWT={jwt.SignatureStatus} | 时间戳={timestamp.UtcText} | UUID={uuids.Count}");
    if (restoredBase64 != source || restoredUnicode != source || url != source ||
        !jwt.Header.Contains("alg", StringComparison.Ordinal) || !jwt.Payload.Contains("xtool", StringComparison.Ordinal) ||
        timestamp.Seconds != 0 || timestamp.Milliseconds != 0 || uuids.Count != 3 || uuids.Any(value => !Guid.TryParse(value, out _)))
    {
        failures.Add("编码转换的文本、JWT、时间戳或 UUID 本地处理结果不符合预期。");
    }
}

void RunSingleInstanceCase()
{
    var suffix = Guid.NewGuid().ToString("N");
    var mutexName = $@"Local\JieYing.Regression.{suffix}";
    var eventName = $@"Local\JieYing.Regression.Activate.{suffix}";
    using var activationReceived = new ManualResetEventSlim(false);
    using var primary = new SingleInstanceCoordinator(
        mutexName,
        eventName,
        activationReceived.Set);
    using var secondary = new SingleInstanceCoordinator(
        mutexName,
        eventName,
        () => { });
    var notified = secondary.NotifyPrimaryInstance(eventName);
    var activated = activationReceived.Wait(TimeSpan.FromSeconds(2));
    Console.WriteLine(
        $"单实例协调 | 首实例 {primary.IsPrimaryInstance} | " +
        $"次实例 {secondary.IsPrimaryInstance} | 唤醒 {notified && activated}");
    if (!primary.IsPrimaryInstance || secondary.IsPrimaryInstance || !notified || !activated)
    {
        failures.Add("单实例互斥或已有窗口唤醒信号失败。 ");
    }
}

void RunNativeWindowAnimationStyleCase()
{
    const long unrelatedStyle = 0x10000000L;
    var style = NativeWindowAnimation.AddRequiredStyles(unrelatedStyle);
    Console.WriteLine($"窗口系统动画样式 | 0x{style:X8}");
    if (!NativeWindowAnimation.HasRequiredStyles(style))
    {
        failures.Add("自定义窗口未补齐 Windows 最小化/还原动画所需的原生样式。");
    }

    if ((style & unrelatedStyle) == 0)
    {
        failures.Add("补齐窗口动画样式时覆盖了已有窗口样式。");
    }
}

BitmapSource CreateCodeContent()
{
    using var bitmap = new Bitmap(Width, ContentHeight, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.FromArgb(25, 27, 29));
    using var lineNumberFont = new Font("Consolas", 9.5f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
    using var codeFont = new Font("Consolas", 13f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
    using var lineBrush = new SolidBrush(Color.FromArgb(92, 99, 108));
    var colors = new[]
    {
        Color.FromArgb(207, 139, 103),
        Color.FromArgb(169, 183, 198),
        Color.FromArgb(86, 156, 214),
        Color.FromArgb(197, 134, 192),
        Color.FromArgb(106, 153, 85)
    };

    for (var line = 1; line <= 220; line++)
    {
        var y = 8 + (line - 1) * 18;
        graphics.DrawString(line.ToString(), lineNumberFont, lineBrush, 8, y + 2);
        var indent = 58 + line % 6 * 15;
        using var codeBrush = new SolidBrush(colors[line % colors.Length]);
        var text = (line % 9) switch
        {
            0 => $"public Long schoolIdForUser{line}(Long userId) {{",
            1 => "if (user == null) return null;",
            2 => "return organizationUnitMapper.selectList(wrapper);",
            3 => "private final UserMapper userMapper;",
            4 => "Set<Long> schools = new LinkedHashSet<>();",
            5 => "while (cursor != null && visited.add(cursor.getId())) {",
            6 => "throw new BusinessException(403, \"no access\");",
            7 => "}",
            _ => "// repeated code structure with different line number"
        };
        graphics.DrawString(text, codeFont, codeBrush, indent, y);
    }

    return ToBitmapSource(bitmap);
}

BitmapSource CreateWebContent()
{
    using var bitmap = new Bitmap(Width, ContentHeight, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.FromArgb(245, 247, 250));
    using var titleFont = new Font("Segoe UI", 22f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
    using var bodyFont = new Font("Segoe UI", 13f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
    for (var section = 0; section < 24; section++)
    {
        var top = 24 + section * 168;
        using var cardBrush = new SolidBrush(section % 2 == 0
            ? Color.White
            : Color.FromArgb(237, 243, 255));
        graphics.FillRectangle(cardBrush, 28, top, Width - 56, 142);
        using var accentBrush = new SolidBrush(Color.FromArgb(49, 124, 245));
        graphics.FillRectangle(accentBrush, 28, top, 6, 142);
        graphics.DrawString($"Section {section + 1}", titleFont, Brushes.Black, 54, top + 18);
        graphics.DrawString(
            $"This is a scrolling web page regression block {section + 1}. " +
            "Cards, text and whitespace must remain continuous.",
            bodyFont,
            Brushes.DimGray,
            new RectangleF(54, top + 58, Width - 105, 64));
    }

    return ToBitmapSource(bitmap);
}

BitmapSource CreateTableContent()
{
    using var bitmap = new Bitmap(Width, ContentHeight, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.White);
    using var font = new Font("Segoe UI", 12f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
    using var gridPen = new Pen(Color.FromArgb(215, 220, 228), 1);
    for (var row = 0; row < 140; row++)
    {
        var y = row * 30;
        if (row % 2 == 1)
        {
            graphics.FillRectangle(Brushes.AliceBlue, 0, y, Width, 30);
        }

        graphics.DrawLine(gridPen, 0, y + 29, Width, y + 29);
        graphics.DrawString($"{row + 1:D3}", font, Brushes.SlateGray, 12, y + 7);
        graphics.DrawString($"item-{row + 1:D3}", font, Brushes.Black, 92, y + 7);
        graphics.DrawString($"status-{row % 5}", font, Brushes.SeaGreen, 310, y + 7);
        graphics.DrawString($"value-{row * 17}", font, Brushes.RoyalBlue, 500, y + 7);
    }

    return ToBitmapSource(bitmap);
}

BitmapSource CreateCardStreamContent()
{
    using var bitmap = new Bitmap(Width, ContentHeight, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.FromArgb(246, 247, 249));
    using var titleFont = new Font("Segoe UI", 11f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
    using var metaFont = new Font("Segoe UI", 9f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
    using var linePen = new Pen(Color.FromArgb(224, 227, 232), 1);
    const int columns = 4;
    const int gap = 12;
    const int left = 18;
    const int cardWidth = 162;
    const int cardHeight = 172;
    const int thumbnailHeight = 108;

    for (var row = 0; row < 25; row++)
    {
        for (var column = 0; column < columns; column++)
        {
            var index = row * columns + column;
            var x = left + column * (cardWidth + gap);
            var y = 18 + row * (cardHeight + gap);
            using var cardBrush = new SolidBrush(Color.White);
            graphics.FillRectangle(cardBrush, x, y, cardWidth, cardHeight);
            var red = 72 + index * 29 % 150;
            var green = 88 + index * 47 % 140;
            var blue = 105 + index * 61 % 130;
            using var thumbnailBrush = new SolidBrush(Color.FromArgb(red, green, blue));
            graphics.FillRectangle(thumbnailBrush, x, y, cardWidth, thumbnailHeight);
            using var accentBrush = new SolidBrush(Color.FromArgb(
                Math.Min(255, red + 30),
                Math.Min(255, green + 25),
                Math.Min(255, blue + 20)));
            graphics.FillEllipse(accentBrush, x + 14 + index % 4 * 9, y + 18, 58, 58);
            graphics.DrawLine(Pens.White, x + 8, y + 92, x + cardWidth - 9, y + 22 + index % 5 * 8);
            graphics.DrawString($"视频 {index + 1:D3} · 动态页面标题", titleFont, Brushes.Black, x + 8, y + 116);
            graphics.DrawString($"UP-{row:D2}-{column}  {index * 137 + 31} 播放", metaFont, Brushes.Gray, x + 8, y + 145);
            graphics.DrawRectangle(linePen, x, y, cardWidth, cardHeight);
        }
    }

    return ToBitmapSource(bitmap);
}

BitmapSource CreateViewport(BitmapSource source, int offset, bool stickyHeader, int dynamicSeed)
{
    var cropped = new CroppedBitmap(source, new Int32Rect(0, offset, Width, FrameHeight));
    cropped.Freeze();
    if (!stickyHeader)
    {
        return cropped;
    }

    var stride = Width * 4;
    var pixels = new byte[stride * FrameHeight];
    cropped.CopyPixels(pixels, stride, 0);
    const int headerHeight = 42;
    for (var y = 0; y < headerHeight; y++)
    {
        for (var x = 0; x < Width; x++)
        {
            var index = y * stride + x * 4;
            pixels[index] = 39;
            pixels[index + 1] = 37;
            pixels[index + 2] = 35;
            pixels[index + 3] = 255;
        }
    }

    // 模拟固定顶栏中会变化的状态点，验证它不会干扰正文匹配。
    var markerX = 20 + dynamicSeed % 5 * 17;
    for (var y = 12; y < 28; y++)
    {
        for (var x = markerX; x < markerX + 12; x++)
        {
            var index = y * stride + x * 4;
            pixels[index] = 230;
            pixels[index + 1] = 160;
            pixels[index + 2] = 70;
        }
    }

    var viewport = BitmapSource.Create(
        Width,
        FrameHeight,
        96,
        96,
        System.Windows.Media.PixelFormats.Bgra32,
        null,
        pixels,
        stride);
    viewport.Freeze();
    return viewport;
}

BitmapSource CreateViewportWithFixedChrome(BitmapSource source, int offset, int dynamicSeed)
{
    const int headerHeight = 64;
    const int footerHeight = 36;
    var cropped = new CroppedBitmap(source, new Int32Rect(0, offset, Width, FrameHeight));
    cropped.Freeze();
    var stride = Width * 4;
    var pixels = new byte[stride * FrameHeight];
    cropped.CopyPixels(pixels, stride, 0);

    for (var y = 0; y < headerHeight; y++)
    {
        for (var x = 0; x < Width; x++)
        {
            var pixel = y * stride + x * 4;
            pixels[pixel] = (byte)(42 + y % 5);
            pixels[pixel + 1] = (byte)(31 + x / 90 % 7);
            pixels[pixel + 2] = 24;
            pixels[pixel + 3] = 255;
        }
    }

    for (var y = FrameHeight - footerHeight; y < FrameHeight; y++)
    {
        for (var x = 0; x < Width; x++)
        {
            var pixel = y * stride + x * 4;
            pixels[pixel] = 32;
            pixels[pixel + 1] = (byte)(45 + x / 70 % 9);
            pixels[pixel + 2] = (byte)(56 + y % 4);
            pixels[pixel + 3] = 255;
        }
    }

    // 模拟固定栏中的时钟或头像轻微变化。
    var markerLeft = 24 + dynamicSeed * 13;
    for (var y = 18; y < 30; y++)
    {
        for (var x = markerLeft; x < markerLeft + 10; x++)
        {
            var pixel = y * stride + x * 4;
            pixels[pixel] = 210;
            pixels[pixel + 1] = 175;
            pixels[pixel + 2] = 92;
        }
    }

    // 模拟右侧滚动条滑块位置随页面变化。
    var thumbTop = 74 + dynamicSeed * 54;
    for (var y = 0; y < FrameHeight; y++)
    {
        for (var x = Width - 7; x < Width; x++)
        {
            var pixel = y * stride + x * 4;
            var isThumb = y >= thumbTop && y < thumbTop + 72;
            pixels[pixel] = isThumb ? (byte)120 : (byte)58;
            pixels[pixel + 1] = isThumb ? (byte)125 : (byte)61;
            pixels[pixel + 2] = isThumb ? (byte)132 : (byte)64;
        }
    }

    var viewport = BitmapSource.Create(
        Width,
        FrameHeight,
        96,
        96,
        System.Windows.Media.PixelFormats.Bgra32,
        null,
        pixels,
        stride);
    viewport.Freeze();
    return viewport;
}

BitmapSource AddRasterizationNoise(BitmapSource source, int amplitude)
{
    var stride = source.PixelWidth * 4;
    var pixels = new byte[stride * source.PixelHeight];
    source.CopyPixels(pixels, stride, 0);
    for (var y = source.PixelHeight / 8; y < source.PixelHeight - source.PixelHeight / 12; y++)
    {
        for (var x = source.PixelWidth / 100; x < source.PixelWidth - source.PixelWidth / 12; x++)
        {
            var index = y * stride + x * 4;
            var luminance = (pixels[index] + pixels[index + 1] + pixels[index + 2]) / 3;
            if (luminance < 45)
            {
                continue;
            }

            var noise = ((x * 17 + y * 31) % (amplitude * 2 + 1)) - amplitude;
            for (var channel = 0; channel < 3; channel++)
            {
                pixels[index + channel] = (byte)Math.Clamp(pixels[index + channel] + noise, 0, 255);
            }
        }
    }

    var noisy = BitmapSource.Create(
        source.PixelWidth,
        source.PixelHeight,
        96,
        96,
        System.Windows.Media.PixelFormats.Bgra32,
        null,
        pixels,
        stride);
    noisy.Freeze();
    return noisy;
}

BitmapSource AddLocalizedInterference(BitmapSource source)
{
    var stride = source.PixelWidth * 4;
    var pixels = new byte[stride * source.PixelHeight];
    source.CopyPixels(pixels, stride, 0);

    // 模拟 IDEA 鼠标光晕、文本光标或短暂悬浮层：只改变约 1% 的视口。
    var left = source.PixelWidth * 2 / 3;
    var top = source.PixelHeight / 2;
    const int size = 58;
    for (var y = top; y < Math.Min(source.PixelHeight, top + size); y++)
    {
        for (var x = left; x < Math.Min(source.PixelWidth, left + size); x++)
        {
            var index = y * stride + x * 4;
            var distance = Math.Abs(x - (left + size / 2)) + Math.Abs(y - (top + size / 2));
            if (distance > size / 2)
            {
                continue;
            }

            pixels[index] = (byte)Math.Clamp(pixels[index] + 35, 0, 255);
            pixels[index + 1] = (byte)Math.Clamp(pixels[index + 1] + 18, 0, 255);
            pixels[index + 2] = (byte)Math.Clamp(pixels[index + 2] + 4, 0, 255);
        }
    }

    var interfered = BitmapSource.Create(
        source.PixelWidth,
        source.PixelHeight,
        96,
        96,
        System.Windows.Media.PixelFormats.Bgra32,
        null,
        pixels,
        stride);
    interfered.Freeze();
    return interfered;
}

BitmapSource AddHorizontalBandInterference(BitmapSource source)
{
    var stride = source.PixelWidth * 4;
    var pixels = new byte[stride * source.PixelHeight];
    source.CopyPixels(pixels, stride, 0);

    // 模拟网页表格 hover 或编辑器当前行高亮：变化横跨正文宽度，
    // 但只占视口高度的一个窄带。
    var top = source.PixelHeight * 11 / 20;
    var bottom = Math.Min(source.PixelHeight, top + source.PixelHeight / 11);
    var left = Math.Max(2, source.PixelWidth / 48);
    var right = source.PixelWidth - left;
    for (var y = top; y < bottom; y++)
    {
        for (var x = left; x < right; x++)
        {
            var index = y * stride + x * 4;
            pixels[index] = (byte)((pixels[index] * 3 + 255) / 4);
            pixels[index + 1] = (byte)((pixels[index + 1] * 3 + 235) / 4);
            pixels[index + 2] = (byte)((pixels[index + 2] * 3 + 215) / 4);
        }
    }

    var interfered = BitmapSource.Create(
        source.PixelWidth,
        source.PixelHeight,
        96,
        96,
        System.Windows.Media.PixelFormats.Bgra32,
        null,
        pixels,
        stride);
    interfered.Freeze();
    return interfered;
}

BitmapSource AddCardStreamDynamicInterference(BitmapSource source, int seed)
{
    var stride = source.PixelWidth * 4;
    var pixels = new byte[stride * source.PixelHeight];
    source.CopyPixels(pixels, stride, 0);

    // 模拟 B 站首页的封面懒加载、轮播帧和局部推广位：每一帧约三分之一
    // 的卡片图片区域发生明显变化，但标题、卡片边界以及其余封面保持稳定。
    const int columns = 4;
    const int cardWidth = 162;
    const int cardHeight = 184;
    const int gap = 12;
    const int left = 18;
    for (var row = -1; row < 4; row++)
    {
        for (var column = 0; column < columns; column++)
        {
            if ((row + column + seed) % 3 != 0)
            {
                continue;
            }

            var xStart = Math.Max(0, left + column * (cardWidth + gap));
            var yStart = Math.Max(42, 18 + row * cardHeight + seed * 13 % 47);
            var xEnd = Math.Min(source.PixelWidth, xStart + cardWidth);
            var yEnd = Math.Min(source.PixelHeight, yStart + 100);
            for (var y = yStart; y < yEnd; y++)
            {
                for (var x = xStart; x < xEnd; x++)
                {
                    var pixel = y * stride + x * 4;
                    pixels[pixel] = (byte)((x * 5 + y * 3 + seed * 41) % 220 + 20);
                    pixels[pixel + 1] = (byte)((x * 2 + y * 7 + seed * 23) % 210 + 30);
                    pixels[pixel + 2] = (byte)((x * 7 + y + seed * 67) % 200 + 40);
                }
            }
        }
    }

    var interfered = BitmapSource.Create(
        source.PixelWidth,
        source.PixelHeight,
        96,
        96,
        System.Windows.Media.PixelFormats.Bgra32,
        null,
        pixels,
        stride);
    interfered.Freeze();
    return interfered;
}

BitmapSource BlendWithVerticalNeighbor(BitmapSource source, double neighborWeight)
{
    var stride = source.PixelWidth * 4;
    var pixels = new byte[stride * source.PixelHeight];
    var blendedPixels = new byte[pixels.Length];
    source.CopyPixels(pixels, stride, 0);
    Array.Copy(pixels, blendedPixels, pixels.Length);

    var ownWeight = 1.0 - neighborWeight;
    for (var y = source.PixelHeight / 10; y < source.PixelHeight - 1; y++)
    {
        for (var x = source.PixelWidth / 120; x < source.PixelWidth - source.PixelWidth / 12; x++)
        {
            var index = y * stride + x * 4;
            var neighborIndex = index + stride;
            for (var channel = 0; channel < 3; channel++)
            {
                blendedPixels[index + channel] = (byte)Math.Clamp(
                    (int)Math.Round(
                        pixels[index + channel] * ownWeight +
                        pixels[neighborIndex + channel] * neighborWeight),
                    0,
                    255);
            }
        }
    }

    var blended = BitmapSource.Create(
        source.PixelWidth,
        source.PixelHeight,
        96,
        96,
        System.Windows.Media.PixelFormats.Bgra32,
        null,
        blendedPixels,
        stride);
    blended.Freeze();
    return blended;
}

BitmapSource ToBitmapSource(Bitmap bitmap)
{
    var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
    var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        var bytes = new byte[data.Stride * data.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        var source = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            bytes,
            data.Stride);
        source.Freeze();
        return source;
    }
    finally
    {
        bitmap.UnlockBits(data);
    }
}

BitmapSource LoadBitmap(string path)
{
    using var stream = File.OpenRead(path);
    var decoder = BitmapDecoder.Create(
        stream,
        BitmapCreateOptions.PreservePixelFormat,
        BitmapCacheOption.OnLoad);
    var frame = decoder.Frames[0];
    frame.Freeze();
    return frame;
}

double CalculateMeanColorDifference(BitmapSource expected, BitmapSource actual)
{
    var stride = expected.PixelWidth * 4;
    var expectedPixels = new byte[stride * expected.PixelHeight];
    var actualPixels = new byte[stride * actual.PixelHeight];
    expected.CopyPixels(expectedPixels, stride, 0);
    actual.CopyPixels(actualPixels, stride, 0);
    long difference = 0;
    for (var index = 0; index < expectedPixels.Length; index += 4)
    {
        difference += Math.Abs(expectedPixels[index] - actualPixels[index]);
        difference += Math.Abs(expectedPixels[index + 1] - actualPixels[index + 1]);
        difference += Math.Abs(expectedPixels[index + 2] - actualPixels[index + 2]);
    }

    return (double)difference / (expected.PixelWidth * expected.PixelHeight * 3L);
}

BitmapSource ScaleBitmap(BitmapSource source, int width, int height)
{
    var visual = new System.Windows.Media.DrawingVisual();
    using (var drawing = visual.RenderOpen())
    {
        drawing.DrawImage(source, new Rect(0, 0, width, height));
    }

    var result = new System.Windows.Media.Imaging.RenderTargetBitmap(
        width,
        height,
        96,
        96,
        System.Windows.Media.PixelFormats.Pbgra32);
    result.Render(visual);
    result.Freeze();
    return result;
}

(byte Red, byte Green, byte Blue) ReadPixel(BitmapSource bitmap, int x, int y)
{
    BitmapSource source = bitmap;
    if (source.Format != System.Windows.Media.PixelFormats.Bgra32 &&
        source.Format != System.Windows.Media.PixelFormats.Pbgra32)
    {
        var converted = new FormatConvertedBitmap(
            source,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            0);
        converted.Freeze();
        source = converted;
    }

    var pixel = new byte[4];
    source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
    return (pixel[2], pixel[1], pixel[0]);
}
