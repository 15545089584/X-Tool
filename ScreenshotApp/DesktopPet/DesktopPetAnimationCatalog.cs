using System.IO;
using System.Text.Json;

namespace ScreenshotApp.DesktopPet;

internal sealed class DesktopPetAnimationCatalog
{
    private readonly Dictionary<string, DesktopPetAnimationState> _statesById;

    private DesktopPetAnimationCatalog(
        string assetRoot,
        int canvasWidth,
        int canvasHeight,
        int displaySize,
        int maxCachedStates,
        DesktopPetAnchor anchor,
        IReadOnlyList<DesktopPetAnimationState> states)
    {
        AssetRoot = assetRoot;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        DisplaySize = displaySize;
        MaxCachedStates = maxCachedStates;
        Anchor = anchor;
        States = states;
        _statesById = states.ToDictionary(state => state.Id, StringComparer.OrdinalIgnoreCase);
    }

    internal string AssetRoot { get; }
    internal int CanvasWidth { get; }
    internal int CanvasHeight { get; }
    internal int DisplaySize { get; }
    internal int MaxCachedStates { get; }
    internal DesktopPetAnchor Anchor { get; }
    internal IReadOnlyList<DesktopPetAnimationState> States { get; }

    internal static DesktopPetAnimationCatalog LoadDefault()
    {
        var assetRoot = Path.Combine(AppContext.BaseDirectory, "assets", "desktop-pet");
        return Load(assetRoot);
    }

    internal static DesktopPetAnimationCatalog Load(string assetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        var manifestPath = Path.Combine(assetRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("未找到桌面宠物动画清单。", manifestPath);
        }

        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<DesktopPetManifest>(stream, JsonOptions)
                       ?? throw new InvalidDataException("桌面宠物动画清单为空。");
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException($"不支持的桌面宠物动画清单版本：{manifest.SchemaVersion}。");
        }

        if (manifest.CanvasWidth <= 0 || manifest.CanvasHeight <= 0 || manifest.DisplaySize <= 0)
        {
            throw new InvalidDataException("桌面宠物画布或显示尺寸无效。");
        }

        var states = new List<DesktopPetAnimationState>();
        foreach (var state in manifest.States)
        {
            if (string.IsNullOrWhiteSpace(state.Id) || string.IsNullOrWhiteSpace(state.FrameDirectory) ||
                state.FrameCount <= 0 || state.Fps <= 0)
            {
                throw new InvalidDataException("桌面宠物状态定义不完整。");
            }

            var frameDirectory = Path.GetFullPath(Path.Combine(assetRoot, state.FrameDirectory));
            var normalizedRoot = Path.GetFullPath(assetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!frameDirectory.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"状态 {state.Id} 的帧目录超出资源根目录。");
            }

            var framePaths = Directory.Exists(frameDirectory)
                ? Directory.EnumerateFiles(frameDirectory, "frame-*.png", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            if (framePaths.Length != state.FrameCount)
            {
                throw new InvalidDataException(
                    $"状态 {state.Id} 应包含 {state.FrameCount} 帧，实际找到 {framePaths.Length} 帧。");
            }

            states.Add(new DesktopPetAnimationState(
                state.Id,
                string.IsNullOrWhiteSpace(state.DisplayName) ? state.Id : state.DisplayName,
                state.Fps,
                state.DurationMs,
                state.Loop,
                state.SourceSha256 ?? string.Empty,
                framePaths));
        }

        if (states.Count == 0)
        {
            throw new InvalidDataException("桌面宠物动画清单没有定义任何状态。");
        }

        return new DesktopPetAnimationCatalog(
            Path.GetFullPath(assetRoot),
            manifest.CanvasWidth,
            manifest.CanvasHeight,
            manifest.DisplaySize,
            Math.Max(1, manifest.MaxCachedStates),
            new DesktopPetAnchor(manifest.Anchor.X, manifest.Anchor.Y),
            states);
    }

    internal DesktopPetAnimationState GetState(string stateId)
    {
        if (!_statesById.TryGetValue(stateId, out var state))
        {
            throw new KeyNotFoundException($"未找到桌面宠物状态：{stateId}。");
        }

        return state;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class DesktopPetManifest
    {
        public int SchemaVersion { get; init; }
        public int CanvasWidth { get; init; }
        public int CanvasHeight { get; init; }
        public int DisplaySize { get; init; }
        public int MaxCachedStates { get; init; } = 2;
        public DesktopPetAnchorManifest Anchor { get; init; } = new();
        public List<DesktopPetStateManifest> States { get; init; } = new();
    }

    private sealed class DesktopPetAnchorManifest
    {
        public int X { get; init; }
        public int Y { get; init; }
    }

    private sealed class DesktopPetStateManifest
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string FrameDirectory { get; init; } = string.Empty;
        public int FrameCount { get; init; }
        public int Fps { get; init; }
        public int DurationMs { get; init; }
        public bool Loop { get; init; } = true;
        public string? SourceSha256 { get; init; }
    }
}

internal sealed record DesktopPetAnimationState(
    string Id,
    string DisplayName,
    int Fps,
    int DurationMs,
    bool Loop,
    string SourceSha256,
    IReadOnlyList<string> FramePaths);

internal readonly record struct DesktopPetAnchor(int X, int Y);
