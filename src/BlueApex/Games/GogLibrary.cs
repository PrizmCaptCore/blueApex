using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BlueApex.Games;

/// <summary>
/// GOG. Installed games register themselves under HKLM\SOFTWARE\WOW6432Node\GOG.com\Games
/// with their executable, so they can be started directly (no launcher needed).
/// The account's uninstalled games are not read: GOG Galaxy keeps that list only
/// behind a login.
/// </summary>
internal static class GogLibrary
{
    private const string Source = "gog";

    public static string Id(string gameId) => $"{GameCatalog.IdPrefix}{Source}:{gameId}";

    public static IEnumerable<GameInfo> Scan()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games == null) yield break;
        foreach (var gameId in games.GetSubKeyNames())
        {
            using var key = games.OpenSubKey(gameId);
            if (key == null) continue;
            var name = key.GetValue("gameName") as string;
            var exe = key.GetValue("exe") as string;
            if (name == null || exe == null || !File.Exists(exe)) continue;
            if (key.GetValue("dependsOn") is string depends && depends.Length > 0) continue; // DLC
            var args = key.GetValue("launchParam") as string;
            var dir = key.GetValue("workingDir") as string ?? key.GetValue("path") as string;
            yield return new GameInfo(Id(gameId), name, Source, true, null, null, exe) { LaunchArgs = args, WorkingDir = dir };
        }
    }

    public static void Launch(GameInfo game)
    {
        if (game.Exe == null) return;
        Process.Start(new ProcessStartInfo(game.Exe, game.LaunchArgs ?? "")
        {
            UseShellExecute = true,
            WorkingDirectory = game.WorkingDir ?? Path.GetDirectoryName(game.Exe) ?? "",
        });
    }
}
