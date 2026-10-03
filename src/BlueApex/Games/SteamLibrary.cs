using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace BlueApex.Games;

/// <summary>
/// Steam. Installed games come from the library folders' appmanifest files; games
/// the account owns but has not installed come from the Steam Web API, which needs
/// the user's own API key (steamcommunity.com/dev/apikey) and a profile whose game
/// list is public. The SteamID is read from the registry (the logged-in account).
/// Artwork is taken from Steam's own cache next to the client.
/// </summary>
internal static class SteamLibrary
{
    private const string Source = "steam";
    private const long SteamId64Base = 76561197960265728;
    private static readonly string OwnedCache = Path.Combine(GameCatalog.CacheDir, "steam-owned.json");

    /// <summary>The Steam client folder from the registry, or null if Steam is not installed.</summary>
    public static string? InstallPath
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            return path != null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
        }
    }

    public static string? ExePath => InstallPath is { } p ? Path.Combine(p, "steam.exe") : null;

    /// <summary>The 64-bit SteamID of the account last logged in to the client, or null.</summary>
    public static long? SteamId64
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("ActiveUser") is int active && active != 0)
                return SteamId64Base + (uint)active;
            // Not running: take the one userdata folder if there is exactly one.
            if (InstallPath is { } path && Directory.Exists(Path.Combine(path, "userdata")))
            {
                var users = Directory.GetDirectories(Path.Combine(path, "userdata"))
                    .Select(Path.GetFileName).Where(n => uint.TryParse(n, out _)).ToList();
                if (users.Count == 1) return SteamId64Base + uint.Parse(users[0]!);
            }
            return null;
        }
    }

    public static string Id(string appId) => $"{GameCatalog.IdPrefix}{Source}:{appId}";
    private static string AppId(GameInfo game) => game.Id[(game.Id.LastIndexOf(':') + 1)..];

    public static string LaunchUri(GameInfo game) =>
        game.Installed ? $"steam://rungameid/{AppId(game)}" : $"steam://install/{AppId(game)}";

    public static IEnumerable<GameInfo> Scan(string? apiKey)
    {
        var steam = InstallPath;
        if (steam == null) yield break;

        var installed = new HashSet<string>();
        foreach (var library in LibraryFolders(steam))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                var acf = Vdf.Parse(File.ReadAllText(manifest)).Block("AppState");
                var appId = acf?["appid"];
                var name = acf?["name"];
                if (appId == null || name == null || IsTool(appId, name)) continue;
                installed.Add(appId);
                yield return new GameInfo(Id(appId), name, Source, true, LocalImage(steam, appId, null), null, null);
            }
        }

        // Owned-but-not-installed, from the last Web API fetch (refreshed by LinkAsync / rescan).
        if (apiKey == null || !File.Exists(OwnedCache)) yield break;
        using var doc = GameCatalog.ReadJson(OwnedCache);
        if (doc == null) yield break;
        foreach (var game in doc.RootElement.EnumerateArray())
        {
            var appId = game.GetProperty("appid").ToString();
            var name = game.GetProperty("name").GetString() ?? appId;
            var iconHash = game.TryGetProperty("icon", out var h) ? h.GetString() : null;
            if (installed.Contains(appId) || IsTool(appId, name)) continue;
            var url = iconHash is { Length: > 0 }
                ? $"https://media.steampowered.com/steamcommunity/public/images/apps/{appId}/{iconHash}.jpg"
                : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
            yield return new GameInfo(Id(appId), name, Source, false, LocalImage(steam, appId, iconHash), url, null);
        }
    }

    /// <summary>
    /// Fetches the account's owned games with the Web API and caches them for <see cref="Scan"/>.
    /// Returns the number of games, or throws with a message fit for the user.
    /// </summary>
    public static async Task<int> FetchOwnedAsync(string apiKey, long steamId)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(apiKey)}&steamid={steamId}&include_appinfo=1&include_played_free_games=1&format=json";
        using var response = await http.GetAsync(url);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Steam이 API 키를 거부했습니다. 키를 다시 확인해 주세요.");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = doc.RootElement.GetProperty("response");
        if (!body.TryGetProperty("games", out var games))
            throw new InvalidOperationException("게임 목록이 비어 있습니다. Steam 프로필의 '게임 세부 정보'가 공개로 되어 있어야 합니다.");

        // Keep only what Scan needs: appid, name, icon hash.
        var slim = games.EnumerateArray().Select(g => new
        {
            appid = g.GetProperty("appid").GetInt64(),
            name = g.TryGetProperty("name", out var n) ? n.GetString() : null,
            icon = g.TryGetProperty("img_icon_url", out var i) ? i.GetString() : null,
        }).ToList();
        Directory.CreateDirectory(GameCatalog.CacheDir);
        await File.WriteAllTextAsync(OwnedCache, JsonSerializer.Serialize(slim));
        Log.Write($"steam: {slim.Count} owned games fetched");
        return slim.Count;
    }

    /// <summary>How old the cached owned-games list is; infinite when there is none.</summary>
    public static TimeSpan OwnedCacheAge =>
        File.Exists(OwnedCache) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(OwnedCache) : TimeSpan.MaxValue;

    public static void ForgetOwned()
    {
        try { File.Delete(OwnedCache); } catch (IOException) { }
    }

    // Every library folder, from the main one's libraryfolders.vdf.
    private static IEnumerable<string> LibraryFolders(string steam)
    {
        var file = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file)) return new[] { steam };
        var root = Vdf.Parse(File.ReadAllText(file)).Block("libraryfolders");
        var folders = root?.Blocks.Select(b => b["path"]).OfType<string>().Where(Directory.Exists).ToList() ?? new();
        if (folders.Count == 0) folders.Add(steam);
        return folders;
    }

    // Steam's artwork cache: appcache/librarycache/<appid>/ holds header.jpg, library_600x900.jpg
    // and the small icon under its hash name. Prefer the icon, then the header.
    private static string? LocalImage(string steam, string appId, string? iconHash)
    {
        var dir = Path.Combine(steam, "appcache", "librarycache", appId);
        if (!Directory.Exists(dir)) return null;
        if (iconHash != null && File.Exists(Path.Combine(dir, iconHash + ".jpg")))
            return Path.Combine(dir, iconHash + ".jpg");
        var hashNamed = Directory.GetFiles(dir, "*.jpg").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Length == 40);
        if (hashNamed != null) return hashNamed;
        var header = Path.Combine(dir, "header.jpg");
        return File.Exists(header) ? header : null;
    }

    // Redistributables, runtimes and the like show up as "apps" but are not games.
    private static bool IsTool(string appId, string name) =>
        appId == "228980" || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase);
}
