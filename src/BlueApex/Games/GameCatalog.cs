using System.Diagnostics;
using System.IO;
using BlueApex.Desktop;

namespace BlueApex.Games;

/// <summary>One game as a launcher knows it. Ids are "game:&lt;source&gt;:&lt;key&gt;" (see <see cref="GameCatalog"/>).</summary>
/// <param name="Installed">False for a game the account owns that is not on this PC; launching then opens the install page.</param>
/// <param name="ImageFile">A local picture to use as the tile (jpg/png), if the launcher cached one.</param>
/// <param name="ImageUrl">Where to download the picture from when there is no local one; cached under %AppData%\BlueApex\cache\games.</param>
/// <param name="Exe">The game's executable, used for its icon and (GOG) to start it.</param>
internal sealed record GameInfo(string Id, string Name, string Source, bool Installed, string? ImageFile, string? ImageUrl, string? Exe)
{
    /// <summary>Command-line arguments when the game is started directly (GOG).</summary>
    public string? LaunchArgs { get; init; }
    public string? WorkingDir { get; init; }
}

/// <summary>
/// Games from the launchers installed on this PC: Steam, Epic Games, GOG. Everything
/// is read from what the launchers keep locally, so no password ever passes through
/// this app. Steam games the account owns but has not installed come through a sign-in
/// in an embedded browser (see <see cref="SteamSession"/>), Playnite-style.
///
/// Ids look like <c>game:steam:108600</c>, <c>game:epic:Boga</c>, <c>game:gog:1207658924</c>
/// and are stable across rescans, so zones can hold them like app ids.
/// </summary>
internal static class GameCatalog
{
    public const string IdPrefix = "game:";

    public static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "cache", "games");

    public static bool IsGameId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>"steam", "epic" or "gog".</summary>
    public static string SourceOf(string id)
    {
        var rest = id[IdPrefix.Length..];
        var colon = rest.IndexOf(':');
        return colon < 0 ? rest : rest[..colon];
    }

    public static string SourceLabel(string source) => source switch
    {
        "steam" => "Steam",
        "epic" => "Epic Games",
        "gog" => "GOG",
        _ => source,
    };

    // The latest scan, so icon loading (on worker threads) can look a game up by id.
    private static Dictionary<string, GameInfo> _byId = new();

    public static GameInfo? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Reads every launcher. Slow-ish (file parsing, a few hundred JSON entries), so call it
    /// off the UI thread. Never throws: a launcher that cannot be read is logged and skipped.
    /// </summary>
    /// <param name="steamOwned">Include Steam games the signed-in account owns but has not installed.</param>
    public static List<GameInfo> Scan(bool steamOwned)
    {
        var games = new List<GameInfo>();
        Collect(games, "Steam", () => SteamLibrary.Scan(steamOwned));
        Collect(games, "Epic", EpicLibrary.Scan);
        Collect(games, "GOG", GogLibrary.Scan);
        // The same game can be seen twice (installed + owned list); the installed one wins.
        var merged = games.GroupBy(g => g.Id).Select(g => g.OrderByDescending(x => x.Installed).First()).ToList();
        _byId = merged.ToDictionary(g => g.Id);
        return merged;
    }

    private static void Collect(List<GameInfo> into, string launcher, Func<IEnumerable<GameInfo>> scan)
    {
        try
        {
            into.AddRange(scan());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            Log.Write($"{launcher} library scan failed: {ex.Message}");
        }
    }

    /// <summary>Starts the game through its launcher; for a game that is not installed, opens the launcher's install page.</summary>
    public static void Launch(GameInfo game)
    {
        switch (game.Source)
        {
            case "steam":
                Open(SteamLibrary.LaunchUri(game));
                break;
            case "epic":
                Open(EpicLibrary.LaunchUri(game));
                break;
            case "gog":
                GogLibrary.Launch(game);
                break;
        }
    }

    /// <summary>What a desktop .url shortcut for the game should point at, and the icon to show on it.</summary>
    public static (string Url, string? IconFile) ShortcutTarget(GameInfo game) => game.Source switch
    {
        "steam" => (SteamLibrary.LaunchUri(game), SteamLibrary.ExePath),
        "epic" => (EpicLibrary.LaunchUri(game), EpicLibrary.ExePath),
        _ => ("file:///" + (game.Exe ?? "").Replace('\\', '/'), game.Exe),
    };

    private static void Open(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    /// <summary>Reads a JSON file into a document; null if it is missing or unreadable.</summary>
    internal static System.Text.Json.JsonDocument? ReadJson(string path)
    {
        try
        {
            return System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
