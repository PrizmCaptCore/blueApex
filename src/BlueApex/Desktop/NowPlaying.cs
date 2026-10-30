using System.IO;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace BlueApex.Desktop;

/// <summary>What is playing right now, as Windows' media controls report it.</summary>
/// <param name="AppId">The source app's AppUserModelID or exe name ("Spotify.exe", "chrome", "Microsoft.ZuneMusic_...!...").</param>
/// <param name="Art">The thumbnail's bytes (decoded on the UI thread by whoever shows it), or null.</param>
internal sealed record NowPlayingInfo(string Title, string Artist, bool Playing, byte[]? Art, string AppId);

/// <summary>
/// The current media session from Windows' system media transport controls (the same source as the
/// volume flyout's media box): Spotify, browsers, the Media Player... <see cref="Changed"/> fires on the
/// UI thread whenever the session, the track or play/pause changes; <see cref="Current"/> is null when
/// nothing is playing or paused.
/// </summary>
internal sealed class NowPlaying : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public NowPlayingInfo? Current { get; private set; }

    public event Action? Changed;

    public NowPlaying() => _ = StartAsync();

    private async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.BeginInvoke(() => Attach(_manager.GetCurrentSession()));
            await _dispatcher.InvokeAsync(() => Attach(_manager.GetCurrentSession()));
        }
        catch (Exception ex)
        {
            Log.Write($"media controls unavailable: {ex.Message}");
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnChanged;
            _session.PlaybackInfoChanged -= OnChanged;
        }
        _session = session;
        if (session == null)
        {
            Publish(null);
            return;
        }
        session.MediaPropertiesChanged += OnChanged;
        session.PlaybackInfoChanged += OnChanged;
        _ = RefreshAsync();
    }

    private void OnChanged(object? sender, object args) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session == null || _disposed) return;
        try
        {
            var media = await session.TryGetMediaPropertiesAsync();
            var status = session.GetPlaybackInfo()?.PlaybackStatus;
            if (status is not (GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                || string.IsNullOrWhiteSpace(media.Title))
            {
                Publish(null); // stopped or closed: the dock hides the media slot
                return;
            }
            var art = media.Thumbnail != null ? await ReadAsync(media.Thumbnail) : null;
            Publish(new NowPlayingInfo(media.Title, media.Artist ?? "", status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                art, session.SourceAppUserModelId ?? ""));
        }
        catch (Exception ex)
        {
            Log.Write($"media refresh failed: {ex.Message}");
        }
    }

    private void Publish(NowPlayingInfo? info) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        Current = info;
        Changed?.Invoke();
    });

    // Only the bytes are read here (any thread); the image is built on the UI thread by the caller.
    private static async Task<byte[]?> ReadAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(memory);
            return memory.Length > 0 ? memory.ToArray() : null;
        }
        catch (Exception)
        {
            return null; // no art is fine
        }
    }

    public void TogglePlayPause() => _ = _session?.TryTogglePlayPauseAsync();
    public void Next() => _ = _session?.TrySkipNextAsync();
    public void Previous() => _ = _session?.TrySkipPreviousAsync();

    public void Dispose()
    {
        _disposed = true;
        Attach(null);
    }
}
