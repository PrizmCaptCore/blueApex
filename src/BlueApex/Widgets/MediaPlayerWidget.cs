using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueApex.Desktop;
using BlueApex.Sdk;
using BlueApex.Ui;
using Microsoft.Web.WebView2.Core;
using CoreWebView2CreationProperties = Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties;
using WebView = Microsoft.Web.WebView2.Wpf.WebView2;

namespace BlueApex.Widgets;

/// <summary>A media player on the home screen: video sites (YouTube, 치지직, ...) and music or video files.</summary>
internal sealed class MediaPlayerProvider : IWidgetProvider
{
    public string Id => "player";
    public string DisplayName => "미디어 플레이어";
    public (int Width, int Height) DefaultSizeDip => (480, 330);
    public IWidget Create(IWidgetContext context) => new MediaPlayerWidget(context);
}

/// <summary>
/// The player's embedded Edge (WebView2) browser profile, under %AppData%\BlueApex\browser-media.
/// Sign-ins to the video sites live there (the user signs in on each site's own page; this app never
/// sees a password), apart from the Steam/GOG profile used for game libraries. Every player widget and
/// the sign-in window share it, so signing in once counts for all of them.
/// </summary>
internal static class MediaBrowser
{
    private static readonly string UserDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "browser-media");

    // Identical properties make the controls share one browser process. HardwareMediaKeyHandling lets the
    // page's playback show up in Windows' media controls (and so in the dock's now-playing slot) and
    // answer the keyboard's play/pause keys.
    public static CoreWebView2CreationProperties Properties() => new()
    {
        UserDataFolder = UserDataDir,
        AdditionalBrowserArguments = "--enable-features=HardwareMediaKeyHandling",
    };

    public static readonly (string Label, string Url)[] Sites =
    {
        ("유튜브", "https://www.youtube.com/"),
        ("치지직", "https://chzzk.naver.com/"),
        ("숲 (SOOP)", "https://www.sooplive.co.kr/"),
        ("트위치", "https://www.twitch.tv/"),
    };
}

/// <summary>
/// Two kinds of content in one widget:
/// <list type="bullet">
/// <item>a web page (a video site), shown as it is; the site's own full-screen button fills the widget with the video;</item>
/// <item>files (from this PC, or a direct link to an .mp3/.mp4...), played by a small page of our own with a queue
/// that moves on by itself and remembers where it stopped.</item>
/// </list>
/// The window is a real (opaque) window that can take keyboard focus and can stay above other apps; see
/// <see cref="IBrowserWidget"/>. Settings (current page or queue, size, "항상 위") are saved in the widget's settings.
/// </summary>
internal sealed class MediaPlayerWidget : IBrowserWidget
{
    private const double BarHeight = 36, GripHeight = 12, MinWidth = 320, MinHeight = 200, MaxSide = 4000;
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private static readonly string[] MediaExtensions =
        { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus", ".mp4", ".m4v", ".webm", ".ogv", ".mov", ".mkv" };

    // Local files are served to the player page under made-up host names; the per-run secret keeps a web page
    // from guessing them. DenyCors still lets a <video> play them but stops any page from reading them.
    private static readonly string HostSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private readonly IWidgetContext _context;
    private readonly Settings _settings;
    private readonly WebView _web;
    private readonly TextBlock _title;
    private readonly TextBlock _pinGlyph;
    private readonly Button _pin;
    private readonly Border _toolbar, _bottom;
    private readonly HashSet<string> _mappedHosts = new();
    private bool _expectPlayer, _playerLoaded, _disposed;
    private TaskCompletionSource? _playerReady;
    private double _lastSavedPosition;
    private Point _gripOffset;

    public sealed class Settings
    {
        /// <summary>The web page last shown (when <see cref="Media"/> is false).</summary>
        public string? Url { get; set; }

        /// <summary>True when the widget plays <see cref="Queue"/> rather than showing <see cref="Url"/>.</summary>
        public bool Media { get; set; }

        /// <summary>Files (full paths) and direct links (http...) to play, in order.</summary>
        public List<string> Queue { get; set; } = new();
        public int Index { get; set; }

        /// <summary>Seconds into the current item, so it resumes where it stopped.</summary>
        public double Position { get; set; }

        public double Width { get; set; } = 480;
        public double Height { get; set; } = 330;
        public bool OnTop { get; set; }
    }

    public MediaPlayerWidget(IWidgetContext context)
    {
        _context = context;
        _settings = Load(context.Settings);

        _title = new TextBlock
        {
            Text = "미디어 플레이어",
            Foreground = Theme.TextDim,
            FontSize = Theme.FontSmall,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(Theme.Space2, 0, Theme.Space2, 0),
            ToolTip = "눌러서 주소 열기 · 끌어서 옮기기",
        };
        var back = IconButton("", "뒤로", () => { if (_web?.CoreWebView2 is { CanGoBack: true } core) core.GoBack(); }, out _);
        var home = IconButton("", "처음 화면", ShowStart, out _);
        var open = IconButton("", "파일 열기", OpenFiles, out _);
        _pin = IconButton(PinGlyph, PinTip, TogglePin, out _pinGlyph);
        Button? more = null;
        more = IconButton("", "메뉴", () => ShowMenu(more!), out _);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { back, home } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { open, _pin, more } };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        _toolbar = new Border
        {
            Height = BarHeight,
            Padding = new Thickness(Theme.Space1),
            Background = Theme.Panel,
            Child = new DockPanel { Children = { left, right, _title } },
        };

        // Bottom edge with a grip: drag it to resize the widget (the browser covers everything else).
        var grip = new Border
        {
            Width = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNWSE,
            ToolTip = "끌어서 크기 바꾸기",
            Child = new TextBlock { Text = "◢", FontSize = 9, Foreground = Theme.TextFaint, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) },
        };
        grip.MouseLeftButtonDown += OnGripDown;
        grip.MouseMove += OnGripMove;
        grip.MouseLeftButtonUp += OnGripUp;
        _bottom = new Border { Height = GripHeight, Background = Theme.Panel, Child = grip };

        _web = new WebView { CreationProperties = MediaBrowser.Properties(), DefaultBackgroundColor = System.Drawing.Color.Black };
        DockPanel.SetDock(_toolbar, Dock.Top);
        DockPanel.SetDock(_bottom, Dock.Bottom);
        View = new DockPanel { Background = Theme.Panel, Children = { _toolbar, _bottom, _web } };
        _ = InitAsync();
    }

    public FrameworkElement View { get; }

    private static Settings Load(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Settings();
        try
        {
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch (JsonException)
        {
            return new Settings();
        }
    }

    private void Save() => _context.Settings = JsonSerializer.Serialize(_settings);

    private static Button IconButton(string glyph, string tip, Action click, out TextBlock text)
    {
        text = new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 14, Foreground = Theme.Text };
        var button = new Button
        {
            Content = text,
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = tip,
            Focusable = false,
        };
        button.Click += (_, _) => click();
        return button;
    }

    // --- browser ---

    private async Task InitAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or ArgumentException)
        {
            _context.Log($"browser unavailable: {ex.Message}");
            _title.Text = "내장 브라우저(WebView2)를 열 수 없습니다";
            return;
        }
        if (_disposed) return;
        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += (_, _) => { if (_playerLoaded) _playerReady?.TrySetResult(); };
        core.SourceChanged += (_, _) =>
        {
            if (_playerLoaded || !IsWeb(core.Source)) return;
            _settings.Url = core.Source;
            _settings.Media = false;
            Save();
        };
        core.DocumentTitleChanged += (_, _) => { if (!_playerLoaded && IsWeb(core.Source)) _title.Text = core.DocumentTitle; };
        // A site's full-screen button: the video fills the whole widget, so the bars step aside.
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            var full = core.ContainsFullScreenElement;
            _toolbar.Visibility = _bottom.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        };
        core.WebMessageReceived += OnMessage;
        Restore();
    }

    private void Restore()
    {
        if (_settings.Media && _settings.Queue.Count > 0) _ = LoadCurrentAsync(play: false, at: _settings.Position);
        else if (_settings.Url != null && IsWeb(_settings.Url)) _web.CoreWebView2.Navigate(_settings.Url);
        else ShowStart();
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // A file dropped on the widget: play it with our player (queue, resume) instead of the bare file view.
        if (e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            var path = new Uri(e.Uri).LocalPath;
            _web.Dispatcher.BeginInvoke(() => PlayItems(new[] { path }));
            return;
        }
        _playerLoaded = _expectPlayer;
        _expectPlayer = false;
    }

    private static bool IsWeb(string? url) =>
        url != null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    private static bool IsMediaLink(string url) =>
        IsWeb(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && MediaExtensions.Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant());

    /// <summary>Opens what the user typed: a file, a media link, a web address, or else a YouTube search.</summary>
    private void Open(string text)
    {
        text = text.Trim().Trim('"');
        if (text.Length == 0 || _web.CoreWebView2 == null) return;
        if (File.Exists(text) || IsMediaLink(text))
        {
            PlayItems(new[] { text });
            return;
        }
        if (!text.Contains("://"))
            text = text.Contains('.') && !text.Contains(' ')
                ? "https://" + text
                : "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(text);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            _title.Text = "열 수 없는 주소입니다";
            return;
        }
        _web.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    private void AskAddress()
    {
        var current = _settings.Media ? "" : _settings.Url ?? "";
        var text = _context.AskText("주소 열기", current, "사이트 주소, 음악·영상 파일 주소, 또는 유튜브에서 찾을 말");
        if (text != null) Open(text);
    }

    private void ShowStart()
    {
        if (_web.CoreWebView2 == null) return;
        _title.Text = "미디어 플레이어";
        _web.CoreWebView2.NavigateToString(StartPage());
    }

    // --- files and the queue ---

    private void OpenFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "재생할 파일",
            Multiselect = true,
            Filter = "음악·동영상|" + string.Join(';', MediaExtensions.Select(e => "*" + e)) + "|모든 파일|*.*",
        };
        if (dialog.ShowDialog() == true) PlayItems(dialog.FileNames);
    }

    /// <summary>Replaces the queue and starts its first item.</summary>
    private void PlayItems(IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        _settings.Queue = list;
        _settings.Index = 0;
        _settings.Position = 0;
        _settings.Media = true;
        Save();
        _ = LoadCurrentAsync(play: true, at: 0);
    }

    private void Step(int by)
    {
        if (_settings.Queue.Count == 0) return;
        var next = _settings.Index + by;
        if (next < 0 || next >= _settings.Queue.Count) return;
        _settings.Index = next;
        _settings.Position = 0;
        Save();
        _ = LoadCurrentAsync(play: true, at: 0);
    }

    private async Task LoadCurrentAsync(bool play, double at)
    {
        var core = _web.CoreWebView2;
        if (core == null || _settings.Queue.Count == 0) return;
        _settings.Index = Math.Clamp(_settings.Index, 0, _settings.Queue.Count - 1);
        var item = _settings.Queue[_settings.Index];
        var name = IsWeb(item) ? Uri.UnescapeDataString(Path.GetFileName(new Uri(item).AbsolutePath)) : Path.GetFileName(item);
        string source;
        if (IsWeb(item))
        {
            source = item;
        }
        else if (File.Exists(item))
        {
            source = ServeLocal(item);
        }
        else
        {
            _title.Text = "파일이 없습니다: " + name;
            return;
        }
        await EnsurePlayerAsync();
        if (_disposed) return;
        _title.Text = _settings.Queue.Count > 1 ? $"{name}  ({_settings.Index + 1}/{_settings.Queue.Count})" : name;
        _lastSavedPosition = at;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { cmd = "load", src = source, title = Path.GetFileNameWithoutExtension(name), at, play }));
    }

    private async Task EnsurePlayerAsync()
    {
        if (_playerLoaded && _playerReady?.Task.IsCompleted == true) return;
        _playerReady = new TaskCompletionSource();
        _expectPlayer = true;
        _web.CoreWebView2.NavigateToString(PlayerPage);
        await _playerReady.Task;
    }

    // A local file becomes https://f<hash>-<secret>.blueapex/<name>, one made-up host per folder.
    private string ServeLocal(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant())))[..10].ToLowerInvariant();
        var host = $"f{hash}-{HostSecret}.blueapex";
        if (_mappedHosts.Add(host))
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(host, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        return $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(path))}";
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (IsWeb(e.Source)) return; // only our own pages (start page, player) talk to us
        string? ev;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            root = doc.RootElement.Clone();
            ev = root.TryGetProperty("ev", out var v) ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return;
        }
        switch (ev)
        {
            case "ended":
                if (_settings.Index + 1 < _settings.Queue.Count) Step(+1);
                else { _settings.Position = 0; Save(); }
                break;
            case "next": Step(+1); break;
            case "prev": Step(-1); break;
            case "error":
                _title.Text = "재생할 수 없는 파일입니다: " + _title.Text;
                _context.Log($"cannot play: {_settings.Queue.ElementAtOrDefault(_settings.Index)}");
                break;
            case "time" when root.TryGetProperty("t", out var t) && t.TryGetDouble(out var seconds):
                _settings.Position = seconds;
                // Saving rewrites the layout file, so only every so often.
                if (Math.Abs(seconds - _lastSavedPosition) >= 15)
                {
                    _lastSavedPosition = seconds;
                    Save();
                }
                break;
            case "go" when root.TryGetProperty("text", out var text):
                Open(text.GetString() ?? "");
                break;
            case "open":
                OpenFiles();
                break;
        }
    }

    // --- menu, "항상 위", sign-in ---

    public IEnumerable<WidgetMenuItem> MenuItems
    {
        get
        {
            foreach (var (label, url) in MediaBrowser.Sites)
                yield return new WidgetMenuItem(label, () => Open(url));
            yield return WidgetMenuItem.Separator;
            yield return new WidgetMenuItem("주소 열기...", AskAddress);
            yield return new WidgetMenuItem("파일 열기...", OpenFiles);
            if (_settings.Media && _settings.Queue.Count > 1)
            {
                yield return new WidgetMenuItem("다음 곡", () => Step(+1));
                yield return new WidgetMenuItem("이전 곡", () => Step(-1));
            }
            yield return WidgetMenuItem.Separator;
            yield return new WidgetMenuItem(_settings.OnTop ? "항상 위 해제" : "항상 위에 표시", TogglePin);
            yield return new WidgetMenuItem("로그인 창 열기 (큰 창)...", OpenSignInWindow);
            yield return new WidgetMenuItem("플레이어 로그인 정보 지우기...", ClearSignIns);
        }
    }

    private void ShowMenu(Button anchor)
    {
        var at = anchor.PointToScreen(new Point(0, anchor.ActualHeight)); // physical pixels
        var items = MenuItems.Select(i => (i.Label, i.Action)).ToArray();
        DesktopPopupMenu.Show((int)at.X, (int)at.Y, items);
    }

    public bool OnTop => _settings.OnTop;
    public event Action? OnTopChanged;

    private string PinGlyph => _settings.OnTop ? "" : "";
    private string PinTip => _settings.OnTop ? "항상 위 해제" : "항상 위에 표시 (다른 창 위에 떠 있게)";

    private void TogglePin()
    {
        _settings.OnTop = !_settings.OnTop;
        Save();
        _pinGlyph.Text = PinGlyph;
        _pin.ToolTip = PinTip;
        OnTopChanged?.Invoke();
    }

    // Signing in inside a small widget is cramped; this opens the same profile in an ordinary window.
    private void OpenSignInWindow()
    {
        var current = _web.CoreWebView2?.Source;
        var url = !_settings.Media && IsWeb(current) ? current! : MediaBrowser.Sites[0].Url;
        var browser = new WebView { CreationProperties = MediaBrowser.Properties(), Source = new Uri(url) };
        var window = new Window
        {
            Title = "로그인 — 미디어 플레이어 (로그인한 뒤 이 창을 닫으세요)",
            Width = 560,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = browser,
        };
        Theme.ApplyWindow(window);
        window.Closed += (_, _) =>
        {
            browser.Dispose();
            if (!_settings.Media) _web.CoreWebView2?.Reload(); // the widget's page picks up the new sign-in
        };
        window.Show();
    }

    private async void ClearSignIns()
    {
        if (_web.CoreWebView2 == null) return;
        if (MessageBox.Show("미디어 플레이어에서 로그인한 사이트(유튜브, 치지직 등)에서 모두 로그아웃하고 방문 기록을 지울까요?",
                "BlueApex", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            await _web.CoreWebView2.Profile.ClearBrowsingDataAsync();
            _settings.Url = null;
            Save();
            ShowStart();
        }
        catch (COMException ex)
        {
            _context.Log($"clear browsing data failed: {ex.Message}");
        }
    }

    // --- size (the grip) ---

    public (double Width, double Height) DesiredSizeDip => (_settings.Width, _settings.Height);
    public event Action? Resized;

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(View);
        _gripOffset = new Point(_settings.Width - p.X, _settings.Height - p.Y);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true; // not a press on the widget (that would start moving it)
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!((UIElement)sender).IsMouseCaptured) return;
        // The window grows from its top-left corner, so positions relative to the view stay comparable.
        var p = e.GetPosition(View);
        _settings.Width = Math.Clamp(p.X + _gripOffset.X, MinWidth, MaxSide);
        _settings.Height = Math.Clamp(p.Y + _gripOffset.Y, MinHeight, MaxSide);
        Resized?.Invoke();
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        var grip = (UIElement)sender;
        if (!grip.IsMouseCaptured) return;
        grip.ReleaseMouseCapture();
        Save();
        e.Handled = true;
    }

    // --- IInternalWidget: the toolbar's free space opens the address box; no scrolling of our own ---

    public void OnClickAt(double x, double y)
    {
        if (y <= BarHeight) AskAddress();
    }

    public bool ScrollsAt(double x, double y) => false;
    public void ScrollBy(double deltaX, double deltaY) { }
    public void Wheel(int notches) { }

    public void Dispose()
    {
        _disposed = true;
        if (_settings.Media) Save(); // the latest position
        _web.Dispose();
    }

    // --- our two pages ---

    private static string StartPage()
    {
        var links = string.Concat(MediaBrowser.Sites.Select(s => $"<a href=\"{WebUtility.HtmlEncode(s.Url)}\">{WebUtility.HtmlEncode(s.Label)}</a>"));
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
            html,body{margin:0;height:100%;background:#161618;color:#fff;font-family:'Segoe UI','Malgun Gothic',sans-serif;overflow:hidden}
            body{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:16px}
            form{display:flex;width:min(86vw,520px)}
            input{flex:1;background:#2c2c2e;border:1px solid #ffffff1a;border-radius:10px;color:#fff;font:inherit;font-size:15px;padding:10px 14px;outline:none}
            input:focus{border-color:#ffffff59}
            .sites{display:flex;gap:8px;flex-wrap:wrap;justify-content:center;padding:0 12px}
            a,button{background:#ffffff1f;color:#fff;text-decoration:none;border:0;border-radius:16px;padding:8px 16px;font:inherit;font-size:14px;cursor:pointer}
            a:hover,button:hover{background:#ffffff33}
            p{margin:0;color:#ffffff80;font-size:12px}
            </style></head><body>
            <form id="f"><input id="q" placeholder="주소, 음악·영상 링크, 또는 유튜브 검색어" autocomplete="off"></form>
            <div class="sites">{{links}}<button id="o">파일 열기</button></div>
            <p>음악·영상 파일을 이 창에 끌어다 놓아도 됩니다</p>
            <script>
              const post = m => chrome.webview.postMessage(m);
              document.getElementById('f').onsubmit = e => { e.preventDefault(); post({ ev: 'go', text: document.getElementById('q').value }); };
              document.getElementById('o').onclick = () => post({ ev: 'open' });
            </script></body></html>
            """;
    }

    // Plays one item at a time; C# sends {cmd:'load'} and hears back ended / time / error / next / prev.
    private const string PlayerPage = """
        <!doctype html><html><head><meta charset="utf-8"><style>
        html,body{margin:0;height:100%;background:#000;color:#fff;font-family:'Segoe UI','Malgun Gothic',sans-serif;overflow:hidden}
        video{position:fixed;inset:0;width:100%;height:100%;object-fit:contain;background:#000}
        #t{position:fixed;left:0;right:0;top:38%;text-align:center;font-size:18px;opacity:.85;padding:0 16px;pointer-events:none}
        </style></head><body>
        <video id="v" controls playsinline></video><div id="t"></div>
        <script>
          const v = document.getElementById('v'), t = document.getElementById('t');
          const post = m => chrome.webview.postMessage(m);
          let last = 0;
          chrome.webview.addEventListener('message', e => {
            const m = e.data;
            if (m.cmd !== 'load') return;
            t.textContent = m.title; t.style.display = 'block';
            v.addEventListener('loadedmetadata', () => {
              if (m.at > 0 && m.at < v.duration - 1) v.currentTime = m.at;
              last = v.currentTime;
              t.style.display = v.videoWidth ? 'none' : 'block'; // music: the title stays in view
              if (m.play) v.play().catch(() => {});
            }, { once: true });
            v.src = m.src;
            if ('mediaSession' in navigator) navigator.mediaSession.metadata = new MediaMetadata({ title: m.title });
          });
          v.onended = () => post({ ev: 'ended' });
          v.onerror = () => post({ ev: 'error' });
          v.ontimeupdate = () => { if (Math.abs(v.currentTime - last) >= 10) { last = v.currentTime; post({ ev: 'time', t: v.currentTime }); } };
          v.onpause = () => post({ ev: 'time', t: v.currentTime });
          if ('mediaSession' in navigator) {
            navigator.mediaSession.setActionHandler('nexttrack', () => post({ ev: 'next' }));
            navigator.mediaSession.setActionHandler('previoustrack', () => post({ ev: 'prev' }));
          }
        </script></body></html>
        """;
}
