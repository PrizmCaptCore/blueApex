using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using BlueApex.Ui;
using Microsoft.Web.WebView2.Core;
using CoreWebView2CreationProperties = Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties;
using WebView = Microsoft.Web.WebView2.Wpf.WebView2;

namespace BlueApex.Desktop;

/// <summary>
/// "소리만 듣기": music and video sound with no picture, controlled from the dock's media slot.
/// One embedded Edge (WebView2) browser in an ordinary window that is hidden while it plays:
/// <list type="bullet">
/// <item>YouTube (or any site): the window shows so the user can pick something; as soon as it starts playing
/// (with sound) the window hides itself and only the sound remains. "화면 보기" brings it back to pick another.</item>
/// <item>files from this PC and direct links (.mp3, .mp4...): played by a small page of our own, never shown,
/// one after another.</item>
/// </list>
/// A script added to every page reports what plays (title, artist, artwork from the page's media session) and
/// answers play/pause/next/previous, so the dock drives this player directly instead of through Windows' media
/// controls. The browser profile (%AppData%\BlueApex\browser-media) keeps the user's sign-ins to the sites;
/// this app never sees a password. Nothing is restored after a restart: the player starts only when asked.
/// </summary>
internal sealed class BackgroundPlayer : IDisposable
{
    private static readonly string UserDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "browser-media");

    private static readonly string[] MediaExtensions =
        { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus", ".mp4", ".m4v", ".webm", ".ogv", ".mov", ".mkv" };

    // Local files are served to the player page under made-up host names; the per-run secret keeps a web page
    // from guessing them, and DenyCors lets <audio> play them while no page can read them.
    private static readonly string HostSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private Window? _window;
    private WebView? _web;
    private Task<CoreWebView2?>? _starting;
    private bool _quitting, _browsing, _disposed;
    private readonly HashSet<string> _mappedHosts = new();
    private List<string> _queue = new();
    private int _index;
    private bool _filesMode;
    private TaskCompletionSource? _playerReady;
    private bool _expectPlayer;
    private string? _artUrl;
    private byte[]? _art;

    /// <summary>What plays (or is paused) in the player; null when it has nothing. Shown by the dock's media slot.</summary>
    public NowPlayingInfo? Current { get; private set; }

    /// <summary>Whether the player is running (it may be paused, or showing a page with nothing playing yet).</summary>
    public bool Active => _web != null;

    /// <summary>Raised on the UI thread when <see cref="Current"/> or <see cref="Active"/> changed.</summary>
    public event Action? Changed;

    // --- what the dock's menu offers ---

    /// <summary>Shows YouTube in the player window; picking something to play hides the window again.</summary>
    public async void OpenYouTube()
    {
        var core = await EnsureAsync();
        if (core == null) return;
        _filesMode = false;
        if (!core.Source.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)) core.Navigate("https://www.youtube.com/");
        ShowWindow();
    }

    public void OpenFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "소리만 듣기: 재생할 파일",
            Multiselect = true,
            Filter = "음악·동영상|" + string.Join(';', MediaExtensions.Select(e => "*" + e)) + "|모든 파일|*.*",
        };
        if (dialog.ShowDialog() == true) _ = PlayFilesAsync(dialog.FileNames);
    }

    public void OpenAddress()
    {
        var text = Zones.ZoneMenu.AskText("소리만 듣기: 주소로 열기", "",
            "유튜브 영상·재생목록 주소, 음악 파일 주소(.mp3 등), 다른 사이트 주소, 또는 유튜브에서 찾을 말", multiline: false);
        if (!string.IsNullOrWhiteSpace(text)) _ = OpenAsync(text.Trim().Trim('"'));
    }

    // A file or media link plays at once; a page address plays hidden (sites start on their own);
    // anything else is a YouTube search, shown so the user can pick.
    private async Task OpenAsync(string text)
    {
        if (File.Exists(text) || IsMediaLink(text))
        {
            await PlayFilesAsync(new[] { text });
            return;
        }
        var search = !text.Contains("://") && (text.Contains(' ') || !text.Contains('.'));
        var url = search ? "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(text)
                : text.Contains("://") ? text : "https://" + text;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        var core = await EnsureAsync();
        if (core == null) return;
        _filesMode = false;
        core.Navigate(uri.AbsoluteUri);
        if (search) ShowWindow();
    }

    /// <summary>Brings the player window back (to pick something else); playing goes on.</summary>
    public void ShowWindow()
    {
        if (_window == null) return;
        _browsing = true;
        if (!_window.IsVisible)
        {
            _window.ShowInTaskbar = true;
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void HideWindow()
    {
        _browsing = false;
        _window?.Hide();
    }

    /// <summary>Stops everything and closes the browser.</summary>
    public void Stop()
    {
        if (_window == null) return;
        _quitting = true;
        _window.Close(); // Closed disposes the browser
    }

    public void TogglePlayPause() => Run("window.__blueapex && window.__blueapex.toggle()");
    public void Next() => Run("window.__blueapex && window.__blueapex.next()");
    public void Previous() => Run("window.__blueapex && window.__blueapex.prev()");

    private void Run(string script)
    {
        try
        {
            _ = _web?.CoreWebView2?.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Log.Write($"player: {ex.Message}");
        }
    }

    // --- the browser ---

    private Task<CoreWebView2?> EnsureAsync()
    {
        if (_web?.CoreWebView2 is { } ready) return Task.FromResult<CoreWebView2?>(ready);
        return _starting ??= CreateAsync();
    }

    private async Task<CoreWebView2?> CreateAsync()
    {
        _quitting = false;
        _web = new WebView
        {
            // No user gesture happens inside the hidden page, so autoplay must be allowed for anything to start.
            // HardwareMediaKeyHandling: the keyboard's play/pause keys reach the player too.
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = UserDataDir,
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --enable-features=HardwareMediaKeyHandling",
            },
            DefaultBackgroundColor = System.Drawing.Color.Black,
        };
        _window = new Window
        {
            Title = "BlueApex 소리만 듣기 — 재생을 시작하면 이 창은 숨겨집니다",
            Width = 1000,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = _web,
        };
        Theme.ApplyWindow(_window);
        // The close button hides the window while something plays (the dock still controls it), otherwise ends the player.
        _window.Closing += (_, e) =>
        {
            if (_quitting || Current is not { Playing: true }) return;
            e.Cancel = true;
            HideWindow();
        };
        _window.Closed += (_, _) => TearDown();
        // The browser needs a real window to start in; it begins hidden and only shows when asked to.
        _window.Left = -32000;
        _window.Top = -32000;
        _window.Show();
        _window.Hide();
        _window.Left = (SystemParameters.WorkArea.Width - _window.Width) / 2;
        _window.Top = (SystemParameters.WorkArea.Height - _window.Height) / 2;
        try
        {
            await _web.EnsureCoreWebView2Async();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or ArgumentException)
        {
            Log.Write($"player: browser unavailable: {ex.Message}");
            _quitting = true;
            _window.Close();
            return null;
        }
        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ReportScript);
        core.WebMessageReceived += OnMessage;
        core.NavigationStarting += (_, e) =>
        {
            if (!_expectPlayer && _filesMode) _filesMode = false; // the user browsed away from the files
            _expectPlayer = false;
        };
        core.NavigationCompleted += (_, _) => _playerReady?.TrySetResult();
        Log.Write("player: started");
        Changed?.Invoke();
        return core;
    }

    private void TearDown()
    {
        _web?.Dispose();
        _web = null;
        _window = null;
        _starting = null;
        _filesMode = false;
        _queue = new List<string>();
        _mappedHosts.Clear();
        _artUrl = null;
        _art = null;
        Current = null;
        if (!_disposed) Changed?.Invoke();
    }

    // --- files and direct links ---

    private static bool IsMediaLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && MediaExtensions.Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant());

    private async Task PlayFilesAsync(IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        var core = await EnsureAsync();
        if (core == null) return;
        _queue = list;
        _index = 0;
        HideWindow();
        await PlayCurrentAsync(core);
    }

    private async Task Step(int by)
    {
        if (!_filesMode || _web?.CoreWebView2 is not { } core) return;
        var next = _index + by;
        if (next < 0 || next >= _queue.Count) return;
        _index = next;
        await PlayCurrentAsync(core);
    }

    private async Task PlayCurrentAsync(CoreWebView2 core)
    {
        var item = _queue[_index];
        string source;
        if (IsMediaLink(item)) source = item;
        else if (File.Exists(item)) source = ServeLocal(core, item);
        else
        {
            Log.Write($"player: file not found: {item}");
            await Step(+1);
            return;
        }
        if (!_filesMode)
        {
            _filesMode = true;
            _playerReady = new TaskCompletionSource();
            _expectPlayer = true;
            core.NavigateToString(PlayerPage);
            await _playerReady.Task;
        }
        var title = IsMediaLink(item) ? Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(item).AbsolutePath)) : Path.GetFileNameWithoutExtension(item);
        var position = _queue.Count > 1 ? $"{_index + 1}/{_queue.Count}" : "";
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { cmd = "load", src = source, title, artist = position }));
    }

    // A local file becomes https://f<hash>-<secret>.blueapex/<name>, one made-up host per folder.
    private string ServeLocal(CoreWebView2 core, string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant())))[..10].ToLowerInvariant();
        var host = $"f{hash}-{HostSecret}.blueapex";
        if (_mappedHosts.Add(host))
            core.SetVirtualHostNameToFolderMapping(host, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        return $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(path))}";
    }

    // --- what the page reports ---

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        var ev = root.TryGetProperty("ev", out var v) ? v.GetString() : null;
        switch (ev)
        {
            case "started":
                Report(root);
                // Something was picked while the window showed: give it a moment, then step aside.
                if (_browsing && _window?.IsVisible == true)
                    _ = Task.Delay(1200).ContinueWith(_ => _window?.Dispatcher.BeginInvoke(() =>
                    {
                        if (Current is { Playing: true } && _browsing) HideWindow();
                    }));
                break;
            case "state":
                Report(root);
                break;
            case "ended" when _filesMode:
                _ = Step(+1);
                break;
            case "next" when _filesMode:
                _ = Step(+1);
                break;
            case "prev" when _filesMode:
                _ = Step(-1);
                break;
            case "error" when _filesMode:
                Log.Write($"player: cannot play {_queue.ElementAtOrDefault(_index)}");
                _ = Step(+1);
                break;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private void Report(JsonElement root)
    {
        var hasMedia = root.TryGetProperty("hasMedia", out var h) && h.ValueKind == JsonValueKind.True;
        var playing = root.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;
        if (!hasMedia)
        {
            Publish(null);
            return;
        }
        // "(3) Song title - YouTube" -> "Song title"
        var title = Regex.Replace(Text(root, "title"), @"^\(\d+\)\s*|\s+-\s+YouTube$", "").Trim();
        if (title.Length == 0) title = "재생 중";
        var artUrl = Text(root, "art");
        if (artUrl != _artUrl)
        {
            _artUrl = artUrl;
            _art = null;
            if (artUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) _ = FetchArtAsync(artUrl);
        }
        Publish(new NowPlayingInfo(title, Text(root, "artist"), playing, _art, "BlueApex"));
    }

    private async Task FetchArtAsync(string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            if (url != _artUrl || Current == null) return;
            _art = bytes;
            Publish(Current with { Art = bytes });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // no artwork is fine
        }
    }

    private void Publish(NowPlayingInfo? info)
    {
        if (info == Current) return;
        if (info != null && Current != null && info.Title == Current.Title && info.Artist == Current.Artist
            && info.Playing == Current.Playing && ReferenceEquals(info.Art, Current.Art)) return;
        if (info?.Title != Current?.Title || info?.Playing != Current?.Playing)
            Log.Write(info == null ? "player: nothing playing" : $"player: {(info.Playing ? "playing" : "paused")}: {info.Title}");
        Current = info;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        if (_window != null)
        {
            _quitting = true;
            _window.Close();
        }
    }

    // Added to every page. Tells us what plays (state every few seconds and on play/pause), and offers
    // toggle / next / prev. Muted playback (YouTube's hover previews) is not "something was picked".
    private const string ReportScript = """
        (() => {
          if (window.top !== window || window.__blueapex) return;
          const post = m => { try { chrome.webview.postMessage(m); } catch (e) {} };
          let active = null; // the last element that started playing with sound
          const media = () => (active && active.isConnected) ? active : null;
          const state = ev => {
            const m = media(), md = navigator.mediaSession && navigator.mediaSession.metadata;
            const art = md && md.artwork && md.artwork.length ? md.artwork[md.artwork.length - 1].src : '';
            return { ev, hasMedia: !!(m && (m.currentSrc || m.src)), playing: !!(m && !m.paused && !m.ended),
                     title: (md && md.title) || document.title, artist: (md && md.artist) || '', art };
          };
          document.addEventListener('play', e => {
            const m = e.target;
            if (m.muted || m.volume === 0) return;
            active = m;
            post(state('started'));
          }, true);
          for (const n of ['pause', 'ended', 'loadedmetadata']) document.addEventListener(n, () => post(state('state')), true);
          setInterval(() => post(state('state')), 3000);
          window.__blueapex = {
            toggle() { const m = media(); if (m) { if (m.paused) m.play().catch(() => {}); else m.pause(); } },
            next() { const b = document.querySelector('.ytp-next-button'); if (b) b.click(); else post({ ev: 'next' }); },
            prev() {
              const b = document.querySelector('.ytp-prev-button');
              if (b && b.offsetParent) { b.click(); return; }
              const m = media();
              if (m && m.currentTime > 3) m.currentTime = 0; else post({ ev: 'prev' });
            },
          };
        })();
        """;

    // Plays one file at a time; C# sends {cmd:'load'} and hears back ended / error / next / prev.
    private const string PlayerPage = """
        <!doctype html><html><head><meta charset="utf-8"><title>BlueApex</title></head>
        <body style="margin:0;background:#000">
        <video id="v" controls style="width:100vw;height:100vh;background:#000"></video>
        <script>
          const v = document.getElementById('v');
          const post = m => chrome.webview.postMessage(m);
          chrome.webview.addEventListener('message', e => {
            const m = e.data;
            if (m.cmd !== 'load') return;
            if ('mediaSession' in navigator) navigator.mediaSession.metadata = new MediaMetadata({ title: m.title, artist: m.artist });
            v.src = m.src;
            v.play().catch(() => {});
          });
          v.onended = () => post({ ev: 'ended' });
          v.onerror = () => post({ ev: 'error' });
          if ('mediaSession' in navigator) {
            navigator.mediaSession.setActionHandler('nexttrack', () => post({ ev: 'next' }));
            navigator.mediaSession.setActionHandler('previoustrack', () => post({ ev: 'prev' }));
          }
        </script></body></html>
        """;
}
