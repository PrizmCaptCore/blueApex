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
    private readonly StackPanel _dropBar;
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
            Text = $"Esc 닫기 · {_drawer.Hotkey} 열고 닫기 · Ctrl/Shift+클릭으로 여러 개 선택 · 끌어서 구역 이동·꺼내기·삭제",
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };

        // Drop targets that appear only while an icon is being dragged.
        _dropBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
            Visibility = Visibility.Collapsed,
            Children =
            {
                BuildDropTarget("바탕화면에 꺼내기", Color.FromRgb(0x2E, 0x7D, 0xD6), ids =>
                {
                    foreach (var id in ids) _drawer.Pin(id);
                }),
                BuildDropTarget("휴지통으로", Color.FromRgb(0xC0, 0x39, 0x2B), ids =>
                    ConfirmDelete(ids.Select(id => _drawer.Icons.FirstOrDefault(i => i.Id == id)).OfType<DesktopIcon>().Where(i => !i.IsShellItem).ToList())),
            },
        };

        var root = new DockPanel { Background = Brushes.Transparent };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        DockPanel.SetDock(_dropBar, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(hint);
        root.Children.Add(_dropBar);
        root.Children.Add(scroll);
        // The marquee (rubber-band) rectangle is drawn on an overlay above everything.
        _marquee = new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xAF, 0xF5)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x4C, 0xAF, 0xF5)),
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
        };
        var overlay = new Canvas { IsHitTestVisible = false, Children = { _marquee } };
        _root = root;
        Content = new Grid { Children = { root, overlay } };

        // Pressing on empty space (background or a card's gaps) starts a rubber-band
        // selection if the mouse moves; a plain click on the background closes the
        // drawer, like tapping outside on a phone. Preview, so it runs before the children.
        root.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var hit = Classify(e.OriginalSource as DependencyObject);
            if (hit is Hit.Tile or Hit.Control) return;
            _pressHit = hit;
            _pressPoint = e.GetPosition(root);
            _marqueeActive = false;
            e.Handled = true;
        };
        root.PreviewMouseMove += (_, e) =>
        {
            if (_pressHit == null || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(root);
            if (!_marqueeActive)
            {
                if (Math.Abs(point.X - _pressPoint.X) < 6 && Math.Abs(point.Y - _pressPoint.Y) < 6) return;
                _marqueeActive = true;
                _marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? _selected.ToHashSet() : new HashSet<string>();
                _marquee.Visibility = Visibility.Visible;
                root.CaptureMouse();
            }
            UpdateMarquee(point);
        };
        root.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_pressHit == null) return;
            var hit = _pressHit;
            _pressHit = null;
            if (_marqueeActive)
            {
                _marqueeActive = false;
                _marquee.Visibility = Visibility.Collapsed;
                root.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            if (hit == Hit.Background) Hide();
            else ClearSelection(); // a plain click in a card's gap
            e.Handled = true;
        };
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += OnKey;
    }

    private enum Hit { Background, Card, Tile, Control }

    private DockPanel _root = null!;
    private System.Windows.Shapes.Rectangle _marquee = null!;
    private Hit? _pressHit;
    private Point _pressPoint;
    private bool _marqueeActive;
    private HashSet<string> _marqueeBase = new();

    // What a press landed on: an icon tile, a control (search, scrollbar, drop bar, button),
    // a card's empty area, or the bare background.
    private Hit Classify(DependencyObject? element)
    {
        for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == _search || node == _dropBar || node is System.Windows.Controls.Primitives.ScrollBar || node is Button) return Hit.Control;
            if (node is Border border)
            {
                if (_tileById.ContainsValue(border)) return Hit.Tile;
                if (_cards.Children.Contains(border)) return Hit.Card;
            }
        }
        return Hit.Background;
    }

    // Selects every visible tile the rectangle touches (plus the Ctrl-held base selection).
    private void UpdateMarquee(Point current)
    {
        var rect = new Rect(_pressPoint, current);
        Canvas.SetLeft(_marquee, rect.X);
        Canvas.SetTop(_marquee, rect.Y);
        _marquee.Width = rect.Width;
        _marquee.Height = rect.Height;

        foreach (var (tile, icon, _) in _tiles)
        {
            if (tile.Visibility != Visibility.Visible || !tile.IsDescendantOf(_root)) continue;
            var bounds = tile.TransformToAncestor(_root).TransformBounds(new Rect(tile.RenderSize));
            var inside = rect.IntersectsWith(bounds) || _marqueeBase.Contains(icon.Id);
            if (inside != _selected.Contains(icon.Id)) SetSelected(icon.Id, inside);
        }
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
        _selected.Clear();
        Refresh();
        Show();
        Activate();
        _search.Focus();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_selected.Count > 0) ClearSelection(); // first Esc drops the selection, the next closes
            else Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            foreach (var (tile, icon, _) in _tiles)
                if (tile.Visibility == Visibility.Visible) SetSelected(icon.Id, true);
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
        _tileById.Clear();
        _selected.IntersectWith(_drawer.Icons.Select(i => i.Id)); // forget icons that are gone
        foreach (var zone in _drawer.Zones)
            _cards.Children.Add(BuildCard(_drawer.IsDefault(zone) ? zone.Title + "  (기본)" : zone.Title, _drawer.IconsOf(zone), zone));
        _cards.Children.Add(BuildAddCard());
        ApplyFilter();
    }

    private Border BuildCard(string title, IEnumerable<DesktopIcon> icons, Zone zone)
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
        var idle = card.Background;
        var hover = new SolidColorBrush(Color.FromArgb(0xE0, 0x2E, 0x3A, 0x50));
        card.Drop += (_, e) =>
        {
            card.Background = idle;
            foreach (var id in DroppedIds(e.Data)) _drawer.MoveToZone(id, zone);
        };
        card.DragEnter += (_, e) => { if (e.Data.GetDataPresent(DragFormat)) card.Background = hover; };
        card.DragLeave += (_, _) => card.Background = idle;
        card.DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;

        var isDefault = _drawer.IsDefault(zone);
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("이름 바꾸기", () =>
        {
            var name = ZoneMenu.AskTitle(zone.Title);
            if (!string.IsNullOrWhiteSpace(name)) _drawer.RenameZone(zone, name);
        }));
        if (!isDefault)
        {
            // The default zone is where unclaimed icons land, so rules on it would be pointless.
            menu.Items.Add(MenuItem("자동 분류 규칙...", () =>
            {
                var text = ZoneMenu.AskText("자동 분류 규칙: " + zone.Title, string.Join(Environment.NewLine, zone.Patterns), ZoneMenu.PatternHint, multiline: true);
                if (text == null) return;
                _drawer.SetPatterns(zone, text.Split('\n'));
                if (zone.Patterns.Count > 0 && MessageBox.Show($"'{_drawer.DefaultZone.Title}' 구역의 아이콘에 지금 적용할까요?", "자동 분류 규칙", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _drawer.ApplyRulesToUnassigned();
            }));
            menu.Items.Add(MenuItem("기본 구역으로 지정", () => _drawer.SetDefaultZone(zone)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("구역 삭제", () =>
            {
                if (MessageBox.Show($"'{zone.Title}' 구역을 삭제할까요?\n아이콘은 '{_drawer.DefaultZone.Title}' 구역으로 옮겨집니다.", "구역 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _drawer.RemoveZone(zone);
            }));
        }
        header.ContextMenu = menu;
        header.Cursor = Cursors.Hand;

        foreach (var icon in icons)
        {
            var tile = BuildTile(icon);
            tiles.Children.Add(tile);
            _tiles.Add((tile, icon, tiles));
        }
        return card;
    }

    // --- selection ---

    private static readonly SolidColorBrush SelectedBrush = new(Color.FromArgb(0x60, 0x4C, 0xAF, 0xF5));
    private static readonly SolidColorBrush HoverBrush = new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private readonly HashSet<string> _selected = new();
    private readonly Dictionary<string, Border> _tileById = new();
    private string? _anchorId; // for Shift+click ranges

    private void SetSelected(string id, bool selected)
    {
        if (selected) _selected.Add(id); else _selected.Remove(id);
        if (_tileById.TryGetValue(id, out var tile))
            tile.Background = selected ? SelectedBrush : Brushes.Transparent;
    }

    private void ClearSelection()
    {
        foreach (var id in _selected.ToList()) SetSelected(id, false);
    }

    /// <summary>The ids an action on this tile applies to: the whole selection when the tile is part of it.</summary>
    private IReadOnlyList<string> TargetsOf(string id) =>
        _selected.Contains(id) ? _selected.ToList() : new[] { id };

    private static IReadOnlyList<string> DroppedIds(IDataObject data) =>
        data.GetData(DragFormat) is string ids ? ids.Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

    private void SelectRange(string fromId, string toId)
    {
        // Ranges run over the visible tiles in display order, across cards.
        var order = _tiles.Where(t => t.Tile.Visibility == Visibility.Visible).Select(t => t.Icon.Id).ToList();
        var a = order.IndexOf(fromId);
        var b = order.IndexOf(toId);
        if (a < 0 || b < 0) return;
        foreach (var id in order.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
            SetSelected(id, true);
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
        if (_drawer.IsOnDesktop(icon.Id))
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
        _tileById[icon.Id] = tile;
        if (_selected.Contains(icon.Id)) tile.Background = SelectedBrush;

        tile.MouseEnter += (_, _) => { if (!_selected.Contains(icon.Id)) tile.Background = HoverBrush; };
        tile.MouseLeave += (_, _) => { if (!_selected.Contains(icon.Id)) tile.Background = Brushes.Transparent; };
        tile.MouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragId = icon.Id;
            // Ctrl toggles, Shift extends from the last plain/Ctrl click; either way no launch on release.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SetSelected(icon.Id, !_selected.Contains(icon.Id));
                _anchorId = icon.Id;
                _modifiedClick = true;
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchorId != null)
            {
                SelectRange(_anchorId, icon.Id);
                _modifiedClick = true;
            }
            else
            {
                _anchorId = icon.Id;
                _modifiedClick = false;
            }
        };
        tile.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragId != icon.Id) return;
            var delta = e.GetPosition(this) - _dragStart;
            if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
            _dragId = null;
            var ids = TargetsOf(icon.Id);
            _dropBar.Visibility = Visibility.Visible;
            foreach (var id in ids) if (_tileById.TryGetValue(id, out var t)) t.Opacity = 0.4; // dim what is being dragged
            try
            {
                DragDrop.DoDragDrop(tile, new DataObject(DragFormat, string.Join('\n', ids)), DragDropEffects.Move);
            }
            finally
            {
                foreach (var id in ids) if (_tileById.TryGetValue(id, out var t)) t.Opacity = 1;
                _dropBar.Visibility = Visibility.Collapsed;
            }
        };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (_dragId != icon.Id) return;
            _dragId = null;
            if (_modifiedClick) return;
            Launch(icon);
        };

        tile.ContextMenuOpening += (_, _) => tile.ContextMenu = BuildTileMenu(icon);
        tile.ContextMenu = BuildTileMenu(icon);
        return tile;
    }

    private bool _modifiedClick;

    // Menu actions apply to the whole selection when the clicked tile is part of it.
    private ContextMenu BuildTileMenu(DesktopIcon icon)
    {
        var ids = TargetsOf(icon.Id);
        var many = ids.Count > 1;
        var label = many ? $"선택한 {ids.Count}개" : "";
        var menu = new ContextMenu();

        var toggleable = ids.Where(_drawer.CanToggle).ToList();
        if (toggleable.Count > 0)
        {
            var pin = toggleable.Any(id => !_drawer.IsPinned(id)); // mixed selection: pin them all
            menu.Items.Add(MenuItem(pin ? $"{label} 바탕화면에 꺼내기".Trim() : $"{label} 서랍에 넣기 (바탕화면에서 치우기)".Trim(), () =>
            {
                foreach (var id in toggleable) { if (pin) _drawer.Pin(id); else _drawer.Unpin(id); }
            }));
        }
        var move = new MenuItem { Header = $"{label} 구역으로 이동".Trim() };
        foreach (var zone in _drawer.Zones)
            move.Items.Add(MenuItem(_drawer.IsDefault(zone) ? zone.Title + " (기본)" : zone.Title, () =>
            {
                foreach (var id in ids) _drawer.MoveToZone(id, zone);
            }));
        menu.Items.Add(move);
        if (!many && !icon.IsShellItem)
            menu.Items.Add(MenuItem("파일 위치 열기", () => DrawerManager.OpenLocation(icon)));
        var deletable = ids.Select(id => _drawer.Icons.FirstOrDefault(i => i.Id == id)).OfType<DesktopIcon>().Where(i => !i.IsShellItem).ToList();
        if (deletable.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem($"{label} 삭제 (휴지통으로)".Trim(), () => ConfirmDelete(deletable)));
        }
        return menu;
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

    private void ConfirmDelete(IReadOnlyList<DesktopIcon> icons)
    {
        if (icons.Count == 0) return;
        var what = icons.Count == 1
            ? $"'{icons[0].Name}' {(System.IO.Directory.Exists(icons[0].Id) ? "폴더" : "파일")}을(를)"
            : $"항목 {icons.Count}개를";
        if (MessageBox.Show($"{what} 휴지통으로 보낼까요?", "삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var failed = icons.Where(i => !_drawer.Delete(i)).Select(i => i.Name).ToList();
        if (failed.Count > 0)
            MessageBox.Show("삭제하지 못했습니다: " + string.Join(", ", failed), "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
        ClearSelection();
    }

    // A labelled pill shown under the cards during a drag; dropping an icon on it runs the action.
    private Border BuildDropTarget(string label, Color color, Action<IReadOnlyList<string>> onDrop)
    {
        var idle = new SolidColorBrush(Color.FromArgb(0xC0, color.R, color.G, color.B));
        var hover = new SolidColorBrush(color);
        var target = new Border
        {
            Padding = new Thickness(28, 14, 28, 14),
            Margin = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(12),
            Background = idle,
            AllowDrop = true,
            Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold },
        };
        target.DragEnter += (_, e) => { if (e.Data.GetDataPresent(DragFormat)) target.Background = hover; };
        target.DragLeave += (_, _) => target.Background = idle;
        target.DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        target.Drop += (_, e) =>
        {
            target.Background = idle;
            var ids = DroppedIds(e.Data);
            if (ids.Count > 0) onDrop(ids);
        };
        return target;
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
