using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BlueApex.Games;
using Microsoft.Web.WebView2.Core;

namespace BlueApex;

/// <summary>
/// The "boot screen": a startup movie played full screen right after logon, the way
/// SteamOS and Big Picture do. The movies are Steam's own files inside the user's Steam
/// install (steamui\movies, plus any Points Shop movie under config\uioverrides); nothing
/// is bundled. Played with WebView2 (WebM), on a black topmost window, closed when the
/// video ends, on Esc / click, or after a safety timeout.
/// </summary>
internal static class StartupMovie
{
    private static readonly string UserDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "browser-player");

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "cache", "movies");

    // Steam's community asset host; serves both the old "items/<app>/<hash>.webm" and the newer nested paths.
    private const string CommunityImages = "https://shared.steamstatic.com/community_assets/images/";

    /// <summary>A movie the user may pick: already on disk (<see cref="LocalPath"/>) or fetched from <see cref="Url"/> into <see cref="CacheFile"/> on first use.</summary>
    public sealed record Choice(string Label, string? LocalPath, string? Url, string? CacheFile)
    {
        public bool IsPointsShop => Url != null;
    }

    /// <summary>
    /// Movies available on this PC: Steam's built-in startup movies, plus every Points Shop
    /// startup movie the signed-in account owns (Steam lists those in localconfig.vdf; the
    /// client downloads only the one applied in Big Picture, the rest come from Steam's CDN).
    /// Empty when Steam is not installed.
    /// </summary>
    public static IReadOnlyList<Choice> Available()
    {
        var steam = SteamLibrary.InstallPath;
        if (steam == null) return Array.Empty<Choice>();
        var list = new List<Choice>();
        var builtIn = Path.Combine(steam, "steamui", "movies");
        if (Directory.Exists(builtIn))
            foreach (var file in Directory.EnumerateFiles(builtIn, "*startup*.webm").OrderBy(f => f))
                list.Add(new Choice(Label(Path.GetFileNameWithoutExtension(file)), file, null, null));
        try
        {
            var shop = PointsShopMovies(steam).ToList();
            list.AddRange(shop);
            Log.Write($"startup movies: {list.Count - shop.Count} built-in, {shop.Count} from the points shop ({shop.Count(c => c.LocalPath != null)} on disk)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Write($"points shop movies unreadable: {ex.Message}");
        }
        return list;
    }

    // localconfig.vdf holds the account's owned startup movies as a JSON string under "GetStartupMovies".
    private static IEnumerable<Choice> PointsShopMovies(string steam)
    {
        if (SteamLibrary.AccountId is not { } account) yield break;
        var localConfig = Path.Combine(steam, "userdata", account.ToString(), "config", "localconfig.vdf");
        if (!File.Exists(localConfig)) yield break;
        var json = FindValue(Vdf.Parse(File.ReadAllText(localConfig)), "GetStartupMovies");
        if (string.IsNullOrWhiteSpace(json)) yield break;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var downloaded = Path.Combine(steam, "config", "communityitemscache", "startupmovies");
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = item.TryGetProperty("communityitemid", out var idProp) ? idProp.GetString() : null;
            var movie = item.TryGetProperty("movie_webm", out var movieProp) ? movieProp.GetString() : null;
            if (id == null || string.IsNullOrEmpty(movie)) continue;
            var title = item.TryGetProperty("item_title", out var t) && t.GetString() is { Length: > 0 } tt ? tt
                      : item.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            var fileName = $"{id}_{Path.GetFileNameWithoutExtension(movie)}.webm";
            var steamCopy = Path.Combine(downloaded, fileName);
            var ourCopy = Path.Combine(CacheDir, fileName);
            var local = File.Exists(steamCopy) ? steamCopy : File.Exists(ourCopy) ? ourCopy : null;
            yield return new Choice("포인트 상점: " + title, local, CommunityImages + movie, ourCopy);
        }
    }

    private static string? FindValue(Vdf node, string key)
    {
        if (node[key] is { } found) return found;
        foreach (var child in node.Blocks)
            if (FindValue(child, key) is { } deeper) return deeper;
        return null;
    }

    /// <summary>The movie's file on disk, downloading a Points Shop movie once if needed. Null when the download fails.</summary>
    public static async Task<string?> EnsureLocalAsync(Choice choice)
    {
        if (choice.LocalPath != null) return choice.LocalPath;
        if (choice.Url == null || choice.CacheFile == null) return null;
        try
        {
            Directory.CreateDirectory(CacheDir);
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var bytes = await http.GetByteArrayAsync(choice.Url);
            await File.WriteAllBytesAsync(choice.CacheFile, bytes);
            Log.Write($"startup movie downloaded: {choice.Label} ({bytes.Length / 1024} KB)");
            return choice.CacheFile;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Write($"startup movie download failed: {choice.Url} ({ex.Message})");
            return null;
        }
    }

    private static string Label(string name) => name switch
    {
        "steam_os_startup" => "SteamOS 부팅",
        "steam_os_family_startup" => "SteamOS 부팅 (패밀리)",
        "deck_startup" => "Steam Deck 부팅",
        "oled_startup" => "Steam Deck OLED 부팅",
        "bigpicture_startup" => "빅픽처 시작",
        "startup_machine" => "Steam Machine 시작",
        _ => name,
    };

    /// <summary>Plays the file full screen on the primary monitor; <paramref name="done"/> runs when the window has closed.</summary>
    public static async Task PlayAsync(string file)
    {
        if (!File.Exists(file)) return;
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.PrimaryScreenWidth * 0, Top = 0,
            Width = SystemParameters.PrimaryScreenWidth,
            Height = SystemParameters.PrimaryScreenHeight,
            Cursor = System.Windows.Input.Cursors.None,
        };
        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.TrySetResult(true);
        window.KeyDown += (_, _) => window.Close();
        window.MouseDown += (_, _) => window.Close();
        window.Show();
        window.Activate();

        try
        {
            // Chromium refuses to autoplay with sound without a user gesture unless told otherwise.
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(null, UserDataDir, options);
            var hwnd = new WindowInteropHelper(window).Handle;
            var controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            if (!window.IsVisible) { controller.Close(); return; } // closed while the browser was starting
            var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale));
            controller.DefaultBackgroundColor = System.Drawing.Color.Black;
            var web = controller.CoreWebView2;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.SetVirtualHostNameToFolderMapping("movie.blueapex", Path.GetDirectoryName(file)!, CoreWebView2HostResourceAccessKind.DenyCors);
            web.WebMessageReceived += (_, _) => window.Dispatcher.BeginInvoke(window.Close);
            var name = Uri.EscapeDataString(Path.GetFileName(file));
            web.NavigateToString($$"""
                <!doctype html><html><body style="margin:0;background:#000;overflow:hidden">
                <video id="v" autoplay playsinline src="https://movie.blueapex/{{name}}"
                       style="position:fixed;inset:0;width:100vw;height:100vh;object-fit:contain;background:#000"></video>
                <script>
                  const v = document.getElementById('v'), done = () => window.chrome.webview.postMessage('done');
                  v.onended = done; v.onerror = done;
                  v.play().catch(done);
                  addEventListener('keydown', done); addEventListener('mousedown', done);
                </script></body></html>
                """);
            // Whatever happens, the desktop is back within half a minute.
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => window.Dispatcher.BeginInvoke(window.Close));
            await closed.Task;
            controller.Close();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or IOException)
        {
            Log.Write($"startup movie failed: {ex.Message}");
            window.Close();
        }
    }
}
