using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace BlueApex.Games;

/// <summary>
/// GOG. Two sources:
///  - installed games register under HKLM\SOFTWARE\WOW6432Node\GOG.com\Games with their exe, so they
///    start directly (no launcher needed);
///  - the account's whole library is in GOG Galaxy's local database (galaxy-2.0.db, SQLite), which Galaxy
///    keeps up to date while it is signed in: titles, vertical covers and icons. Read-only; nothing is
///    written, and if Galaxy changes the format the owned list just stays empty.
/// A game that is owned but not installed opens on its page in Galaxy, where it can be installed.
/// </summary>
internal static class GogLibrary
{
    private const string Source = "gog";

    private static readonly string GalaxyDb = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");

    public static string Id(string gameId) => $"{GameCatalog.IdPrefix}{Source}:{gameId}";

    public static IEnumerable<GameInfo> Scan()
    {
        var owned = OwnedFromGalaxy();
        var installed = new HashSet<string>();
        using (var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
        {
            foreach (var gameId in games?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var key = games!.OpenSubKey(gameId);
                if (key == null) continue;
                var name = key.GetValue("gameName") as string;
                var exe = key.GetValue("exe") as string;
                if (name == null || exe == null || !File.Exists(exe)) continue;
                if (key.GetValue("dependsOn") is string depends && depends.Length > 0) continue; // DLC
                var args = key.GetValue("launchParam") as string;
                var dir = key.GetValue("workingDir") as string ?? key.GetValue("path") as string;
                owned.TryGetValue(gameId, out var art); // the installed game takes the library's artwork
                installed.Add(gameId);
                yield return new GameInfo(Id(gameId), name, Source, true, null, art?.Icon, exe)
                    { LaunchArgs = args, WorkingDir = dir, CoverUrl = art?.Cover };
            }
        }
        foreach (var (gameId, art) in owned)
            if (!installed.Contains(gameId))
                yield return new GameInfo(Id(gameId), art.Title, Source, false, null, art.Icon, null) { CoverUrl = art.Cover };
    }

    private sealed record Owned(string Title, string? Icon, string? Cover);

    // releaseKey "gog_1207658924" -> title and images, for the games the signed-in Galaxy user owns.
    private static Dictionary<string, Owned> OwnedFromGalaxy()
    {
        var result = new Dictionary<string, Owned>();
        if (!File.Exists(GalaxyDb)) return result;
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = GalaxyDb, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT lr.releaseKey,
                       (SELECT gp.value FROM GamePieces gp JOIN GamePieceTypes t ON t.id = gp.gamePieceTypeId
                         WHERE gp.releaseKey = lr.releaseKey AND t.type IN ('title', 'originalTitle')
                         ORDER BY t.type = 'title' DESC LIMIT 1),
                       (SELECT gp.value FROM GamePieces gp JOIN GamePieceTypes t ON t.id = gp.gamePieceTypeId
                         WHERE gp.releaseKey = lr.releaseKey AND t.type IN ('images', 'originalImages')
                         ORDER BY t.type = 'images' DESC LIMIT 1)
                FROM LibraryReleases lr
                WHERE lr.releaseKey LIKE 'gog\_%' ESCAPE '\'
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var gameId = reader.GetString(0)[4..];
                var title = Field(reader.IsDBNull(1) ? null : reader.GetString(1), "title");
                if (string.IsNullOrWhiteSpace(title)) continue;
                var images = reader.IsDBNull(2) ? null : reader.GetString(2);
                result[gameId] = new Owned(title, Field(images, "squareIcon"), Field(images, "verticalCover"));
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            Log.Write($"gog: Galaxy library unreadable: {ex.Message}");
        }
        return result;
    }

    // GamePieces values are small JSON objects: {"title":"..."} or {"squareIcon":"...", "verticalCover":"...", ...}.
    private static string? Field(string? json, string name)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Launch(GameInfo game)
    {
        if (!game.Installed || game.Exe == null)
        {
            // Owned but not installed: Galaxy's page for the game, with its install button.
            var releaseKey = "gog_" + game.Id[(game.Id.LastIndexOf(':') + 1)..];
            Process.Start(new ProcessStartInfo($"goggalaxy://openGameView/{releaseKey}") { UseShellExecute = true });
            return;
        }
        Process.Start(new ProcessStartInfo(game.Exe, game.LaunchArgs ?? "")
        {
            UseShellExecute = true,
            WorkingDirectory = game.WorkingDir ?? Path.GetDirectoryName(game.Exe) ?? "",
        });
    }
}
