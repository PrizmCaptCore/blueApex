using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueApex.Desktop;
using BlueApex.Games;
using BlueApex.Sdk;
using BlueApex.Ui;

namespace BlueApex.Widgets;

/// <summary>
/// Spotify on the home screen: the user's playlists (click one to see its tracks), liked songs and search,
/// as a scrolling list with covers. Clicking a track plays it on the built-in player (see Desktop/Spotify.cs);
/// what plays then shows in the dock's media slot. Offered in "위젯 추가" only once a Client ID is saved.
/// </summary>
internal sealed class SpotifyWidgetProvider : IWidgetProvider
{
    private readonly Spotify _spotify;

    public SpotifyWidgetProvider(Spotify spotify) => _spotify = spotify;

    public string Id => "spotify";
    public string DisplayName => "Spotify (재생목록 · 좋아요 · 검색)";
    public (int Width, int Height) DefaultSizeDip => ((int)SpotifyWidget.WidthDip, (int)SpotifyWidget.HeightDip);
    public IWidget Create(IWidgetContext context) => new SpotifyWidget(_spotify, context);
}

internal sealed class SpotifyWidget : IInternalWidget
{
    private enum Tab { Playlists, Liked, Search }

    private static readonly string[] TabNames = { "재생목록", "좋아요", "검색" };

    // Layout in DIPs. A widget gets no mouse events of its own, only "clicked at (x, y)", so OnClickAt works
    // out what was hit from these same numbers: change one here and both the drawing and the clicks follow.
    private const double Pad = 12, PadTop = 10, TabsH = 28, SubH = 24, RowH = 44, StatusH = 20;
    private const double TabW = 68, ButtonW = 26, PlayAllW = 86, ArtSize = 36;
    private const int Rows = 8;
    public const double WidthDip = 320;
    private const double ListW = WidthDip - Pad * 2;
    private const double ViewportH = Rows * RowH;
    private const double ListTop = PadTop + TabsH + SubH;
    public const double HeightDip = ListTop + ViewportH + StatusH + PadTop;

    // A few covers at a time: a list is up to 50 rows and each cover is a download the first time.
    private static readonly SemaphoreSlim ArtGate = new(4, 4);

    private readonly Spotify _spotify;
    private readonly IWidgetContext _context;
    private readonly TextBlock[] _tabLabels = new TextBlock[TabNames.Length];
    private readonly TextBlock _sub = new() { Foreground = Theme.TextDim, FontSize = Theme.FontSmall, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _playAll = new() { Text = "▶ 전체 재생", Foreground = Theme.Text, FontSize = Theme.FontSmall, Width = PlayAllW, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly StackPanel _list = new() { Width = ListW };
    private readonly TextBlock _empty = new() { Foreground = Theme.TextDim, FontSize = Theme.FontBody, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.Space3) };
    private readonly Border _thumb = new() { Background = Theme.TextFaint, CornerRadius = new CornerRadius(1.5), Width = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { Foreground = Theme.TextFaint, FontSize = Theme.FontSmall, TextTrimming = TextTrimming.CharacterEllipsis, Height = StatusH, Padding = new Thickness(0, 3, 0, 0) };
    private readonly Dictionary<string, ImageSource> _art = new();

    private Tab _tab = Tab.Playlists;
    private SpotifyItem? _playlist;            // the playlist whose tracks are listed; null = a tab's own list
    private List<SpotifyItem> _items = new();  // the rows, top to bottom
    private string _query = "";
    private double _offset, _maxOffset;
    private int _load;                         // counts loads, so an answer that arrives late for an old view is dropped
    private bool _busy;                        // a "play" is on its way; further clicks wait
    private bool _disposed;

    public SpotifyWidget(Spotify spotify, IWidgetContext context)
    {
        _spotify = spotify;
        _context = context;

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < TabNames.Length; i++)
        {
            _tabLabels[i] = new TextBlock { Text = TabNames[i], Width = TabW, Height = TabsH, FontSize = Theme.FontBody, Padding = new Thickness(Theme.Space1, 4, 0, 0) };
            tabs.Children.Add(_tabLabels[i]);
        }
        var refresh = new TextBlock
        {
            Text = "⟳", Width = ButtonW, Height = TabsH, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = Theme.FontTitle, Foreground = Theme.TextDim, FontFamily = new FontFamily("Segoe UI Symbol"),
        };
        var tabsRow = new Grid { Height = TabsH, Children = { tabs, refresh } };
        var subRow = new Grid { Height = SubH, Children = { _sub, _playAll } };
        // The list is taller than the viewport: it sits on a Canvas and is slid up by Canvas.Top while the
        // viewport clips (the same trick as ZoneWidget, where the reason is explained).
        var viewport = new Grid { Height = ViewportH, ClipToBounds = true, Children = { new Canvas { Children = { _list } }, _empty, _thumb } };
        View = Parts.Panel(new StackPanel { Children = { tabsRow, subRow, viewport, _status } }, padding: new Thickness(Pad, PadTop, Pad, PadTop));
        Select(Tab.Playlists);
    }

    public FrameworkElement View { get; }
    public (double Width, double Height) DesiredSizeDip => (WidthDip, HeightDip);
    public event Action? Resized { add { } remove { } } // the size is fixed

    // --- what is shown ---

    private void Select(Tab tab)
    {
        _tab = tab;
        _playlist = null;
        for (var i = 0; i < _tabLabels.Length; i++)
        {
            _tabLabels[i].Foreground = i == (int)tab ? Theme.Text : Theme.TextDim;
            _tabLabels[i].FontWeight = i == (int)tab ? FontWeights.SemiBold : FontWeights.Normal;
        }
        Reload();
    }

    // Reads the current view again from Spotify.
    private void Reload()
    {
        if (!_spotify.Linked)
        {
            _load++;
            Show(new List<SpotifyItem>(), "", "트레이 메뉴 \"Spotify 연결 (고급)...\"에서 연결하면 여기에 재생목록이 나옵니다. 연결한 뒤 ⟳를 눌러 주세요.");
            return;
        }
        if (_playlist is { } playlist)
        {
            Load(() => _spotify.PlaylistTracksAsync(playlist), "‹ " + playlist.Title, "이 재생목록에는 곡이 없습니다.");
            return;
        }
        switch (_tab)
        {
            case Tab.Playlists:
                Load(_spotify.PlaylistsAsync, "누르면 곡 목록이 열립니다", "라이브러리에 재생목록이 없습니다.");
                break;
            case Tab.Liked:
                Load(_spotify.LikedAsync, "최근 50곡 · 누른 곡부터 이어서 재생", "좋아요 표시한 곡이 없습니다.");
                break;
            default:
                if (_query.Length == 0) Show(new List<SpotifyItem>(), "", "\"검색\"을 누르면 찾을 말을 물어봅니다.");
                else Load(() => _spotify.SearchAsync(_query), $"\"{_query}\" · 다시 찾으려면 \"검색\"을 누르세요", "찾은 것이 없습니다.");
                break;
        }
    }

    // "async void": nothing waits for a load, so a failure is caught here and shown instead of escaping.
    private async void Load(Func<Task<List<SpotifyItem>>> source, string sub, string empty)
    {
        var load = ++_load;
        Show(new List<SpotifyItem>(), sub, "불러오는 중...");
        try
        {
            var items = await source();
            if (load != _load || _disposed) return; // the user moved on to another view meanwhile
            Show(items, sub, items.Count == 0 ? empty : "");
        }
        catch (SpotifyException ex)
        {
            if (load == _load && !_disposed) Show(new List<SpotifyItem>(), sub, ex.Message);
        }
    }

    private void Show(List<SpotifyItem> items, string sub, string message)
    {
        _items = items;
        _sub.Text = sub;
        _playAll.Visibility = _playlist != null ? Visibility.Visible : Visibility.Collapsed;
        _sub.Margin = new Thickness(0, 0, _playlist != null ? PlayAllW : 0, 0);
        _empty.Text = message;
        _empty.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Children.Clear();
        foreach (var item in items) _list.Children.Add(Row(item));
        _maxOffset = Math.Max(0, items.Count * RowH - ViewportH);
        SetOffset(0);
    }

    private FrameworkElement Row(SpotifyItem item)
    {
        var art = new Border { Width = ArtSize, Height = ArtSize, CornerRadius = new CornerRadius(Theme.Space1), Background = Theme.GroupBar };
        if (item.Image is { } url) LoadArt(art, url);
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Theme.Space2, 0, Theme.Space2, 0),
            Children =
            {
                new TextBlock { Text = item.Title, Foreground = Theme.Text, FontSize = Theme.FontBody, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = item.Subtitle, Foreground = Theme.TextDim, FontSize = Theme.FontSmall, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        var row = new DockPanel { Height = RowH, Width = ListW };
        DockPanel.SetDock(art, Dock.Left);
        row.Children.Add(art);
        row.Children.Add(text);
        return row;
    }

    // Covers are downloaded (kept on disk by ImageCache) and decoded off the UI thread, then put in place.
    private async void LoadArt(Border target, string url)
    {
        if (_art.TryGetValue(url, out var cached))
        {
            target.Background = new ImageBrush(cached) { Stretch = Stretch.UniformToFill };
            return;
        }
        await ArtGate.WaitAsync();
        ImageSource? image;
        try
        {
            image = await Task.Run(() => Decode(ImageCache.Fetch(url)));
        }
        finally
        {
            ArtGate.Release();
        }
        if (image == null || _disposed) return;
        _art[url] = image;
        target.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
    }

    private static ImageSource? Decode(string? file)
    {
        if (file == null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read fully now, so the file is not kept open
            image.UriSource = new Uri(file);
            image.DecodePixelWidth = (int)(ArtSize * 3);
            image.EndInit();
            image.Freeze(); // frozen = may be handed from this worker thread to the UI thread
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or UriFormatException)
        {
            return null; // not a decodable picture
        }
    }

    private void SetOffset(double offset)
    {
        _offset = Math.Clamp(offset, 0, _maxOffset);
        Canvas.SetTop(_list, -_offset);
        if (_maxOffset <= 0)
        {
            _thumb.Visibility = Visibility.Collapsed;
            return;
        }
        // A thin thumb on the right edge shows where in the list the viewport is.
        _thumb.Height = Math.Max(16, ViewportH * ViewportH / (_maxOffset + ViewportH));
        _thumb.Margin = new Thickness(0, (ViewportH - _thumb.Height) * (_offset / _maxOffset), 0, 0);
        _thumb.Visibility = Visibility.Visible;
    }

    // --- input ---

    public void OnClick() { }

    public bool ScrollsAt(double x, double y) => y >= ListTop && y < ListTop + ViewportH && _maxOffset > 0;

    public void ScrollBy(double deltaX, double deltaY) => SetOffset(_offset + deltaY);

    /// <summary>One wheel notch moves two rows.</summary>
    public void Wheel(int notches) => SetOffset(_offset + notches * RowH * 2);

    public void OnClickAt(double x, double y)
    {
        if (y >= PadTop && y < PadTop + TabsH)
        {
            if (WidthDip - Pad - x is >= 0 and < ButtonW) Reload(); // ⟳ at the right end
            else if (x >= Pad && (int)((x - Pad) / TabW) is var tab && tab < TabNames.Length) ClickTab((Tab)tab);
            return;
        }
        if (y >= PadTop + TabsH && y < ListTop)
        {
            if (_playlist is not { } open) return;
            if (x >= WidthDip - Pad - PlayAllW) Play(() => _spotify.PlayAsync(open), open.Title); // "▶ 전체 재생"
            else Select(Tab.Playlists);                                                           // "‹ name": back to the playlists
            return;
        }
        if (y < ListTop || y >= ListTop + ViewportH) return;
        var index = (int)((y - ListTop + _offset) / RowH);
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        if (_playlist is { } inside) Play(() => _spotify.PlayAsync(inside, item), item.Title);
        else if (_tab == Tab.Playlists) Open(item);
        else if (_tab == Tab.Liked)
        {
            var tracks = _items;
            Play(() => _spotify.PlayAsync(tracks, index), item.Title);
        }
        else Play(() => _spotify.PlayAsync(item), item.Title);
    }

    private void ClickTab(Tab tab)
    {
        if (tab == Tab.Search)
        {
            // A widget cannot be typed into (it sits on the desktop layer), so the words come from a small dialog.
            var words = _context.AskText("Spotify 검색", _query, "곡·앨범·재생목록 이름", multiline: false)?.Trim();
            if (string.IsNullOrEmpty(words)) return;
            _query = words;
        }
        Select(tab);
    }

    // A playlist's tracks can only be listed when the user made it (Spotify's rule). For the others the
    // list comes back empty or refused; then the playlist is simply played, which always works.
    private async void Open(SpotifyItem playlist)
    {
        var load = ++_load;
        _status.Text = "여는 중: " + playlist.Title;
        List<SpotifyItem> tracks;
        try
        {
            tracks = await _spotify.PlaylistTracksAsync(playlist);
        }
        catch (SpotifyException)
        {
            tracks = new List<SpotifyItem>();
        }
        if (load != _load || _disposed) return;
        if (tracks.Count == 0)
        {
            Play(() => _spotify.PlayAsync(playlist), playlist.Title + " (곡 목록은 내가 만든 재생목록만 볼 수 있습니다)");
            return;
        }
        _status.Text = "";
        _playlist = playlist;
        Show(tracks, "‹ " + playlist.Title, "");
    }

    private async void Play(Func<Task> play, string what)
    {
        if (_busy) return; // one at a time: two "play"s racing made the player start twice
        _busy = true;
        _status.Text = "Spotify에 보내는 중...";
        try
        {
            await play();
            if (!_disposed) _status.Text = "재생: " + what;
        }
        catch (SpotifyException ex)
        {
            if (!_disposed) _status.Text = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    public IEnumerable<WidgetMenuItem> MenuItems
    {
        get
        {
            yield return new WidgetMenuItem("새로 고침", Reload);
        }
    }

    public void Dispose() => _disposed = true;
}
