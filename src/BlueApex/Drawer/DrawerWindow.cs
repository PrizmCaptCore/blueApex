using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueApex.Desktop;
using BlueApex.Zones;

namespace BlueApex.Drawer;

/// <summary>
/// The drawer: a full-screen overlay listing every desktop icon by zone, with
/// search, launch on click, pin/unpin, drag between zones and zone management.
/// Opens on the hotkey or from the tray; closes on Esc, on a launch, on a click
/// on the empty background, or when another window takes focus.
/// </summary>
internal sealed class DrawerWindow : Window
{
    private const string DragFormat = "BlueApex.IconId";
    private const int IconPx = 48;

    private readonly DrawerManager _drawer;
    private readonly IconLoader _iconLoader = new();
    private readonly TextBox _search;
    private readonly WrapPanel _cards;
    private readonly List<(FrameworkElement Tile, DesktopIcon Icon, Panel Card)> _tiles = new();
    private Point _dragStart;
    private string? _dragId;

    public DrawerWindow(DrawerManager drawer)
    {
        _drawer = drawer;
        _drawer.Changed += () => { if (IsVisible) Refresh(); };

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x10, 0x10, 0x14));
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "BlueApex";

        _search = new TextBox
        {
            Width = 640,
            FontSize = 20,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 36, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x30)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
        };
        _search.TextChanged += (_, _) => ApplyFilter();

        _cards = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 0, 24, 24) };
        var scroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var hint = new TextBlock
        {
            Text = $"Esc 닫기 · {_drawer.Hotkey} 열고 닫기 · 아이콘 우클릭으로 바탕화면에 꺼내기",
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var root = new DockPanel { Background = Brushes.Transparent };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(hint);
        root.Children.Add(scroll);
        Content = root;

        // A click anywhere that is not a card, the search box or a scrollbar closes the
        // drawer, like tapping outside on a phone. Preview, so it runs before the children.
        root.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            Hide();
            e.Handled = true;
        };
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += OnKey;
    }

    // Whether the element is inside a card, the search box or a scrollbar (all of _cards' children are cards).
    private bool IsInteractive(DependencyObject? element)
    {
        for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == _search || node is System.Windows.Controls.Primitives.ScrollBar) return true;
            if (node is Border border && _cards.Children.Contains(border)) return true;
        }
        return false;
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Open();
    }

    public void Open()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
        _search.Text = "";
        Refresh();
        Show();
        Activate();
        _search.Focus();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var first = _tiles.FirstOrDefault(t => t.Tile.Visibility == Visibility.Visible);
            if (first.Icon != null) Launch(first.Icon);
            e.Handled = true;
        }
    }

    private void Launch(DesktopIcon icon)
    {
        Hide();
        try
        {
            DrawerManager.Launch(icon);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"실행하지 못했습니다: {icon.Name}\n{ex.Message}", "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- building the cards ---

    private void Refresh()
    {
        _cards.Children.Clear();
        _tiles.Clear();
        foreach (var zone in _drawer.Zones)
            _cards.Children.Add(BuildCard(zone.Title, _drawer.IconsOf(zone), zone));
        var unassigned = _drawer.Unassigned.ToList();
        if (unassigned.Count > 0 || _drawer.Zones.Count == 0)
            _cards.Children.Add(BuildCard("기타", unassigned, null));
        _cards.Children.Add(BuildAddCard());
        ApplyFilter();
    }

    private Border BuildCard(string title, IEnumerable<DesktopIcon> icons, Zone? zone)
    {
        var header = new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 6, 10),
        };
        var tiles = new WrapPanel();
        var body = new StackPanel { Children = { header, tiles } };
        var card = new Border
        {
            Width = 440,
            MinHeight = 140,
            Margin = new Thickness(12),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x1E, 0x1E, 0x24)),
            Child = body,
            AllowDrop = true,
            Tag = zone,
        };
        card.Drop += (_, e) =>
        {
            if (e.Data.GetData(DragFormat) is string id) _drawer.MoveToZone(id, zone);
        };
        card.DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;

        if (zone != null)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("이름 바꾸기", () =>
            {
                var name = ZoneMenu.AskTitle(zone.Title);
                if (!string.IsNullOrWhiteSpace(name)) _drawer.RenameZone(zone, name);
            }));
            menu.Items.Add(MenuItem("자동 분류 규칙...", () =>
            {
                var text = ZoneMenu.AskText("자동 분류 규칙: " + zone.Title, string.Join(Environment.NewLine, zone.Patterns), ZoneMenu.PatternHint, multiline: true);
                if (text == null) return;
                _drawer.SetPatterns(zone, text.Split('\n'));
                if (zone.Patterns.Count > 0 && MessageBox.Show("구역에 속하지 않은 아이콘에 지금 적용할까요?", "자동 분류 규칙", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _drawer.ApplyRulesToUnassigned();
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("구역 삭제", () =>
            {
                if (MessageBox.Show($"'{zone.Title}' 구역을 삭제할까요?\n아이콘은 '기타'로 옮겨집니다.", "구역 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _drawer.RemoveZone(zone);
            }));
            header.ContextMenu = menu;
            header.Cursor = Cursors.Hand;
        }

        foreach (var icon in icons)
        {
            var tile = BuildTile(icon);
            tiles.Children.Add(tile);
            _tiles.Add((tile, icon, tiles));
        }
        return card;
    }

    private FrameworkElement BuildTile(DesktopIcon icon)
    {
        var image = new Image { Source = _iconLoader.Get(icon.Id, IconPx), Width = IconPx, Height = IconPx, Margin = new Thickness(0, 6, 0, 4) };
        var name = new TextBlock
        {
            Text = icon.Name,
            Foreground = Brushes.White,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
        };
        var top = new Grid { Children = { image } };
        if (_drawer.IsPinned(icon.Id))
        {
            // Small badge: this icon is also out on the desktop.
            top.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 8, 0),
            });
        }
        var stack = new StackPanel { Children = { top, name } };

        var tile = new Border
        {
            Width = 100,
            Height = 104,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Child = stack,
            Cursor = Cursors.Hand,
            ToolTip = icon.Name,
        };
        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragId = icon.Id; };
        tile.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragId != icon.Id) return;
            var delta = e.GetPosition(this) - _dragStart;
            if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
            _dragId = null;
            DragDrop.DoDragDrop(tile, new DataObject(DragFormat, icon.Id), DragDropEffects.Move);
        };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (_dragId != icon.Id) return;
            _dragId = null;
            Launch(icon);
        };

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(_drawer.IsPinned(icon.Id) ? "서랍에 넣기 (바탕화면에서 치우기)" : "바탕화면에 꺼내기",
            () => { if (_drawer.IsPinned(icon.Id)) _drawer.Unpin(icon.Id); else _drawer.Pin(icon.Id); }));
        var move = new MenuItem { Header = "구역으로 이동" };
        foreach (var zone in _drawer.Zones)
            move.Items.Add(MenuItem(zone.Title, () => _drawer.MoveToZone(icon.Id, zone)));
        move.Items.Add(MenuItem("기타 (구역 없음)", () => _drawer.MoveToZone(icon.Id, null)));
        menu.Items.Add(move);
        if (!icon.Id.StartsWith("::", StringComparison.Ordinal))
            menu.Items.Add(MenuItem("파일 위치 열기", () => DrawerManager.OpenLocation(icon)));
        tile.ContextMenu = menu;
        return tile;
    }

    private Border BuildAddCard()
    {
        var button = new Button
        {
            Content = "+ 새 구역",
            FontSize = 15,
            Padding = new Thickness(18, 10, 18, 10),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        button.Click += (_, _) =>
        {
            var name = ZoneMenu.AskTitle("새 구역");
            if (!string.IsNullOrWhiteSpace(name)) _drawer.AddZone(name);
        };
        return new Border
        {
            Width = 200,
            MinHeight = 140,
            Margin = new Thickness(12),
            Child = button,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        foreach (var (tile, icon, _) in _tiles)
            tile.Visibility = query.Length == 0 || icon.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Hide cards with nothing left to show while searching.
        foreach (var child in _cards.Children.OfType<Border>())
        {
            if (child.Child is not StackPanel body || body.Children.Count < 2 || body.Children[1] is not WrapPanel tiles) continue;
            var any = tiles.Children.OfType<FrameworkElement>().Any(t => t.Visibility == Visibility.Visible);
            child.Visibility = query.Length == 0 || any ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
