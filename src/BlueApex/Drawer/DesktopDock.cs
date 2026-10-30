using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BlueApex.Desktop;
using BlueApex.Games;
using BlueApex.Ui;

namespace BlueApex.Drawer;

/// <summary>
/// The home-screen dock, GNOME-style: favourite apps, then the apps that have windows open, then
/// the Discord voice slot (while in a voice channel) and the now-playing slot (while media plays),
/// then the drawer button. A dot under an icon means the app is running; clicking a running app
/// brings its window forward, clicking a favourite that is not running starts it.
/// Lives in a <see cref="DesktopFloatWindow"/> just above the desktop, like the widgets.
/// </summary>
internal sealed class DesktopDock : IDesktopLayerItem, IDisposable
{
    // Layout, in DIPs.
    private const double Slot = 52, IconSize = 36, Pad = 8, Height = 64, DividerWidth = 13, DotSize = 5;
    // Now playing: art | title, artist | prev play next.
    private const double MediaWidth = 236, ArtSize = 40, MediaButtons = 164, MediaButton = 24;
    // Discord voice: overlapping avatars | mic, headset.
    private const double VoiceWidth = 194, Avatar = 28, AvatarStep = 19, VoiceButtons = 128, VoiceButton = 30; // 4 faces + "+N", then the buttons
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private readonly DesktopHost _desktop;
    private readonly DrawerManager _drawer;
    private readonly Action _open;
    private readonly DesktopFloatWindow _window;
    private readonly IconLoader _icons = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Dictionary<string, string?> _shortcutTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly double _scale;
    private readonly NowPlaying _media;
    private readonly DiscordVoice _voice;
    private readonly Dictionary<string, ImageSource?> _avatars = new();
    private byte[]? _artBytes;
    private ImageSource? _art;

    private List<Entry> _entries = new();
    private string _signature = "";
    private int _x, _y, _width, _height;

    /// <summary>One slot: a favourite (Id set), a running app (App set), or both; Divider and Drawer are fixed slots.</summary>
    private sealed record Entry(string Name, string? Id, RunningApp? App, double Left, double Width,
        bool Divider = false, bool Drawer = false, bool Media = false, bool Voice = false);

    public DesktopDock(DesktopHost desktop, DrawerManager drawer, Action open, NowPlaying media, DiscordVoice voice)
    {
        _desktop = desktop;
        _drawer = drawer;
        _open = open;
        _media = media;
        _voice = voice;
        _scale = desktop.Dpi / 96.0;
        _height = Px(Height);
        Rebuild(force: true, out var content);
        (_x, _y) = Place(_width);
        _window = new DesktopFloatWindow(desktop, this, content!, _x, _y, _width, _height);

        _drawer.DockChanged += () => Refresh(force: true);
        _drawer.Changed += () => Refresh(force: false); // names and icons of drawer items
        _media.Changed += () => Refresh(force: false);
        _voice.Changed += () => Refresh(force: false);
        _timer.Tick += (_, _) => Refresh(force: false);
        _timer.Start();
    }

    private int Px(double dip) => (int)Math.Round(dip * _scale);

    // --- what the dock shows ---

    private void Refresh(bool force)
    {
        var center = _x + _width / 2; // before Rebuild changes the width
        if (!Rebuild(force, out var content)) return;
        _window.SetContent(content!);
        // Grow or shrink around the centre, so the dock stays where the user put it.
        (_x, _y) = Clamp(center - _width / 2, _y);
        _window.SetBounds(_x, _y, _width, _height);
    }

    // Running apps keep the order they first appeared in; window z-order would reshuffle them on every focus change.
    private readonly List<string> _runningOrder = new();

    private double Measure() => _entries.Count == 0 ? 0 : _entries[^1].Left + _entries[^1].Width + Pad;

    /// <summary>Recomputes the slots; false (and no content) when nothing visible changed.</summary>
    private bool Rebuild(bool force, out FrameworkElement? content)
    {
        content = null;
        List<RunningApp> running;
        try
        {
            running = RunningApps.Scan();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"dock: window scan failed: {ex.Message}");
            running = new List<RunningApp>();
        }

        var entries = new List<Entry>();
        var left = Pad;
        var claimed = new HashSet<RunningApp>();
        foreach (var id in _drawer.Dock)
        {
            var app = running.FirstOrDefault(a => !claimed.Contains(a) && Matches(id, a));
            if (app != null) claimed.Add(app);
            entries.Add(new Entry(NameOf(id), id, app, left, Slot));
            left += Slot;
        }
        _runningOrder.RemoveAll(key => running.All(a => a.Key != key));
        foreach (var app in running.Where(a => !_runningOrder.Contains(a.Key))) _runningOrder.Add(app.Key);
        var others = running.Where(a => !claimed.Contains(a)).OrderBy(a => _runningOrder.IndexOf(a.Key)).ToList();
        if (others.Count > 0 && entries.Count > 0)
        {
            entries.Add(new Entry("", null, null, left, DividerWidth, Divider: true));
            left += DividerWidth;
        }
        foreach (var app in others)
        {
            entries.Add(new Entry(app.Name, null, app, left, Slot));
            left += Slot;
        }
        var voice = _voice.Current;
        var media = _media.Current;
        if ((voice != null || media != null) && entries.Count > 0)
        {
            entries.Add(new Entry("", null, null, left, DividerWidth, Divider: true));
            left += DividerWidth;
        }
        if (voice != null)
        {
            entries.Add(new Entry(voice.ChannelName, null, null, left, VoiceWidth, Voice: true));
            left += VoiceWidth;
        }
        if (media != null)
        {
            entries.Add(new Entry(media.Title, null, null, left, MediaWidth, Media: true));
            left += MediaWidth;
        }
        if (entries.Count > 0)
        {
            entries.Add(new Entry("", null, null, left, DividerWidth, Divider: true));
            left += DividerWidth;
        }
        entries.Add(new Entry("서랍", null, null, left, Slot, Drawer: true));

        // Anything visible that changed means a redraw: slots, the track, and who is talking.
        var signature = string.Join("|", entries.Select(e => $"{e.Id}:{e.App?.Key}:{e.Divider}:{e.Drawer}"))
            + $"#{media?.Title}|{media?.Artist}|{media?.Playing}|{media?.Art?.Length}"
            + $"#{voice?.ChannelName}|{voice?.SelfMute}|{voice?.SelfDeaf}|"
            + string.Join(",", voice?.Members.Select(m => $"{m.Id}{m.Speaking}{m.Muted}{m.Deafened}") ?? Array.Empty<string>());
        if (!force && signature == _signature)
        {
            _entries = entries; // same slots; keep the window handles current for clicks
            return false;
        }
        _signature = signature;
        _entries = entries;
        _width = Px(Measure());
        content = Draw();
        return true;
    }

    private FrameworkElement Draw()
    {
        var row = new Canvas { Width = Measure(), Height = Height };
        foreach (var entry in _entries)
        {
            FrameworkElement slot;
            if (entry.Divider)
                slot = new Border { Width = 1, Height = IconSize, Background = Theme.Border, Margin = new Thickness((DividerWidth - 1) / 2, (Height - IconSize) / 2, 0, 0) };
            else if (entry.Drawer)
                slot = DrawerGlyph();
            else if (entry.Media)
                slot = MediaSlot();
            else if (entry.Voice)
                slot = VoiceSlot();
            else
                slot = AppSlot(entry);
            Canvas.SetLeft(slot, entry.Left);
            row.Children.Add(slot);
        }
        return Parts.Panel(row, corner: 18, padding: new Thickness(0));
    }

    private FrameworkElement AppSlot(Entry entry)
    {
        var image = new Image { Width = IconSize, Height = IconSize };
        // Store apps are drawn from their shell:AppsFolder entry; files, exes and games by id.
        var source = entry.Id == null ? entry.App!.IconSource : DockApps.IconSource(entry.Id);
        image.Source = _icons.GetAsync(source, (int)Math.Round(IconSize * _scale), false, Dispatcher.CurrentDispatcher, ready => image.Source = ready);
        var dot = new Ellipse
        {
            Width = DotSize, Height = DotSize, Fill = Theme.Text,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 5),
            Visibility = entry.App != null ? Visibility.Visible : Visibility.Hidden,
        };
        return new Grid
        {
            Width = Slot, Height = Height,
            Children = { new Border { Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) }, dot },
        };
    }

    // --- now playing ---

    private FrameworkElement MediaSlot()
    {
        var info = _media.Current!;
        if (!ReferenceEquals(info.Art, _artBytes))
        {
            _artBytes = info.Art;
            _art = Decode(info.Art, (int)Math.Round(ArtSize * _scale * 2));
        }
        var art = new Border
        {
            Width = ArtSize, Height = ArtSize, CornerRadius = new CornerRadius(8), Background = Theme.GroupBar,
            Child = _art == null
                ? Glyph("", 18, Theme.TextDim) // a music note
                : new Border { CornerRadius = new CornerRadius(8), Background = new ImageBrush(_art) { Stretch = Stretch.UniformToFill } },
        };
        Canvas.SetLeft(art, 6);
        Canvas.SetTop(art, (Height - ArtSize) / 2);
        var text = new StackPanel
        {
            Width = MediaButtons - 6 - ArtSize - 14,
            Children =
            {
                new TextBlock { Text = info.Title, Foreground = Theme.Text, FontSize = Theme.FontBody, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = info.Artist, Foreground = Theme.TextDim, FontSize = Theme.FontSmall, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        Canvas.SetLeft(text, 6 + ArtSize + 8);
        Canvas.SetTop(text, Height / 2 - 18);
        var canvas = new Canvas { Width = MediaWidth, Height = Height, Children = { art, text } };
        // Previous, play/pause, next (Segoe icon glyphs).
        var glyphs = new[] { "", info.Playing ? "" : "", "" };
        for (var i = 0; i < glyphs.Length; i++)
        {
            var button = Glyph(glyphs[i], i == 1 ? 18 : 14, Theme.Text);
            button.Width = MediaButton;
            Canvas.SetLeft(button, MediaButtons + i * MediaButton);
            Canvas.SetTop(button, (Height - 24) / 2);
            canvas.Children.Add(button);
        }
        return canvas;
    }

    private static TextBlock Glyph(string glyph, double size, Brush color) => new()
    {
        Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size, Foreground = color,
        Height = 24, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, (24 - size) / 2, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private static ImageSource? Decode(byte[]? bytes, int sizePx)
    {
        if (bytes == null) return null;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelHeight = sizePx;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            return null; // not a decodable picture
        }
    }

    // Clicking the art or title brings the playing app forward, if it has a window.
    private void ShowMediaApp()
    {
        var appId = _media.Current?.AppId;
        if (string.IsNullOrEmpty(appId)) return;
        var exe = appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? appId : appId + ".exe";
        var app = _entries.Select(e => e.App).OfType<RunningApp>().FirstOrDefault(a =>
            string.Equals(a.AppId, appId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(System.IO.Path.GetFileName(a.ExePath), exe, StringComparison.OrdinalIgnoreCase));
        if (app != null) RunningApps.Activate(app);
    }

    // --- Discord voice ---

    private FrameworkElement VoiceSlot()
    {
        var state = _voice.Current!;
        var canvas = new Canvas { Width = VoiceWidth, Height = Height };
        var shown = state.Members.Take(4).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var member = shown[i];
            var face = new Ellipse
            {
                Width = Avatar, Height = Avatar,
                Fill = AvatarOf(member) is { } img ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : Theme.GroupBar,
                // Discord's own cue: a green ring while someone talks; otherwise a dark ring separates overlapping faces.
                Stroke = member.Speaking ? new SolidColorBrush(Color.FromRgb(0x23, 0xA5, 0x59)) : Theme.Glass,
                StrokeThickness = member.Speaking ? 2.5 : 2,
                Opacity = member.Deafened ? 0.5 : 1,
            };
            Canvas.SetLeft(face, 8 + i * AvatarStep);
            Canvas.SetTop(face, (Height - Avatar) / 2 - 6);
            canvas.Children.Add(face);
        }
        var extra = state.Members.Count - shown.Count;
        var count = new TextBlock
        {
            Text = extra > 0 ? $"+{extra}" : state.Members.Count == 0 ? "아무도 없음" : "",
            Foreground = Theme.TextDim, FontSize = Theme.FontSmall,
        };
        Canvas.SetLeft(count, shown.Count == 0 ? 8 : 8 + (shown.Count - 1) * AvatarStep + Avatar + 4);
        Canvas.SetTop(count, Height / 2 - 14);
        canvas.Children.Add(count);
        var channel = new TextBlock
        {
            Text = state.ChannelName, Foreground = Theme.TextFaint, FontSize = 10, Width = VoiceButtons - 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Canvas.SetLeft(channel, 8);
        Canvas.SetTop(channel, Height - 18);
        canvas.Children.Add(channel);

        var red = new SolidColorBrush(Color.FromRgb(0xED, 0x42, 0x45));
        var micOff = state.SelfMute || state.SelfDeaf;
        var mic = Glyph(micOff ? "" : "", 16, micOff ? red : Theme.Text);
        var headset = Glyph("", 16, state.SelfDeaf ? red : Theme.Text);
        mic.Width = headset.Width = VoiceButton;
        Canvas.SetLeft(mic, VoiceButtons);
        Canvas.SetLeft(headset, VoiceButtons + VoiceButton);
        Canvas.SetTop(mic, (Height - 24) / 2);
        Canvas.SetTop(headset, (Height - 24) / 2);
        canvas.Children.Add(mic);
        canvas.Children.Add(headset);
        return canvas;
    }

    // Avatars come from Discord's CDN once (cached on disk by ImageCache); the dock redraws when one arrives.
    private ImageSource? AvatarOf(DiscordMember member)
    {
        if (_avatars.TryGetValue(member.AvatarUrl, out var cached)) return cached;
        _avatars[member.AvatarUrl] = null; // fetching
        var url = member.AvatarUrl;
        _ = Task.Run(() => Games.ImageCache.Fetch(url)).ContinueWith(t =>
        {
            ImageSource? image = null;
            if (t.Result is { } file)
                try { image = Decode(File.ReadAllBytes(file), (int)Math.Round(Avatar * _scale * 2)); }
                catch (IOException) { }
            _avatars[url] = image;
            if (image != null) Refresh(force: true);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    private void ShowMembers(int screenX, int screenY)
    {
        var state = _voice.Current;
        if (state == null) return;
        var items = new List<(string? Label, Action? Action)> { ($"🔊 {state.ChannelName}", null), (null, null) };
        foreach (var m in state.Members)
            items.Add(($"{(m.Speaking ? "● " : "    ")}{m.Name}{(m.Deafened ? "  (헤드셋 끔)" : m.Muted ? "  (음소거)" : "")}", null));
        if (state.Members.Count == 0) items.Add(("아무도 없음", null));
        DesktopPopupMenu.Show(screenX, screenY, items.ToArray());
    }

    // The 3x3 dots of "all apps", like GNOME's "Show Applications" at the end of its dash.
    private static FrameworkElement DrawerGlyph()
    {
        var dots = new UniformGrid { Rows = 3, Columns = 3, Width = 22, Height = 22 };
        for (var i = 0; i < 9; i++)
            dots.Children.Add(new Ellipse { Width = 4.5, Height = 4.5, Fill = Theme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return new Grid { Width = Slot, Height = Height, Children = { dots } };
    }

    private string NameOf(string id) => DockApps.NameOf(_drawer, id);

    // A favourite and a running app are the same app when the ids match, or the favourite
    // (an exe, or a shortcut to one) points at the running exe.
    private bool Matches(string id, RunningApp app)
    {
        if (id.StartsWith("aumid:", StringComparison.Ordinal))
            return app.AppId != null && string.Equals(id[6..], app.AppId, StringComparison.OrdinalIgnoreCase);
        if (app.ExePath == null) return false;
        var target = id.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(id) : id;
        if (GameCatalog.IsGameId(id)) target = GameCatalog.Find(id)?.Exe;
        return target != null && string.Equals(target, app.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    private string? ShortcutTarget(string lnk)
    {
        if (_shortcutTargets.TryGetValue(lnk, out var cached)) return cached;
        string? target = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type != null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                target = (string?)shell.CreateShortcut(lnk).TargetPath;
                if (string.IsNullOrEmpty(target)) target = null;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // an unreadable shortcut just never shows as running
        }
        _shortcutTargets[lnk] = target;
        return target;
    }

    // --- placement ---

    private (int X, int Y) Place(int width)
    {
        if (_drawer.DockPosition is { } saved) return Clamp(saved.CenterX - width / 2, saved.Y);
        var (areaWidth, _) = _desktop.IconAreaSize;
        var workBottom = (int)Math.Round(SystemParameters.WorkArea.Bottom * _scale);
        return Clamp((areaWidth - width) / 2, workBottom - _height - Px(16));
    }

    private (int X, int Y) Clamp(int x, int y)
    {
        var (areaWidth, areaHeight) = _desktop.IconAreaSize;
        return (Math.Clamp(x, 0, Math.Max(0, areaWidth - _width)), Math.Clamp(y, 0, Math.Max(0, areaHeight - _height)));
    }

    // --- input (from DesktopLayerInput) ---

    public NativeMethods.RECT Bounds => new() { Left = _x, Top = _y, Right = _x + _width, Bottom = _y + _height };
    public bool Movable => true;

    public void MoveTo(int x, int y)
    {
        _x = x;
        _y = y;
        _window.SetBounds(_x, _y, _width, _height);
    }

    public void Moved() => _drawer.DockPosition = (_x + _width / 2, _y);

    private Entry? EntryAt(int x) =>
        _entries.FirstOrDefault(e => !e.Divider && x / _scale >= e.Left && x / _scale < e.Left + e.Width);

    public void Click(int x, int y)
    {
        var entry = EntryAt(x);
        if (entry == null) return;
        var inside = x / _scale - entry.Left; // DIPs from the slot's left edge
        if (entry.Media)
        {
            if (inside < MediaButtons) ShowMediaApp();
            else
                switch ((int)((inside - MediaButtons) / MediaButton))
                {
                    case 0: _media.Previous(); break;
                    case 1: _media.TogglePlayPause(); break;
                    default: _media.Next(); break;
                }
            return;
        }
        if (entry.Voice)
        {
            var state = _voice.Current;
            if (state == null) return;
            if (inside >= VoiceButtons + VoiceButton) _ = _voice.SetDeafAsync(!state.SelfDeaf);
            else if (inside >= VoiceButtons) _ = _voice.SetMuteAsync(!state.SelfMute);
            else
            {
                var screen = _desktop.IconToScreen(_x + x, _y + _height);
                ShowMembers(screen.X, screen.Y);
            }
            return;
        }
        if (entry.Drawer) _open();
        else if (entry.App != null) RunningApps.Activate(entry.App);
        else if (entry.Id != null) Launch(entry.Id);
    }

    private void Launch(string id) => DockApps.Launch(_drawer, id);

    public void RightClick(int screenX, int screenY)
    {
        var at = _desktop.ScreenToIcon(new NativeMethods.POINT { X = screenX, Y = screenY });
        var entry = EntryAt(at.X - _x);
        var items = new List<(string? Label, Action? Action)>();
        if (entry is { Voice: true })
        {
            items.Add(("음성 채널 사람 보기", () => ShowMembers(screenX, screenY)));
            items.Add((null, null));
        }
        else if (entry is { Media: true })
        {
            items.Add(("재생 중인 앱으로 가기", ShowMediaApp));
            items.Add((null, null));
        }
        else if (entry is { Drawer: false } e)
        {
            if (e.App != null) items.Add(("창으로 가기", () => RunningApps.Activate(e.App)));
            if (e.Id != null) items.Add(("새로 실행", () => Launch(e.Id)));
            else if (e.App?.ExePath != null) items.Add(("새로 실행", () => Launch(e.App.AppId != null ? "aumid:" + e.App.AppId : e.App.ExePath)));
            if (e.Id != null)
                items.Add(("독에서 빼기", () => _drawer.RemoveFromDock(e.Id)));
            else if (e.App != null)
                items.Add(("독에 고정", () => _drawer.AddToDock(e.App.AppId != null ? "aumid:" + e.App.AppId : e.App.ExePath!)));
            items.Add((null, null));
        }
        items.Add(("서랍 열기", _open));
        items.Add(("독 위치 초기화", () =>
        {
            _drawer.DockPosition = null;
            (_x, _y) = Place(_width);
            _window.SetBounds(_x, _y, _width, _height);
        }));
        DesktopPopupMenu.Show(screenX, screenY, items.ToArray());
    }

    public void Dispose()
    {
        _timer.Stop();
        _window.Dispose();
    }
}
