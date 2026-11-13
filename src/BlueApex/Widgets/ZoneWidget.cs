using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueApex.Desktop;
using BlueApex.Drawer;
using BlueApex.Games;
using BlueApex.Sdk;
using BlueApex.Ui;
using BlueApex.Zones;

namespace BlueApex.Widgets;

/// <summary>
/// A built-in widget that can do more than the SDK allows a plugin: it is told where
/// inside it the click landed, it may ask for a new size, and part of it can scroll
/// when dragged (the title bar still moves the widget).
/// </summary>
internal interface IInternalWidget : IWidget
{
    /// <summary>Click position in DIPs from the widget's top-left.</summary>
    void OnClickAt(double x, double y);

    /// <summary>The size the content wants right now (DIPs); the host resizes the window to it.</summary>
    (double Width, double Height) DesiredSizeDip { get; }

    /// <summary>Raised when <see cref="DesiredSizeDip"/> changed.</summary>
    event Action? Resized;

    /// <summary>Whether a press here (DIPs) starts a scroll of the content rather than a move of the widget.</summary>
    bool ScrollsAt(double x, double y);

    /// <param name="deltaX">DIPs; positive = finger moved left.</param>
    /// <param name="deltaY">DIPs; positive = finger moved up.</param>
    void ScrollBy(double deltaX, double deltaY);

    /// <param name="notches">Mouse-wheel notches; positive = scroll down.</param>
    void Wheel(int notches);
}

/// <summary>
/// A zone (or the installed games of a launcher) as a panel on the home screen: a 4×3
/// grid of icons that launch on click, dragged up and down to see the rest, kept in
/// step with the drawer. Opened from a card's right-click menu in the drawer.
/// Settings: {"Kind":"zone","Id":"<zone guid>"} or {"Kind":"games","Id":"steam"|"epic"|"gog"|""}.
/// </summary>
internal sealed class ZoneWidgetProvider : IWidgetProvider
{
    private readonly DrawerManager _drawer;

    public ZoneWidgetProvider(DrawerManager drawer) => _drawer = drawer;

    public string Id => "zone";
    public string DisplayName => "구역 (서랍의 카드 우클릭 → 위젯으로 열기)";
    public (int Width, int Height) DefaultSizeDip => ((int)ZoneWidget.WidthDip, (int)ZoneWidget.HeightDip);
    public IWidget Create(IWidgetContext context) => new ZoneWidget(_drawer, context);

    public static string SettingsFor(Zone zone) => JsonSerializer.Serialize(new ZoneWidget.Settings { Kind = "zone", Id = zone.Id.ToString() });
    public static string SettingsForGames(string source) => JsonSerializer.Serialize(new ZoneWidget.Settings { Kind = "games", Id = source });
}

internal sealed class ZoneWidget : IInternalWidget
{
    internal sealed class Settings
    {
        public string Kind { get; set; } = "zone";
        public string Id { get; set; } = "";
        /// <summary>Scroll sideways (columns) instead of down (rows). Null = the kind's default: covers sideways, icons down.</summary>
        public bool? Horizontal { get; set; }
    }

    private const int Columns = 4, Rows = 3, MaxItems = 400;
    private const double TileW = 84, TileH = 92, Pad = 12, PadTop = 10, TitleH = 28;
    private const double CoverGap = 6, CoverH = (TileW - CoverGap) * 1.5; // box art is 2:3
    private const double ButtonW = 26; // the title row's glyph buttons

    /// <summary>Game widgets show box art instead of icon + name.</summary>
    private bool Covers => _settings.Kind == "games";
    private double RowH => Covers ? CoverH + CoverGap : TileH;

    /// <summary>Sideways: tiles fill columns top-to-bottom and the wheel moves one column; otherwise rows and one row.</summary>
    private bool Horizontal => _settings.Horizontal ?? Covers;
    private int RowsFit => Math.Max(1, (int)(ViewportH / RowH));
    private double ViewportW => Columns * TileW;
    public const double WidthDip = Columns * TileW + Pad * 2;
    public const double ViewportH = Rows * TileH;
    public const double HeightDip = PadTop + TitleH + ViewportH + PadTop;

    private readonly DrawerManager _drawer;
    private readonly IWidgetContext _context;
    private readonly Settings _settings;
    private readonly IconLoader _icons = new();
    private readonly TextBlock _title = new() { Foreground = Theme.Text, FontSize = Theme.FontBody, FontWeight = FontWeights.SemiBold, Margin = new Thickness(Theme.Space1, 0, 0, 0), Height = TitleH };
    private readonly WrapPanel _grid = new() { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _thumb = new() { Background = Theme.TextFaint, CornerRadius = new CornerRadius(1.5), Visibility = Visibility.Collapsed };
    private readonly List<DesktopIcon> _shown = new();
    private double _offset, _maxOffset;

    public ZoneWidget(DrawerManager drawer, IWidgetContext context)
    {
        _drawer = drawer;
        _context = context;
        _settings = Parse(context.Settings);
        // The grid is taller than the viewport. It sits on a Canvas (which never clips its
        // children, so every row is drawn) and is slid up by Canvas.Top; the viewport clips.
        // A RenderTransform would not do: WPF clips a child that outgrows its slot, and that
        // clip moves with the transform, leaving the exposed rows blank.
        var track = new Canvas { Children = { _grid } };
        var viewport = new Grid { Height = ViewportH, ClipToBounds = true, Children = { track, _thumb } };
        // Title row: name on the left, two glyph buttons on the right (refresh, settings); OnClickAt knows their spots.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var glyph in new[] { "⟳", "⚙" })
            buttons.Children.Add(new TextBlock
            {
                Text = glyph, Width = ButtonW, Height = TitleH, TextAlignment = TextAlignment.Center,
                FontSize = Theme.FontTitle, Foreground = Theme.TextDim, FontFamily = new FontFamily("Segoe UI Symbol"),
            });
        var titleRow = new Grid { Children = { _title, buttons } };
        var stack = new StackPanel { Children = { titleRow, viewport } };
        View = Parts.Panel(stack, padding: new Thickness(Pad, PadTop, Pad, PadTop));
        DesiredSizeDip = (WidthDip, HeightDip);
        _drawer.Changed += Rebuild;
        Rebuild();
    }

    public FrameworkElement View { get; }
    public (double Width, double Height) DesiredSizeDip { get; }
    public event Action? Resized { add { } remove { } } // the size is fixed

    private static Settings Parse(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? new Settings() : JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch (JsonException)
        {
            return new Settings();
        }
    }

    private Zone? ZoneOrNull => Guid.TryParse(_settings.Id, out var id) ? _drawer.Zones.FirstOrDefault(z => z.Id == id) : null;

    // (title, icons) for what this widget shows.
    private (string Title, List<DesktopIcon> Icons) Source()
    {
        if (_settings.Kind == "games")
        {
            var games = _drawer.Games.Where(g => _drawer.GameOf(g.Id)?.Installed == true
                                               && (_settings.Id.Length == 0 || GameCatalog.SourceOf(g.Id) == _settings.Id)).ToList();
            return (_settings.Id.Length == 0 ? "게임" : GameCatalog.SourceLabel(_settings.Id), games);
        }
        var zone = ZoneOrNull;
        return zone == null ? ("(없어진 구역)", new List<DesktopIcon>()) : (zone.Title, _drawer.IconsOf(zone).ToList());
    }

    private void Rebuild()
    {
        var (title, icons) = Source();
        _shown.Clear();
        _shown.AddRange(icons.Take(MaxItems));
        _title.Text = $"{title}  ({icons.Count})";

        _grid.Children.Clear();
        foreach (var icon in _shown)
            _grid.Children.Add(Covers ? CoverTile(icon) : IconTile(icon));
        if (Horizontal)
        {
            // Columns of RowsFit tiles, growing to the right.
            _grid.Orientation = Orientation.Vertical;
            _grid.Width = double.NaN;
            _grid.Height = RowsFit * RowH;
            var totalCols = (int)Math.Ceiling(_shown.Count / (double)RowsFit);
            _maxOffset = Math.Max(0, totalCols * TileW - ViewportW);
        }
        else
        {
            _grid.Orientation = Orientation.Horizontal;
            _grid.Width = ViewportW;
            _grid.Height = double.NaN;
            var totalRows = (int)Math.Ceiling(_shown.Count / (double)Columns);
            _maxOffset = Math.Max(0, totalRows * RowH - ViewportH);
        }
        SetOffset(_offset);
    }

    // Icon above name, like the drawer.
    private FrameworkElement IconTile(DesktopIcon icon)
    {
        var (tile, image) = Parts.Tile(icon.Name, badge: false);
        tile.Width = TileW;
        tile.Height = TileH;
        tile.Margin = new Thickness(0);
        tile.ToolTip = null; // tooltips cannot show on the desktop layer
        if (icon.IsVirtual)
            image.Source = _icons.GetAsync(icon.Id, (int)Theme.IconSize, false, _context.Dispatcher, ready => image.Source = ready);
        else
            image.Source = _icons.Get(icon.Id, (int)Theme.IconSize);
        return tile;
    }

    // The launcher's box art (2:3), as Steam's own library shows it. Until the art is
    // decoded, and for a game that has none (GOG), the icon and name stand in.
    private FrameworkElement CoverTile(DesktopIcon icon)
    {
        var fallbackImage = new Image { Width = Theme.IconSize, Height = Theme.IconSize, Margin = new Thickness(0, 14, 0, 4) };
        fallbackImage.Source = _icons.GetAsync(icon.Id, (int)Theme.IconSize, false, _context.Dispatcher, ready => fallbackImage.Source = ready);
        var fallback = new StackPanel
        {
            Children =
            {
                fallbackImage,
                new TextBlock
                {
                    Text = icon.Name, Foreground = Theme.Text, FontSize = Theme.FontSmall, TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 48, Margin = new Thickness(4, 0, 4, 0),
                },
            },
        };
        var art = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
        art.Source = _icons.GetAsync(icon.Id, (int)(CoverH * 2), true, _context.Dispatcher, ready =>
        {
            art.Source = ready;
            art.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Collapsed;
        });
        if (art.Source != null)
        {
            art.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Collapsed;
        }
        var box = new Border
        {
            Width = TileW - CoverGap,
            Height = CoverH,
            CornerRadius = new CornerRadius(Theme.RadiusControl),
            Background = Theme.GroupBar,
            ClipToBounds = true,
            Child = new Grid { Children = { fallback, art } },
        };
        return new Border { Width = TileW, Height = RowH, Padding = new Thickness(CoverGap / 2, 0, CoverGap / 2, CoverGap), Child = box };
    }

    private void SetOffset(double offset)
    {
        _offset = Math.Clamp(offset, 0, _maxOffset);
        Canvas.SetLeft(_grid, Horizontal ? -_offset : 0);
        Canvas.SetTop(_grid, Horizontal ? 0 : -_offset);
        if (_maxOffset <= 0)
        {
            _thumb.Visibility = Visibility.Collapsed;
            return;
        }
        // A thin thumb (right edge, or bottom edge when sideways) shows where in the list the viewport is.
        var viewport = Horizontal ? ViewportW : ViewportH;
        var thumb = Math.Max(16, viewport * viewport / (_maxOffset + viewport));
        var along = (viewport - thumb) * (_offset / _maxOffset);
        if (Horizontal)
        {
            _thumb.Width = thumb; _thumb.Height = 3;
            _thumb.HorizontalAlignment = HorizontalAlignment.Left; _thumb.VerticalAlignment = VerticalAlignment.Bottom;
            _thumb.Margin = new Thickness(along, 0, 0, 0);
        }
        else
        {
            _thumb.Width = 3; _thumb.Height = thumb;
            _thumb.HorizontalAlignment = HorizontalAlignment.Right; _thumb.VerticalAlignment = VerticalAlignment.Top;
            _thumb.Margin = new Thickness(0, along, 0, 0);
        }
        _thumb.Visibility = Visibility.Visible;
    }

    public void OnClick() { }

    public bool ScrollsAt(double x, double y) => y >= PadTop + TitleH && _maxOffset > 0;

    public void ScrollBy(double deltaX, double deltaY) => SetOffset(_offset + (Horizontal ? deltaX : deltaY));

    /// <summary>One wheel notch moves one row of tiles (one column when sideways).</summary>
    public void Wheel(int notches) => SetOffset(_offset + notches * (Horizontal ? TileW : RowH));

    public void OnClickAt(double x, double y)
    {
        if (y < PadTop + TitleH)
        {
            // The glyph buttons at the title row's right end: [⟳][⚙].
            var fromRight = WidthDip - Pad - x;
            if (fromRight >= 0 && fromRight < ButtonW) OpenSettings();
            else if (fromRight >= ButtonW && fromRight < ButtonW * 2) Refresh();
            return;
        }
        var gx = x - Pad + (Horizontal ? _offset : 0);
        var gy = y - PadTop - TitleH + (Horizontal ? 0 : _offset);
        if (gx < 0 || gy < 0) return;
        var col = (int)(gx / TileW);
        var row = (int)(gy / RowH);
        int index;
        if (Horizontal)
        {
            if (row >= RowsFit) return;
            index = col * RowsFit + row;
        }
        else
        {
            if (col >= Columns) return;
            index = row * Columns + col;
        }
        if (index < 0 || index >= _shown.Count) return;
        try
        {
            DrawerManager.Launch(_shown[index]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _context.Log($"launch failed: {_shown[index].Name}: {ex.Message}");
        }
    }

    public IEnumerable<WidgetMenuItem> MenuItems
    {
        get
        {
            yield return new WidgetMenuItem("설정 (이미지 선택, 스크롤 방향)...", OpenSettings);
            yield return new WidgetMenuItem("라이브러리 다시 읽기", Refresh);
            yield return new WidgetMenuItem(Horizontal ? "세로로 스크롤" : "가로로 스크롤", () => SetHorizontal(!Horizontal));
        }
    }

    private void SetHorizontal(bool horizontal)
    {
        _settings.Horizontal = horizontal;
        _context.Settings = JsonSerializer.Serialize(_settings);
        _offset = 0;
        Rebuild();
    }

    /// <summary>Re-reads the launchers (games) or the desktop (zones); Changed then rebuilds the grid.</summary>
    private void Refresh()
    {
        if (Covers) _drawer.RescanGames();
        else _drawer.PollNow();
    }

    private void OpenSettings()
    {
        var (title, icons) = Source();
        ZoneWidgetSettingsDialog.Show(_drawer, title, icons, Horizontal, SetHorizontal, Refresh);
    }

    public void Dispose() => _drawer.Changed -= Rebuild;
}
