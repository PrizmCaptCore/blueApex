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

    // Spotify's Web Playback SDK runs in a page of our own under this made-up address (https, because
    // protected playback only works on a "secure" page). See StartSpotifyDeviceAsync.
    private const string SpotifyHost = "spotify.blueapex";
    private Func<Task<string>>? _spotifyToken;
    private TaskCompletionSource<string>? _spotifyReady;
    private string? _spotifyDevice;
    private int _spotifyErrors;
    private DateTime _spotifyErrorAt;

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

    // --- Spotify without the Spotify app ---

    /// <summary>Whether we hold a device id from an earlier start (it may have gone stale; see StartSpotifyDeviceAsync).</summary>
    public bool HasSpotifyDevice => _spotifyDevice != null;

    /// <summary>
    /// Turns this player into a Spotify playback device named "BlueApex" (Spotify's Web Playback SDK in the
    /// hidden browser) and returns its device id; <see cref="Spotify"/> then tells Spotify to play there.
    /// Whatever played here before (YouTube, files) is replaced. Throws <see cref="SpotifyException"/> when
    /// the browser cannot do it (no WebView2, protected playback refused, not Premium...).
    /// </summary>
    /// <param name="token">Gives a valid Spotify access token; the SDK asks again whenever it needs a new one.</param>
    /// <param name="fresh">
    /// Throw away the device we think we have and make a new one. Needed when Spotify answers "device not found":
    /// after the network was away for a while (sleep, a dropped connection) Spotify forgets the device, and the
    /// id we remembered points at nothing.
    /// </param>
    public async Task<string> StartSpotifyDeviceAsync(Func<Task<string>> token, bool fresh = false)
    {
        if (fresh) _spotifyDevice = null;
        if (_spotifyDevice != null) return _spotifyDevice;
        if (_spotifyReady is { Task.IsCompleted: false } starting) return await starting.Task;
        var core = await EnsureAsync() ?? throw new SpotifyException("내장 브라우저(WebView2)를 시작하지 못했습니다.");
        _spotifyToken = token;
        var ready = _spotifyReady = new TaskCompletionSource<string>();
        HideWindow();
        core.Navigate($"https://{SpotifyHost}/");
        if (await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(20))) != ready.Task)
            ready.TrySetException(new SpotifyException("내장 Spotify 플레이어가 20초 안에 준비되지 않았습니다."));
        return await ready.Task;
    }

    // Only our own page may ask for the token or claim to be the device: any site can post messages to us.
    private bool OnSpotifyPage(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == SpotifyHost;

    private async Task SendSpotifyTokenAsync()
    {
        if (_spotifyToken == null) return;
        try
        {
            var token = await _spotifyToken();
            // Checked again after the wait: the browser may have gone elsewhere, and the token must not follow it.
            if (_web?.CoreWebView2 is { } core && OnSpotifyPage(core.Source))
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { cmd = "token", token }));
        }
        catch (SpotifyException ex)
        {
            Log.Write($"player: spotify token unavailable: {ex.Message}");
            _spotifyReady?.TrySetException(ex);
            // The SDK asked for a token (usually to reconnect after the network came back) and got none, so it
            // stays disconnected and Spotify drops the device. Forget the id: the next play makes a new device.
            _spotifyDevice = null;
        }
    }

    // Leaves our Spotify page, which ends the device (see NavigationStarting) and silences it.
    private void StopSpotifyPage()
    {
        _spotifyDevice = null;
        if (_web?.CoreWebView2 is { } core && OnSpotifyPage(core.Source)) core.Navigate("about:blank");
        Publish(null);
    }

    private void OnSpotifyMessage(string ev, JsonElement root)
    {
        switch (ev)
        {
            case "sp-token":
                _ = SendSpotifyTokenAsync();
                break;
            case "sp-ready":
                _spotifyDevice = Text(root, "device");
                Log.Write("player: spotify device ready");
                ApplyVolume();
                _spotifyReady?.TrySetResult(_spotifyDevice);
                break;
            case "sp-gone": // the SDK lost its connection; the next play starts it again
                _spotifyDevice = null;
                break;
            case "sp-error":
                var kind = Text(root, "kind");
                var message = Text(root, "message");
                Log.Write($"player: spotify {kind}: {message}");
                if (kind == "playback_error")
                {
                    // One track failing is fine (Spotify moves on). Several in a row means nothing will play here,
                    // and Spotify would race through the whole playlist failing each track: stop instead.
                    var now = DateTime.UtcNow;
                    _spotifyErrors = now - _spotifyErrorAt < TimeSpan.FromSeconds(3) ? _spotifyErrors + 1 : 1;
                    _spotifyErrorAt = now;
                    if (_spotifyErrors < 3) break;
                    _spotifyErrors = 0;
                    Log.Write("player: spotify stopped after repeated playback errors");
                    StopSpotifyPage();
                    // Shown after this browser callback returns: a dialog must not block inside it.
                    Application.Current.Dispatcher.BeginInvoke(() => MessageBox.Show(
                        "Spotify가 내장 플레이어에서 곡을 재생하지 못해 멈췄습니다.\n\n" +
                        "트레이 \"Spotify 연결 (고급)...\"에서 \"연결\"을 다시 누른 뒤 시도해 주세요.\n" +
                        "그래도 같으면 %AppData%\\BlueApex\\log.txt의 \"player: spotify\" 줄을 확인해 주세요.",
                        "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning));
                    break;
                }
                _spotifyReady?.TrySetException(new SpotifyException(kind switch
                {
                    "initialization_error" => "이 PC의 내장 브라우저가 Spotify의 보호된 재생(DRM)을 시작하지 못했습니다: " + message,
                    "authentication_error" => "내장 플레이어 로그인이 거부됐습니다. 트레이 \"Spotify 연결 (고급)...\"에서 \"연결\"을 다시 눌러 주세요: " + message,
                    "account_error" => "내장 플레이어는 Spotify Premium 계정만 쓸 수 있습니다.",
                    _ => "내장 Spotify 플레이어를 켜지 못했습니다: " + message,
                }));
                // The SDK can still report "ready" after one of these; a device that cannot log in must not be used.
                StopSpotifyPage();
                break;
        }
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

    // --- volume ---
    // The browser has no volume knob of its own, so the page is told: each page's __blueapex.volume sets it on
    // whatever plays there (media elements, or Spotify's player). A new page starts at full volume, so it is
    // sent again whenever a page finishes loading or something starts playing. Mute is the browser's own switch.

    private double _volume = 1;
    private bool _muted;

    /// <summary>How loud this player is, 0 to 1 (the PC's own volume still applies on top).</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            ApplyVolume();
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_web?.CoreWebView2 is { } core) core.IsMuted = value;
        }
    }

    private void ApplyVolume()
    {
        var v = _volume.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); // "0.6", never "0,6"
        Run($"window.__blueapexVolume = {v}; window.__blueapex && window.__blueapex.volume && window.__blueapex.volume({v})");
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
            // Any new page ends the Spotify device (a reload makes a new one); leaving for another site also ends a pending start.
            _spotifyDevice = null;
            if (!OnSpotifyPage(e.Uri)) _spotifyReady?.TrySetException(new SpotifyException("내장 Spotify 플레이어가 다른 재생으로 바뀌었습니다."));
        };
        // Our Spotify page is not a file: requests to its made-up address are answered from memory.
        core.AddWebResourceRequestedFilter($"https://{SpotifyHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var isPage = Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/";
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(isPage ? SpotifyPage : "")),
                isPage ? 200 : 404, isPage ? "OK" : "Not Found", "Content-Type: text/html; charset=utf-8");
        };
        core.NavigationCompleted += (_, _) =>
        {
            _playerReady?.TrySetResult();
            ApplyVolume();
        };
        core.IsMuted = _muted;
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
        _spotifyDevice = null;
        _spotifyReady?.TrySetException(new SpotifyException("플레이어가 꺼졌습니다."));
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
        var ev = root.TryGetProperty("ev", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (ev != null && ev.StartsWith("sp-", StringComparison.Ordinal))
        {
            if (OnSpotifyPage(e.Source)) OnSpotifyMessage(ev, root);
            return;
        }
        switch (ev)
        {
            case "started":
                ApplyVolume();
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
          if (location.hostname === 'spotify.blueapex') return; // our Spotify page reports for itself (SpotifyPage)
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
            if (m.muted || (m.volume === 0 && window.__blueapexVolume !== 0)) return; // silent by the site, not by our volume bar
            if (typeof window.__blueapexVolume === 'number') m.volume = window.__blueapexVolume;
            active = m;
            post(state('started'));
          }, true);
          for (const n of ['pause', 'ended', 'loadedmetadata']) document.addEventListener(n, () => post(state('state')), true);
          setInterval(() => post(state('state')), 3000);
          window.__blueapex = {
            volume(v) { for (const m of document.querySelectorAll('video, audio')) m.volume = v; },
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

    // Spotify's Web Playback SDK: makes this browser a Spotify device called "BlueApex". The SDK asks for an
    // access token whenever it needs one (sp-token -> C# answers {cmd:'token'}), says when the device exists
    // (sp-ready, with its id) and what plays; the same "state" message and __blueapex buttons as ReportScript
    // let the dock show and drive it like any other playback here.
    private const string SpotifyPage = """
        <!doctype html><html><head><meta charset="utf-8"><title>BlueApex · Spotify</title></head>
        <body style="margin:0;height:100vh;background:#121214;color:#fff;font-family:'Segoe UI','Malgun Gothic',sans-serif;display:flex;align-items:center;justify-content:center">
        <div style="text-align:center;max-width:80vw">
          <img id="art" style="width:260px;height:260px;border-radius:12px;display:none">
          <h2 id="title">BlueApex 내장 Spotify 플레이어</h2>
          <div id="artist" style="opacity:.7">독 우클릭 메뉴에서 곡을 고르면 여기서 소리가 납니다.</div>
        </div>
        <script>
          const post = m => chrome.webview.postMessage(m);
          let waiting = []; // the SDK's callbacks that are waiting for a token
          chrome.webview.addEventListener('message', e => {
            if (!e.data || e.data.cmd !== 'token') return;
            const list = waiting; waiting = [];
            for (const give of list) give(e.data.token);
          });
          const report = s => {
            const t = s && s.track_window && s.track_window.current_track;
            const images = (t && t.album && t.album.images) || [];
            const big = images.slice().sort((a, b) => (b.width || 0) - (a.width || 0))[0];
            const art = big ? big.url : '';
            const artist = t ? (t.artists || []).map(a => a.name).join(', ') : '';
            post({ ev: 'state', hasMedia: !!t, playing: !!(t && !s.paused), title: t ? t.name : '', artist, art });
            if (!t) return;
            document.getElementById('title').textContent = t.name;
            document.getElementById('artist').textContent = artist;
            const img = document.getElementById('art');
            if (art) { img.src = art; img.style.display = 'inline'; }
          };
          window.onSpotifyWebPlaybackSDKReady = () => {
            const player = new Spotify.Player({ name: 'BlueApex', getOAuthToken: give => { waiting.push(give); post({ ev: 'sp-token' }); } });
            player.addListener('ready', e => post({ ev: 'sp-ready', device: e.device_id }));
            player.addListener('not_ready', () => post({ ev: 'sp-gone' }));
            for (const kind of ['initialization_error', 'authentication_error', 'account_error', 'playback_error'])
              player.addListener(kind, e => post({ ev: 'sp-error', kind, message: (e && e.message) || '' }));
            player.addListener('player_state_changed', report);
            window.__blueapex = { toggle: () => player.togglePlay(), next: () => player.nextTrack(), prev: () => player.previousTrack(),
                                  volume: v => player.setVolume(v) };
            if (typeof window.__blueapexVolume === 'number') player.setVolume(window.__blueapexVolume);
            setInterval(() => player.getCurrentState().then(report), 3000);
            player.connect().then(ok => { if (!ok) post({ ev: 'sp-error', kind: 'connect', message: 'connect failed' }); });
          };
        </script>
        <script src="https://sdk.scdn.co/spotify-player.js"></script>
        </body></html>
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
