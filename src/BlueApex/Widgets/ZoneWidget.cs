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

    /// <param name="deltaY">DIPs; positive scrolls the content up.</param>
    void ScrollBy(double deltaY);

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
    }

    private const int Columns = 4, Rows = 3, MaxItems = 400;
    private const double TileW = 84, TileH = 92, Pad = 12, PadTop = 10, TitleH = 28;
    public const double WidthDip = Columns * TileW + Pad * 2;
    public const double ViewportH = Rows * TileH;
    public const double HeightDip = PadTop + TitleH + ViewportH + PadTop;

    private readonly DrawerManager _drawer;
    private readonly IWidgetContext _context;
    private readonly Settings _settings;
    private readonly IconLoader _icons = new();
    private readonly TextBlock _title = new() { Foreground = Theme.Text, FontSize = Theme.FontBody, FontWeight = FontWeights.SemiBold, Margin = new Thickness(Theme.Space1, 0, 0, 0), Height = TitleH };
    private readonly WrapPanel _grid = new() { Width = Columns * TileW, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _thumb = new() { Width = 3, Background = Theme.TextFaint, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
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
        var stack = new StackPanel { Children = { _title, viewport } };
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
            _grid.Children.Add(tile);
        }
        var totalRows = (int)Math.Ceiling(_shown.Count / (double)Columns);
        _maxOffset = Math.Max(0, totalRows * TileH - ViewportH);
        SetOffset(_offset);
    }

    private void SetOffset(double offset)
    {
        _offset = Math.Clamp(offset, 0, _maxOffset);
        Canvas.SetTop(_grid, -_offset);
        if (_maxOffset <= 0)
        {
            _thumb.Visibility = Visibility.Collapsed;
            return;
        }
        // A thin thumb at the right edge shows where in the list the viewport is.
        var contentH = _maxOffset + ViewportH;
        _thumb.Height = Math.Max(16, ViewportH * ViewportH / contentH);
        _thumb.Margin = new Thickness(0, (ViewportH - _thumb.Height) * (_offset / _maxOffset), 0, 0);
        _thumb.Visibility = Visibility.Visible;
    }

    public void OnClick() { }

    public bool ScrollsAt(double x, double y) => y >= PadTop + TitleH && _maxOffset > 0;

    public void ScrollBy(double deltaY) => SetOffset(_offset + deltaY);

    /// <summary>One wheel notch moves one row of tiles.</summary>
    public void Wheel(int notches) => SetOffset(_offset + notches * TileH);

    public void OnClickAt(double x, double y)
    {
        var gx = x - Pad;
        var gy = y - PadTop - TitleH + _offset;
        if (gx < 0 || gy < 0 || y < PadTop + TitleH) return;
        var col = (int)(gx / TileW);
        var row = (int)(gy / TileH);
        if (col >= Columns) return;
        var index = row * Columns + col;
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

    public IEnumerable<WidgetMenuItem> MenuItems => Array.Empty<WidgetMenuItem>();

    public void Dispose() => _drawer.Changed -= Rebuild;
}
