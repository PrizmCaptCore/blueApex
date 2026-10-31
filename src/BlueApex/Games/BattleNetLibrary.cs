using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BlueApex.Games;

/// <summary>
/// Battle.net (Blizzard). Installed games are the Blizzard entries in Windows' installed-programs list:
/// their uninstall command carries the product code ("--uid=s2") and their icon is the game's exe.
/// Starting one goes through Battle.net ("Battle.net.exe --exec=launch S2"), which handles login and updates.
/// No account data is read.
/// </summary>
internal static class BattleNetLibrary
{
    private const string Source = "battlenet";

    // Product uid (from the uninstall entry) -> the code Battle.net's "launch" understands.
    private static readonly Dictionary<string, string> LaunchCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["s1"] = "S1", ["s2"] = "S2", ["w3"] = "W3", ["wow"] = "WoW", ["wow_classic"] = "WoWC",
        ["prometheus"] = "Pro", ["hs_beta"] = "WTCG", ["heroes"] = "Hero", ["diablo3"] = "D3", ["osi"] = "OSI",
        ["fenris"] = "Fen", ["rtro"] = "RTRO", ["wlby"] = "WLBY", ["viper"] = "VIPR", ["odin"] = "ODIN",
        ["lazarus"] = "LAZR", ["zeus"] = "ZEUS", ["fore"] = "FORE", ["aqua"] = "AQUA", ["auks"] = "AUKS",
    };

    public static string Id(string uid) => $"{GameCatalog.IdPrefix}{Source}:{uid}";

    /// <summary>Battle.net.exe, from its battlenet:// protocol registration; null if Battle.net is not installed.</summary>
    public static string? ExePath
    {
        get
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"battlenet\shell\open\command");
            var command = key?.GetValue(null) as string;
            var exe = command == null ? null : command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];
            return exe != null && File.Exists(exe) ? exe : null;
        }
    }

    public static IEnumerable<GameInfo> Scan()
    {
        if (ExePath == null) yield break;
        foreach (var entry in InstalledPrograms.Where(p => p.Publisher == "Blizzard Entertainment"))
        {
            var uid = Regex.Match(entry.UninstallString ?? "", @"--uid=([\w.]+)").Groups[1].Value;
            if (uid.Length == 0 || uid.Equals("battle.net", StringComparison.OrdinalIgnoreCase)) continue; // the launcher itself
            var exe = entry.Icon != null && File.Exists(entry.Icon) ? entry.Icon : null;
            yield return new GameInfo(Id(uid), entry.Name, Source, true, null, null, exe);
        }
    }

    public static void Launch(GameInfo game)
    {
        var uid = game.Id[(game.Id.LastIndexOf(':') + 1)..];
        var exe = ExePath ?? throw new InvalidOperationException("Battle.net이 설치돼 있지 않습니다");
        // A known code starts the game directly; otherwise Battle.net opens on its library.
        var args = LaunchCodes.TryGetValue(uid, out var code) ? $"--exec=\"launch {code}\"" : "";
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
    }

    /// <summary>The desktop-shortcut form: the protocol link opens the game's page in Battle.net.</summary>
    public static string Uri(GameInfo game)
    {
        var uid = game.Id[(game.Id.LastIndexOf(':') + 1)..];
        return LaunchCodes.TryGetValue(uid, out var code) ? $"battlenet://{code}" : "battlenet://";
    }

    internal sealed record InstalledProgram(string Name, string? Publisher, string? UninstallString, string? Icon);

    /// <summary>Windows' installed-programs list (both registry views, machine and user).</summary>
    internal static IEnumerable<InstalledProgram> InstalledPrograms
    {
        get
        {
            var roots = new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };
            foreach (var (hive, path) in roots)
            {
                using var uninstall = hive.OpenSubKey(path);
                if (uninstall == null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string display) continue;
                    var icon = (key.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                    yield return new InstalledProgram(display, key.GetValue("Publisher") as string, key.GetValue("UninstallString") as string, icon);
                }
            }
        }
    }
}
