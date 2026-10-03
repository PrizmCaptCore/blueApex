using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueApex.Sdk;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MediaWidget;

/// <summary>
/// What is playing right now, from the system media transport controls (the
/// same source as the volume flyout): title, artist, album art, state. Click
/// toggles play/pause; the menu skips tracks.
/// </summary>
public sealed class MediaProvider : IWidgetProvider
{
    public string Id => "media";
    public string DisplayName => "미디어 (지금 재생 중)";
    public (int Width, int Height) DefaultSizeDip => (320, 110);
    public IWidget Create(IWidgetContext context) => new MediaWidget(context);
}

internal sealed class MediaWidget : IWidget
{
    private readonly IWidgetContext _context;
    private readonly Image _art = new() { Width = 72, Height = 72, Stretch = Stretch.UniformToFill };
    private readonly TextBlock _title = new() { Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { Foreground = Brushes.White, Opacity = 0.75, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _state = new() { Foreground = Brushes.White, Opacity = 0.5, FontSize = 12 };

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public MediaWidget(IWidgetContext context)
    {
        _context = context;
        var artFrame = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 14, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Child = _art, ClipToBounds = true,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _artist, _state } };
        View = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new DockPanel { Children = { artFrame, text } },
        };
        DockPanel.SetDock(artFrame, Dock.Left);
        Show("재생 중인 미디어 없음", "", "");
        _ = ConnectAsync();
    }

    public FrameworkElement View { get; }

    public IEnumerable<WidgetMenuItem> MenuItems => new[]
    {
        new WidgetMenuItem("재생 / 일시정지", () => _ = _session?.TryTogglePlayPauseAsync()),
        new WidgetMenuItem("다음 곡", () => _ = _session?.TrySkipNextAsync()),
        new WidgetMenuItem("이전 곡", () => _ = _session?.TrySkipPreviousAsync()),
    };

    public void OnClick() => _ = _session?.TryTogglePlayPauseAsync();

    private async Task ConnectAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Attach(_manager.GetCurrentSession());
            Attach(_manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            _context.Log($"media session manager unavailable: {ex.Message}");
            Show("미디어 정보를 사용할 수 없음", "", "");
        }
    }

    // Sessions come and go as players start and stop; follow the one Windows calls current.
    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnChanged;
            _session.PlaybackInfoChanged -= OnChanged;
        }
        _session = session;
        if (_session == null)
        {
            Show("재생 중인 미디어 없음", "", "");
            return;
        }
        _session.MediaPropertiesChanged += OnChanged;
        _session.PlaybackInfoChanged += OnChanged;
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
            var state = status switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "재생 중",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "일시정지",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "정지",
                _ => "",
            };
            var app = session.SourceAppUserModelId;
            var art = media.Thumbnail != null ? await LoadArtAsync(media.Thumbnail) : null;
            Show(string.IsNullOrWhiteSpace(media.Title) ? "(제목 없음)" : media.Title, media.Artist ?? "", $"{state}  ·  {Friendly(app)}", art);
        }
        catch (Exception ex)
        {
            _context.Log($"media refresh failed: {ex.Message}");
        }
    }

    // Only the bytes are read here (any thread); the WPF image is built on the UI thread in Show.
    private static async Task<byte[]?> LoadArtAsync(IRandomAccessStreamReference reference)
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

    private static BitmapImage? ToImage(byte[]? bytes)
    {
        if (bytes == null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = 144;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException)
        {
            return null; // not a decodable image
        }
    }

    // "Spotify.exe" / "Chrome.exe" / "Microsoft.ZuneMusic_8wekyb3d8bbwe!..." -> something readable.
    private static string Friendly(string appId)
    {
        var name = appId.Split('!')[0].Split('_')[0];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private void Show(string title, string artist, string state, byte[]? art = null) => _context.Dispatcher.Invoke(() =>
    {
        _title.Text = title;
        _artist.Text = artist;
        _state.Text = state;
        _art.Source = ToImage(art);
    });

    public void Dispose()
    {
        _disposed = true;
        Attach(null);
    }
}
