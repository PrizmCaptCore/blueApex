using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueApex.Desktop;
using BlueApex.Games;
using BlueApex.Ui;
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
    private const int IconPx = (int)Theme.IconSize;

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
        Background = Theme.Scrim;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "BlueApex";

        _search = new TextBox
        {
            Width = 640,
            FontSize = Theme.FontLarge,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 36, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _search.TextChanged += (_, _) =>
        {
            // Clearing the search rebuilds the cards so folded groups go back to their own state.
            if (_search.Text.Trim().Length == 0 && _lastQuery.Length > 0) Refresh();
            else ApplyFilter();
            _lastQuery = _search.Text.Trim();
        };

        _cards = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 0, 24, 24) };
        var scroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var hint = new TextBlock
        {
            Text = $"Esc 닫기 · {_drawer.Hotkey} 열고 닫기 · Ctrl/Shift+클릭으로 여러 개 선택 · 끌어서 구역 이동·꺼내기·삭제",
            Foreground = Theme.TextFaint,
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
                BuildDropTarget("바탕화면에 꺼내기", Theme.AccentColor, ids =>
                {
                    foreach (var id in ids)
                    {
                        if (IsVirtualId(id)) _drawer.PinApp(id); // an app or game gets a shortcut on the desktop
                        else _drawer.Pin(id);
                    }
                }),
                BuildDropTarget("휴지통으로", Theme.DangerColor, ids =>
                {
                    // Apps and games are not files: dropping one here just takes it out of its zone.
                    foreach (var id in ids.Where(IsVirtualId)) _drawer.RemoveMember(id);
                    ConfirmDelete(ids.Select(id => _drawer.Icons.FirstOrDefault(i => i.Id == id)).OfType<DesktopIcon>().Where(i => !i.IsShellItem).ToList());
                }),
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
            Fill = Theme.MarqueeFill,
            Stroke = Theme.MarqueeStroke,
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
        // Losing focus closes the drawer, unless focus went to one of our own dialogs
        // (rename box, folder picker, confirmation); those return focus here afterwards.
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var foreground = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
            if (pid == Environment.ProcessId) return;
            var cls = new System.Text.StringBuilder(64);
            NativeMethods.GetClassName(foreground, cls, cls.Capacity);
            Log.Write($"drawer closed: focus went to {cls} (pid {pid})");
            Hide();
        });
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
            if (node == _search || node == _dropBar || node is System.Windows.Controls.Primitives.ScrollBar || node is Button || node is ComboBox) return Hit.Control;
            if (node is FrameworkElement { Tag: "control" }) return Hit.Control; // selectors, group headers
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
            var hint = icon.IsGame ? $"\n({GameCatalog.SourceLabel(GameCatalog.SourceOf(icon.Id))} 런처가 설치돼 있어야 합니다)" : "";
            MessageBox.Show($"실행하지 못했습니다: {icon.Name}\n{ex.Message}{hint}", "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- building the cards ---

    private void Refresh()
    {
        _cards.Children.Clear();
        _tiles.Clear();
        _tileById.Clear();
        _selected.IntersectWith(_drawer.Icons.Select(i => i.Id)); // forget icons that are gone
        _generation++; // thumbnails still decoding for the old tiles are dropped when they arrive
        _lazy.Clear();
        _sections.Clear();
        foreach (var zone in _drawer.Zones)
            _cards.Children.Add(BuildCard(_drawer.IsDefault(zone) ? zone.Title + "  (기본)" : zone.Title, _drawer.IconsOf(zone), zone));
        if (_drawer.ShowApps)
            _cards.Children.Add(BuildAppsCard());
        if (_drawer.ShowGames)
            _cards.Children.Add(BuildGamesCard());
        _cards.Children.Add(BuildAddCard());
        ApplyFilter();
    }

    private Border BuildCard(string title, IEnumerable<DesktopIcon> icons, Zone zone)
    {
        // Portals show a folder as-is; other zones get the layout selector (manual / name / date / type).
        var selector = zone.IsPortal ? null : SortSelector(zone.SortMode, mode => _drawer.SetSortMode(zone, mode),
            ("manual", "수동"), ("name", "이름"), ("date", "날짜"), ("type", "종류"));
        var parts = Parts.Card(zone.IsPortal ? "📁 " + title : title, selector, folded: zone.Rolled);
        parts.Folded = folded => _drawer.SetRolled(zone, folded); // click on the title folds the card to its title
        var (card, body, header) = parts;
        var tiles = new WrapPanel();
        body.Children.Add(tiles);
        card.AllowDrop = true;
        card.Tag = zone;
        var idle = card.Background;
        var hover = Theme.CardHover;
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
        if (zone.IsPortal)
        {
            menu.Items.Add(MenuItem("폴더 열기", () => Process.Start(new ProcessStartInfo(zone.PortalPath!) { UseShellExecute = true })));
            menu.Items.Add(MenuItem("새 폴더 만들기 (이 폴더 안에)", () =>
            {
                var name = ZoneMenu.AskText("새 폴더", "새 폴더", null, multiline: false)?.Trim();
                if (string.IsNullOrWhiteSpace(name)) return;
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(zone.PortalPath!, name));
                Refresh();
            }));
            menu.Items.Add(new Separator());
        }
        else
        {
            menu.Items.Add(MenuItem("새 폴더 만들기", () =>
            {
                var name = ZoneMenu.AskText("새 폴더", "새 폴더", "바탕화면에 폴더를 만들고 이 구역에 넣습니다.", multiline: false)?.Trim();
                if (!string.IsNullOrWhiteSpace(name)) _drawer.CreateFolder(zone, name);
            }));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(MenuItem("이름 바꾸기", () =>
        {
            var name = ZoneMenu.AskTitle(zone.Title);
            if (!string.IsNullOrWhiteSpace(name)) _drawer.RenameZone(zone, name);
        }));
        if (zone.IsPortal)
        {
            menu.Items.Add(MenuItem("포털 폴더 바꾸기...", () =>
            {
                var folder = PickFolder(zone.PortalPath);
                if (folder != null) _drawer.SetPortal(zone, folder);
            }));
            menu.Items.Add(MenuItem($"한 번에 보일 줄 수... (현재 {_drawer.PortalRows}줄, 모든 포털)", () =>
            {
                var text = ZoneMenu.AskText("포털 표시 줄 수", _drawer.PortalRows.ToString(),
                    "서랍을 열 때 포털 폴더를 몇 줄까지 보여 줄지 (한 줄 4개). 나머지는 \"더 보기\"로 봅니다. 1~50.", multiline: false);
                if (int.TryParse(text?.Trim(), out var rows)) _drawer.PortalRows = rows;
            }));
            menu.Items.Add(MenuItem("포털 해제 (빈 구역으로)", () => _drawer.SetPortal(zone, null)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("구역 삭제", () =>
            {
                if (MessageBox.Show($"'{zone.Title}' 포털 구역을 삭제할까요?\n폴더와 파일은 그대로 남습니다.", "구역 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _drawer.RemoveZone(zone);
            }));
        }
        else if (!isDefault)
        {
            menu.Items.Add(MenuItem("폴더 포털로 바꾸기...", () =>
            {
                var folder = PickFolder(null);
                if (folder != null) _drawer.SetPortal(zone, folder);
            }));
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
            menu.Items.Add(MenuItem("이름순으로 정렬 (한 번)", () => _drawer.SortZone(zone)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("구역 삭제", () =>
            {
                if (MessageBox.Show($"'{zone.Title}' 구역을 삭제할까요?\n아이콘은 '{_drawer.DefaultZone.Title}' 구역으로 옮겨집니다.", "구역 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _drawer.RemoveZone(zone);
            }));
        }
        // Right-click anywhere in the card that is not a tile (tiles have their own menu).
        menu.Items.Insert(0, MenuItem(zone.Rolled ? "펼치기" : "접기", () =>
        {
            parts.SetFolded(!zone.Rolled);
            _drawer.SetRolled(zone, !zone.Rolled);
        }));
        card.ContextMenu = menu;

        if (!zone.IsPortal && zone.SortMode != "manual")
        {
            // Folded into collapsible groups ("little drawers") by the chosen key.
            body.Children.Remove(tiles);
            BuildSections(body, icons.ToList(), zone.SortMode, zone.Id.ToString());
            return card;
        }
        // A portal can hold hundreds of files; only the first page is built, the rest
        // on demand, so opening the drawer stays instant.
        AddTiles(body, tiles, icons, zone.IsPortal ? _drawer.PortalRows * TilesPerRow : int.MaxValue);
        return card;
    }

    // A small "현재값 ▾" button that opens a menu of choices. A plain ComboBox's dropdown
    // misbehaved in this topmost, transparent window; context menus are known to work here.
    private static Button SortSelector(string current, Action<string> changed, params (string Value, string Label)[] choices)
    {
        var currentLabel = choices.FirstOrDefault(c => c.Value == current).Label ?? choices[0].Label;
        var button = Parts.SmallButton(currentLabel + " ▾");
        button.Margin = new Thickness(Theme.Space1 + 2, -2, Theme.Space1 + 2, 0);
        button.VerticalAlignment = VerticalAlignment.Top;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (value, label) in choices)
            menu.Items.Add(MenuItem((value == current ? "● " : "○ ") + label, () =>
            {
                Log.Write($"layout selector: {current} -> {value}");
                if (value != current) changed(value);
            }));
        button.Click += (_, _) => menu.IsOpen = true;
        return button;
    }

    // --- grouped layout ("little drawers") ---

    // Open/closed state of groups, remembered for the session so reopening the drawer keeps them.
    private readonly HashSet<string> _openSections = new();
    private readonly List<(string Query, Action<string> BuildMatching, Action<bool> SetExpanded)> _sections = new();

    /// <param name="innerMode">When set, each group holds another level of groups (e.g. launcher → first letter) instead of tiles.</param>
    private void BuildSections(StackPanel body, List<DesktopIcon> icons, string mode, string cardKey, string? innerMode = null, double indent = 0)
    {
        foreach (var group in GroupIcons(icons, mode))
        {
            var key = cardKey + "/" + group.Label;
            var open = _openSections.Contains(key);
            var (headerBar, arrow) = Parts.GroupHeader($"{group.Label}  ({group.Items.Count})", open);
            headerBar.Margin = new Thickness(headerBar.Margin.Left + indent, headerBar.Margin.Top, headerBar.Margin.Right, headerBar.Margin.Bottom);
            var panel = new WrapPanel();
            var content = new StackPanel { Children = { panel }, Visibility = open ? Visibility.Visible : Visibility.Collapsed };
            LazyTiles? lazy = null;
            var built = false;
            void Ensure()
            {
                if (built) return;
                built = true;
                if (innerMode != null)
                {
                    // Nested groups are built on first open, like tiles; they register themselves for search.
                    BuildSections(content, group.Items, innerMode, key, indent: indent + Theme.Space3);
                    return;
                }
                lazy = new LazyTiles(this, content, panel, group.Items, _drawer.PortalRows * TilesPerRow);
                _lazy.Add(lazy);
                lazy.AddPage();
            }
            void SetExpanded(bool expanded)
            {
                if (expanded) Ensure();
                content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                arrow.Text = expanded ? "▾" : "▸";
            }
            if (open) Ensure();
            headerBar.MouseLeftButtonUp += (_, _) =>
            {
                var expanded = content.Visibility != Visibility.Visible;
                Log.Write($"section {key}: {(expanded ? "open" : "close")}");
                if (expanded) _openSections.Add(key); else _openSections.Remove(key);
                SetExpanded(expanded);
            };
            body.Children.Add(headerBar);
            body.Children.Add(content);
            _sections.Add((cardKey, query => { Ensure(); lazy?.BuildMatching(query); }, SetExpanded));
        }
    }

    private IEnumerable<(string Label, List<DesktopIcon> Items)> GroupIcons(List<DesktopIcon> icons, string mode)
    {
        switch (mode)
        {
            case "source":
            {
                // One group per launcher, in a fixed order; installed games before owned-only ones.
                var order = new[] { "steam", "epic", "gog" };
                foreach (var group in icons.Where(i => i.IsGame).GroupBy(i => GameCatalog.SourceOf(i.Id)).OrderBy(g => Array.IndexOf(order, g.Key)))
                    yield return (GameCatalog.SourceLabel(group.Key), group.OrderBy(i => _drawer.GameOf(i.Id)?.Installed == true ? 0 : 1).ToList());
                yield break;
            }
            case "date":
            {
                var now = DateTime.Now;
                var stamped = icons.Select(i => (Icon: i, When: LastWrite(i))).ToList();
                var buckets = new (string Label, Func<DateTime?, bool> Match)[]
                {
                    ("오늘", t => t != null && t.Value.Date == now.Date),
                    ("어제", t => t != null && t.Value.Date == now.Date.AddDays(-1)),
                    ("이번 주", t => t != null && t.Value.Date > now.Date.AddDays(-7)),
                    ("이번 달", t => t != null && t.Value.Year == now.Year && t.Value.Month == now.Month),
                    ("올해", t => t != null && t.Value.Year == now.Year),
                    ("이전", t => t != null),
                    ("날짜 없음", t => t == null),
                };
                var left = stamped.ToList();
                foreach (var (label, match) in buckets)
                {
                    var hit = left.Where(s => match(s.When)).OrderByDescending(s => s.When).Select(s => s.Icon).ToList();
                    if (hit.Count == 0) continue;
                    left.RemoveAll(s => match(s.When));
                    yield return (label, hit);
                }
                yield break;
            }
            case "type":
            {
                var order = new[] { "게임", "앱", "바로가기", "폴더", "이미지", "문서", "파일", "시스템" };
                var sorted = AppCatalog.Sort(icons, _drawer.AppSortLatinFirst);
                foreach (var label in order)
                {
                    var hit = sorted.Where(i => Kind(i) == label).ToList();
                    if (hit.Count > 0) yield return (label, hit);
                }
                yield break;
            }
            default: // "name": first letter, in the same order as the sort setting
            {
                var sorted = AppCatalog.Sort(icons, _drawer.AppSortLatinFirst);
                foreach (var group in sorted.GroupBy(i => Initial(i.Name)))
                    yield return (group.Key, group.ToList());
                yield break;
            }
        }
    }

    private static bool IsVirtualId(string id) => AppCatalog.IsAppId(id) || GameCatalog.IsGameId(id);

    private static DateTime? LastWrite(DesktopIcon icon)
    {
        if (icon.IsVirtual || icon.IsShellItem) return null;
        try
        {
            return System.IO.File.GetLastWriteTime(icon.Id);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Kind(DesktopIcon icon)
    {
        if (icon.IsGame) return "게임";
        if (icon.IsApp) return "앱";
        if (icon.IsShellItem) return "시스템";
        if (System.IO.Directory.Exists(icon.Id)) return "폴더";
        var ext = System.IO.Path.GetExtension(icon.Id).ToLowerInvariant();
        return ext switch
        {
            ".lnk" or ".url" or ".appref-ms" => "바로가기",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".avif" or ".tif" or ".tiff" => "이미지",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".hwp" or ".hwpx" or ".rtf" => "문서",
            _ => "파일",
        };
    }

    // "A".."Z", "0-9", a Hangul initial consonant (ㄱ..ㅎ), or "#".
    private static string Initial(string name)
    {
        if (name.Length == 0) return "#";
        var c = name[0];
        if (c is >= '가' and <= '힣')
            return "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ"[(c - 0xAC00) / 588].ToString();
        if (char.IsDigit(c)) return "0-9";
        if (c < 0x0250 && char.IsLetter(c)) return char.ToUpperInvariant(c).ToString();
        return "#";
    }

    /// <summary>The card listing every installed app (from the shell's Applications folder), paged like a portal.</summary>
    private Border BuildAppsCard()
    {
        var apps = _drawer.Apps;
        var selector = SortSelector(_drawer.AppSections ? "letters" : "flat",
            mode => _drawer.AppSections = mode == "letters", ("flat", "전체"), ("letters", "글자별"));
        var parts = Parts.Card(apps.Count > 0 ? $"모든 앱  ({apps.Count})" : "모든 앱  (불러오는 중...)", selector, alternate: true, folded: _drawer.AppsFolded);
        parts.Folded = folded => _drawer.AppsFolded = folded;
        var (card, body, _) = parts;
        var tiles = new WrapPanel();
        body.Children.Add(tiles);
        var menu = new ContextMenu();
        var latin = _drawer.AppSortLatinFirst;
        menu.Items.Add(MenuItem((latin ? "● " : "○ ") + "정렬: A→Z 먼저, 그다음 가나다", () => _drawer.AppSortLatinFirst = true));
        menu.Items.Add(MenuItem((latin ? "○ " : "● ") + "정렬: 가나다 먼저, 그다음 A→Z", () => _drawer.AppSortLatinFirst = false));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("설치된 앱 카드 숨기기 (트레이 메뉴에서 다시 켤 수 있음)", () => _drawer.ShowApps = false));
        card.ContextMenu = menu;
        if (_drawer.AppSections)
        {
            body.Children.Remove(tiles);
            BuildSections(body, apps.ToList(), "name", "apps");
        }
        else
        {
            AddTiles(body, tiles, apps, _drawer.PortalRows * TilesPerRow);
        }
        return card;
    }

    /// <summary>The card listing games from Steam, Epic Games and GOG (installed, plus owned ones when the account is linked).</summary>
    private Border BuildGamesCard()
    {
        var games = _drawer.Games;
        var selector = SortSelector(_drawer.GameLayout, mode => _drawer.GameLayout = mode,
            ("source", "런처별"), ("flat", "전체"), ("letters", "글자별"));
        var parts = Parts.Card(games.Count > 0 ? $"게임  ({games.Count})" : "게임  (불러오는 중...)", selector, alternate: true, folded: _drawer.GamesFolded);
        parts.Folded = folded => _drawer.GamesFolded = folded;
        var (card, body, _) = parts;
        var tiles = new WrapPanel();
        body.Children.Add(tiles);

        var menu = new ContextMenu();
        if (_drawer.SteamLinked)
            menu.Items.Add(MenuItem("Steam 로그아웃 (보유 게임 목록 지우기)", _drawer.UnlinkSteam));
        else
            menu.Items.Add(MenuItem("Steam 로그인 (보유한 게임 전부 불러오기)...", LinkSteam));
        var epic = MenuItem(EpicLibrary.IsLauncherInstalled
            ? "Epic Games: 런처에 로그인돼 있으면 라이브러리를 자동으로 읽습니다"
            : "Epic Games: 런처가 설치돼 있지 않습니다", () => { });
        epic.IsEnabled = false;
        menu.Items.Add(epic);
        menu.Items.Add(MenuItem("라이브러리 다시 읽기", _drawer.RescanGames));
        menu.Items.Add(new Separator());
        var showAll = _drawer.ShowUninstalledGames;
        menu.Items.Add(MenuItem((showAll ? "● " : "○ ") + "미설치 게임도 보기 (흐리게; 클릭하면 설치 화면)", () => _drawer.ShowUninstalledGames = !showAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("게임 카드 숨기기 (트레이 메뉴에서 다시 켤 수 있음)", () => _drawer.ShowGames = false));
        card.ContextMenu = menu;

        if (_drawer.GameLayout == "source")
        {
            // Launcher → first letter: two levels of little drawers.
            body.Children.Remove(tiles);
            BuildSections(body, games.ToList(), "source", "games", innerMode: "name");
        }
        else if (_drawer.GameLayout == "letters")
        {
            body.Children.Remove(tiles);
            BuildSections(body, games.ToList(), "name", "games");
        }
        else
        {
            AddTiles(body, tiles, games, _drawer.PortalRows * TilesPerRow);
        }
        return card;
    }

    // Opens Steam's sign-in page in an embedded browser window; the session then fetches the owned list.
    private async void LinkSteam()
    {
        Hide(); // the drawer is topmost and would sit over the login window
        try
        {
            var count = await _drawer.LinkSteamAsync();
            if (count != null)
                MessageBox.Show($"Steam 게임 {count}개를 불러왔습니다.", "BlueApex", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("로그인 창을 띄우려면 Microsoft Edge WebView2 런타임이 필요합니다.\nWindows 11에는 들어 있고, 없으면 Microsoft 사이트에서 'WebView2 Runtime'을 설치하면 됩니다.",
                "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException
                                   or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show("Steam 로그인에 실패했습니다.\n" + ex.Message, "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Tiles are built a page at a time ("더 보기" adds the next page); a search builds
    // whatever matches straight away, so nothing is missed just because it was unbuilt.
    private readonly List<LazyTiles> _lazy = new();

    private void AddTiles(StackPanel body, WrapPanel tiles, IEnumerable<DesktopIcon> icons, int pageSize)
    {
        var lazy = new LazyTiles(this, body, tiles, icons, pageSize);
        _lazy.Add(lazy);
        lazy.AddPage();
    }

    private sealed class LazyTiles
    {
        private readonly DrawerWindow _owner;
        private readonly StackPanel _body;
        private readonly WrapPanel _tiles;
        private readonly List<DesktopIcon> _pending;
        private readonly int _pageSize;
        private Button? _more;

        public LazyTiles(DrawerWindow owner, StackPanel body, WrapPanel tiles, IEnumerable<DesktopIcon> icons, int pageSize)
        {
            _owner = owner;
            _body = body;
            _tiles = tiles;
            _pending = icons.ToList();
            _pageSize = pageSize;
        }

        public void AddPage()
        {
            var page = _pending.Take(_pageSize).ToList();
            _pending.RemoveRange(0, page.Count);
            foreach (var icon in page) Build(icon);
            UpdateMoreButton();
        }

        /// <summary>Builds every unbuilt tile whose name matches, so searching reaches past the current page.</summary>
        public void BuildMatching(string query)
        {
            var matches = _pending.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return;
            _pending.RemoveAll(matches.Contains);
            foreach (var icon in matches) Build(icon);
            UpdateMoreButton();
        }

        private void Build(DesktopIcon icon)
        {
            var tile = _owner.BuildTile(icon);
            _tiles.Children.Add(tile);
            _owner._tiles.Add((tile, icon, _tiles));
        }

        private void UpdateMoreButton()
        {
            if (_more != null) _body.Children.Remove(_more);
            if (_pending.Count == 0) return;
            _more = Parts.SmallButton($"더 보기 (남은 {_pending.Count}개)");
            _more.Click += (_, _) => AddPage();
            _body.Children.Add(_more);
        }
    }

    // Card width 440 minus padding, tiles 100 wide plus margins: four per row.
    private const int TilesPerRow = 4;

    // --- selection ---

    private static readonly Brush SelectedBrush = Theme.Selection;
    private static readonly Brush HoverBrush = Theme.Hover;
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
        var portalEntry = !_drawer.IsDesktopItem(icon.Id);
        var (tile, image) = Parts.Tile(icon.Name, badge: _drawer.IsOnDesktop(icon.Id));
        if (icon.IsGame && _drawer.GameOf(icon.Id) is { Installed: false })
        {
            // Owned but not on this PC: shown dimmed; opening it goes to the launcher's install page.
            image.Opacity = 0.45;
            tile.ToolTip = icon.Name + "  (미설치 · 클릭하면 설치 화면)";
        }
        if (icon.IsVirtual)
        {
            // App and game pictures can be slow (Store apps, downloads); fetch them off the UI thread like thumbnails.
            var generation = _generation;
            image.Source = _iconLoader.GetAsync(icon.Id, IconPx, false, Dispatcher, ready =>
            {
                if (generation == _generation) image.Source = ready;
            });
        }
        else if (portalEntry)
        {
            // File-type icon now (cheap); the thumbnail replaces it when a worker has decoded it.
            var generation = _generation;
            image.Source = _iconLoader.Get(icon.Id, IconPx);
            var thumb = _iconLoader.GetAsync(icon.Id, IconPx * 2, true, Dispatcher, ready =>
            {
                if (generation == _generation) image.Source = ready;
            });
            if (thumb != null) image.Source = thumb;
        }
        else
        {
            image.Source = _iconLoader.Get(icon.Id, IconPx);
        }
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
            if (portalEntry && !icon.IsVirtual) return; // files seen through a portal are not desktop items; nothing to drag them into
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
    private int _generation;

    // Menu actions apply to the whole selection when the clicked tile is part of it.
    private ContextMenu BuildTileMenu(DesktopIcon icon)
    {
        var ids = TargetsOf(icon.Id);
        var many = ids.Count > 1;
        var label = many ? $"선택한 {ids.Count}개" : "";
        var menu = new ContextMenu();

        if (icon.IsVirtual)
        {
            // Installed app or game: launch; add to / move between zones; make a desktop shortcut.
            var appIds = ids.Where(IsVirtualId).ToList();
            var inZone = _drawer.ZoneOf(icon.Id);
            var game = icon.IsGame ? _drawer.GameOf(icon.Id) : null;
            menu.Items.Add(MenuItem(game is { Installed: false } ? "설치 (런처 열기)" : "열기", () => Launch(icon)));
            var add = new MenuItem { Header = $"{label} {(inZone == null ? "구역에 추가" : "구역으로 이동")}".Trim() };
            foreach (var zone in _drawer.Zones.Where(z => !z.IsPortal))
                add.Items.Add(MenuItem(_drawer.IsDefault(zone) ? zone.Title + " (기본)" : zone.Title, () =>
                {
                    foreach (var id in appIds) _drawer.MoveToZone(id, zone);
                }));
            menu.Items.Add(add);
            if (inZone != null)
                menu.Items.Add(MenuItem($"{label} 구역에서 빼기".Trim(), () =>
                {
                    foreach (var id in appIds) _drawer.RemoveMember(id);
                }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem($"{label} 바탕화면에 바로가기 만들기".Trim(), () =>
            {
                foreach (var id in appIds) _drawer.PinApp(id);
            }));
            return menu;
        }
        if (!_drawer.IsDesktopItem(icon.Id))
        {
            // A file seen through a portal: open, locate, delete. No pinning or zones.
            menu.Items.Add(MenuItem("열기", () => Launch(icon)));
            menu.Items.Add(MenuItem("파일 위치 열기", () => DrawerManager.OpenLocation(icon)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("삭제 (휴지통으로)", () => ConfirmDelete(new[] { icon })));
            return menu;
        }

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
        foreach (var zone in _drawer.Zones.Where(z => !z.IsPortal))
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
        var button = Parts.BigButton("+ 새 구역");
        button.Click += (_, _) =>
        {
            var name = ZoneMenu.AskTitle("새 구역");
            if (!string.IsNullOrWhiteSpace(name)) _drawer.AddZone(name);
        };
        var portal = Parts.BigButton("+ 폴더 포털");
        portal.Margin = new Thickness(0, 10, 0, 0);
        portal.ToolTip = "폴더 하나의 내용을 그대로 보여 주는 구역";
        portal.Click += (_, _) =>
        {
            var folder = PickFolder(null);
            if (folder != null) _drawer.AddPortal(folder);
        };
        return new Border
        {
            Width = 200,
            MinHeight = 140,
            Margin = new Thickness(12),
            Child = new StackPanel { Children = { button, portal } },
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    // The drawer is topmost; the dialog is modal to it so it is not hidden behind.
    private static string? PickFolder(string? current)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "포털로 보여 줄 폴더",
            UseDescriptionForTitle = true,
            InitialDirectory = current ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ShowNewFolderButton = true,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
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
        var target = Parts.Pill(label, color);
        var idle = target.Background;
        var hover = new SolidColorBrush(color);
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
        if (query.Length > 0)
        {
            // Folded groups open up for a search so their matches can be seen. Opening a group
            // can register nested groups, so keep going until no new ones appear.
            var seen = 0;
            while (seen < _sections.Count)
            {
                var batch = _sections.Skip(seen).ToList();
                seen = _sections.Count;
                foreach (var (_, buildMatching, setExpanded) in batch)
                {
                    buildMatching(query);
                    setExpanded(true);
                }
            }
            foreach (var lazy in _lazy) lazy.BuildMatching(query);
        }
        foreach (var (tile, icon, _) in _tiles)
            tile.Visibility = query.Length == 0 || icon.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Hide cards with nothing left to show while searching.
        foreach (var child in _cards.Children.OfType<Border>())
        {
            if (child.Child is not StackPanel) continue; // the add-zone buttons
            var any = _tiles.Any(t => t.Tile.Visibility == Visibility.Visible && t.Tile.IsDescendantOf(child));
            child.Visibility = query.Length == 0 || any ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string _lastQuery = "";
}
