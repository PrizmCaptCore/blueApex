using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace BlueApex.Games;

/// <summary>
/// Epic Games. Installed games are the launcher's manifest files; the account's whole
/// library is the launcher's own catalog cache (catcache.bin, base64 JSON), which it
/// refreshes whenever it is opened and signed in. So "linking" Epic is simply being
/// signed in to the Epic Games Launcher; no credentials are handled here.
/// </summary>
internal static class EpicLibrary
{
    private const string Source = "epic";
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data");

    /// <summary>The launcher's executable (for shortcut icons), from its protocol registration.</summary>
    public static string? ExePath
    {
        get
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"com.epicgames.launcher\shell\open\command");
            var command = key?.GetValue(null) as string;
            if (command == null) return null;
            var exe = command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];
            return File.Exists(exe) ? exe : null;
        }
    }

    public static bool IsLauncherInstalled => ExePath != null;

    public static string Id(string appName) => $"{GameCatalog.IdPrefix}{Source}:{appName}";
    private static string AppName(GameInfo game) => game.Id[(GameCatalog.IdPrefix.Length + Source.Length + 1)..];

    public static string LaunchUri(GameInfo game) =>
        $"com.epicgames.launcher://apps/{AppName(game)}?action={(game.Installed ? "launch&silent=true" : "install")}";

    public static IEnumerable<GameInfo> Scan()
    {
        if (!Directory.Exists(DataDir)) yield break;

        var installed = new Dictionary<string, GameInfo>();
        var manifests = Path.Combine(DataDir, "Manifests");
        if (Directory.Exists(manifests))
        {
            foreach (var file in Directory.GetFiles(manifests, "*.item"))
            {
                using var doc = GameCatalog.ReadJson(file);
                if (doc == null) continue;
                var m = doc.RootElement;
                var appName = Str(m, "AppName");
                var name = Str(m, "DisplayName");
                if (appName == null || name == null) continue;
                // DLC manifests name their main game; skip those.
                if (Str(m, "MainGameAppName") is { } main && main != appName) continue;
                if (m.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) continue;
                var exe = Str(m, "InstallLocation") is { } dir && Str(m, "LaunchExecutable") is { } launch
                    ? Path.Combine(dir, launch) : null;
                installed[appName] = new GameInfo(Id(appName), name, Source, true, null, null, exe);
            }
        }

        // The library: every catalog item the launcher has cached for this account.
        var owned = new List<GameInfo>();
        var catalog = Path.Combine(DataDir, "Catalog", "catcache.bin");
        if (File.Exists(catalog))
        {
            var json = Convert.FromBase64String(File.ReadAllText(catalog));
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!IsGame(item)) continue;
                var title = Str(item, "title");
                if (title == null || !item.TryGetProperty("releaseInfo", out var releases)) continue;
                var appName = releases.EnumerateArray().Select(r => Str(r, "appId")).FirstOrDefault(a => a != null);
                if (appName == null) continue;
                var image = Image(item);
                if (installed.TryGetValue(appName, out var inst))
                    installed[appName] = inst with { ImageUrl = image }; // the installed entry gains its artwork
                else
                    owned.Add(new GameInfo(Id(appName), title, Source, false, null, image, null));
            }
        }

        foreach (var g in installed.Values) yield return g;
        foreach (var g in owned) yield return g;
    }

    // "games" category and not an add-on (DLC, soundtracks...), with a Windows release.
    private static bool IsGame(JsonElement item)
    {
        if (!item.TryGetProperty("categories", out var categories)) return false;
        var paths = categories.EnumerateArray().Select(c => Str(c, "path")).ToList();
        if (!paths.Contains("games") || paths.Contains("addons")) return false;
        // Every item carries mainGameItem; only add-ons fill it in.
        if (item.TryGetProperty("mainGameItem", out var main) && main.ValueKind == JsonValueKind.Object && Str(main, "id") is { Length: > 0 }) return false;
        return !item.TryGetProperty("releaseInfo", out var releases) || releases.EnumerateArray().Any(r =>
            !r.TryGetProperty("platform", out var p) || p.EnumerateArray().Any(x => x.GetString() == "Windows"));
    }

    // Portrait box art, downsized by Epic's CDN; the landscape box or any thumbnail otherwise.
    private static string? Image(JsonElement item)
    {
        if (!item.TryGetProperty("keyImages", out var images)) return null;
        var list = images.EnumerateArray().Select(i => (Type: Str(i, "type"), Url: Str(i, "url"))).Where(i => i.Url != null).ToList();
        var pick = list.FirstOrDefault(i => i.Type == "DieselGameBoxTall").Url
                   ?? list.FirstOrDefault(i => i.Type == "Thumbnail").Url
                   ?? list.FirstOrDefault(i => i.Type == "DieselGameBox").Url
                   ?? list.FirstOrDefault().Url;
        return pick == null ? null : pick + (pick.Contains('?') ? "&" : "?") + "resize=1&w=160&h=160";
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
