using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenshotApp.DesktopPet;

internal sealed class DesktopPetAnimationPlayer : IDisposable
{
    private readonly Image _target;
    private readonly DesktopPetAnimationCatalog _catalog;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<string, CachedAnimation> _cache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _switchCancellation;
    private long _cacheSequence;
    private CachedAnimation? _current;
    private int _lastFrameIndex = -1;
    private bool _isPlaying;
    private bool _disposed;

    internal DesktopPetAnimationPlayer(Image target, DesktopPetAnimationCatalog catalog)
    {
        _target = target;
        _catalog = catalog;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += Timer_Tick;
    }

    internal event EventHandler<DesktopPetStateChangedEventArgs>? StateChanged;

    internal string? CurrentStateId => _current?.State.Id;

    internal int CurrentFrameIndex => _lastFrameIndex;

    internal long CachedDecodedBytes => _cache.Values.Sum(item => item.DecodedBytes);

    internal async Task SwitchStateAsync(string stateId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = _catalog.GetState(stateId);
        if (_current?.State.Id.Equals(state.Id, StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        _switchCancellation?.Cancel();
        _switchCancellation?.Dispose();
        _switchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _switchCancellation.Token;

        if (!_cache.TryGetValue(state.Id, out var animation))
        {
            var frames = await Task.Run(
                () => LoadFrames(state, _catalog.DisplaySize, token),
                token);
            token.ThrowIfCancellationRequested();
            animation = new CachedAnimation(state, frames, CalculateDecodedBytes(frames), ++_cacheSequence);
            _cache[state.Id] = animation;
            TrimCache(state.Id);
        }
        else
        {
            animation.LastAccess = ++_cacheSequence;
        }

        _current = animation;
        _clock.Restart();
        _lastFrameIndex = 0;
        _target.Source = animation.Frames[0];
        if (_isPlaying)
        {
            _timer.Start();
        }

        StateChanged?.Invoke(this, new DesktopPetStateChangedEventArgs(state, animation.DecodedBytes));
    }

    internal void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _isPlaying = true;
        if (_current is null)
        {
            return;
        }

        if (!_clock.IsRunning)
        {
            _clock.Start();
        }

        _timer.Start();
    }

    internal void Pause()
    {
        if (_disposed)
        {
            return;
        }

        _isPlaying = false;
        _timer.Stop();
        _clock.Stop();
    }

    internal static IReadOnlyList<BitmapSource> LoadFrames(
        DesktopPetAnimationState state,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        var frames = new List<BitmapSource>(state.FramePaths.Count);
        foreach (var framePath in state.FramePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                framePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            frames.Add(bitmap);
        }

        return frames;
    }

    internal static long CalculateDecodedBytes(IReadOnlyList<BitmapSource> frames) =>
        frames.Sum(frame => (long)frame.PixelWidth * frame.PixelHeight * 4);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _switchCancellation?.Cancel();
        _switchCancellation?.Dispose();
        _switchCancellation = null;
        _target.Source = null;
        _cache.Clear();
        _current = null;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_current is null || _current.Frames.Count == 0)
        {
            return;
        }

        var frameIndex = DesktopPetPlaybackMath.GetFrameIndex(
            _clock.Elapsed,
            _current.State.Fps,
            _current.Frames.Count,
            _current.State.Loop);
        if (frameIndex == _lastFrameIndex)
        {
            return;
        }

        _lastFrameIndex = frameIndex;
        _target.Source = _current.Frames[frameIndex];
    }

    private void TrimCache(string incomingStateId)
    {
        while (_cache.Count > _catalog.MaxCachedStates)
        {
            var removable = _cache.Values
                .Where(item => !item.State.Id.Equals(incomingStateId, StringComparison.OrdinalIgnoreCase) &&
                               !item.State.Id.Equals(_current?.State.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.LastAccess)
                .FirstOrDefault();
            if (removable is null)
            {
                return;
            }

            _cache.Remove(removable.State.Id);
        }
    }

    private sealed class CachedAnimation
    {
        internal CachedAnimation(
            DesktopPetAnimationState state,
            IReadOnlyList<BitmapSource> frames,
            long decodedBytes,
            long lastAccess)
        {
            State = state;
            Frames = frames;
            DecodedBytes = decodedBytes;
            LastAccess = lastAccess;
        }

        internal DesktopPetAnimationState State { get; }
        internal IReadOnlyList<BitmapSource> Frames { get; }
        internal long DecodedBytes { get; }
        internal long LastAccess { get; set; }
    }
}

internal static class DesktopPetPlaybackMath
{
    internal static int GetFrameIndex(TimeSpan elapsed, int fps, int frameCount, bool loop)
    {
        if (fps <= 0 || frameCount <= 1)
        {
            return 0;
        }

        var absoluteIndex = Math.Max(0L, (long)Math.Floor(elapsed.TotalSeconds * fps));
        return loop
            ? (int)(absoluteIndex % frameCount)
            : (int)Math.Min(frameCount - 1L, absoluteIndex);
    }
}

internal sealed class DesktopPetStateChangedEventArgs : EventArgs
{
    internal DesktopPetStateChangedEventArgs(DesktopPetAnimationState state, long decodedBytes)
    {
        State = state;
        DecodedBytes = decodedBytes;
    }

    internal DesktopPetAnimationState State { get; }
    internal long DecodedBytes { get; }
}
