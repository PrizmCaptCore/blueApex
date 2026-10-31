using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BlueApex.Games;

/// <summary>
/// HoYoPlay (HoYoverse: Genshin Impact, Honkai: Star Rail, Zenless Zone Zero, ...). The launcher keeps
/// one registry key per installed game under HKCU\Software\Cognosphere\HYP\1_0\&lt;game code&gt;
/// ("hk4e_global" is Genshin) with its install folder; the Chinese edition uses miHoYo instead of
/// Cognosphere. The game's display name comes from Windows' installed-programs list, whose entry
/// uninstalls it with "launcher.exe --uninstall_game=&lt;game code&gt;".
/// Starting one runs "launcher.exe --game=&lt;game code&gt;", exactly what HoYoPlay's own Start-menu
/// shortcut does, so the launcher still handles updates and login. No account data is read.
/// </summary>
internal static class HoYoPlayLibrary
{
    private const string Source = "hoyoplay";

    private static readonly string[] Roots = { @"Software\Cognosphere\HYP", @"Software\miHoYo\HYP" };

    // Used only when the installed-programs list has no name for the game.
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hk4e"] = "원신", ["hkrpg"] = "붕괴: 스타레일", ["nap"] = "젠레스 존 제로", ["bh3"] = "붕괴3rd",
    };

    public static string Id(string gameCode) => $"{GameCatalog.IdPrefix}{Source}:{gameCode}";

    public static IEnumerable<GameInfo> Scan()
    {
        var names = NamesFromInstalledPrograms();
        foreach (var root in Roots)
        {
            using var hyp = Registry.CurrentUser.OpenSubKey(root);
            if (hyp == null) continue;
            // "1_0" is the launcher's data version; read every one in case a later launcher adds another.
            foreach (var version in hyp.GetSubKeyNames())
            {
                using var launcherKey = hyp.OpenSubKey(version);
                var launcher = LauncherExe(launcherKey?.GetValue("InstallPath") as string);
                if (launcherKey == null || launcher == null) continue;
                foreach (var code in launcherKey.GetSubKeyNames())
                {
                    using var game = launcherKey.OpenSubKey(code);
                    var folder = game?.GetValue("GameInstallPath") as string;
                    if (folder == null || !Directory.Exists(folder)) continue; // registered but not (or no longer) installed
                    var name = names.GetValueOrDefault(code) ?? KnownNames.GetValueOrDefault(code.Split('_')[0]) ?? code;
                    var exe = GameExe(folder) ?? game!.GetValue("GameIconPath") as string;
                    yield return new GameInfo(Id(code), name, Source, true, null, null, exe);
                }
            }
        }
    }

    public static void Launch(GameInfo game)
    {
        var code = game.Id[(game.Id.LastIndexOf(':') + 1)..];
        var launcher = LauncherFor(code) ?? throw new InvalidOperationException("HoYoPlay가 설치돼 있지 않습니다");
        Process.Start(new ProcessStartInfo(launcher, $"--game={code}") { UseShellExecute = true });
    }

    // The launcher.exe that has this game registered.
    private static string? LauncherFor(string code)
    {
        foreach (var root in Roots)
        {
            using var hyp = Registry.CurrentUser.OpenSubKey(root);
            foreach (var version in hyp?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var launcherKey = hyp!.OpenSubKey(version);
                if (launcherKey?.GetSubKeyNames().Contains(code, StringComparer.OrdinalIgnoreCase) != true) continue;
                if (LauncherExe(launcherKey.GetValue("InstallPath") as string) is { } exe) return exe;
            }
        }
        return null;
    }

    private static string? LauncherExe(string? installPath)
    {
        if (string.IsNullOrEmpty(installPath)) return null;
        var exe = Path.Combine(installPath, "launcher.exe");
        return File.Exists(exe) ? exe : null;
    }

    // The game's own exe sits at the top of its folder (GenshinImpact.exe, StarRail.exe, ...), next to
    // small helpers such as UnityCrashHandler64.exe; the game is by far the largest.
    private static string? GameExe(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*.exe")
                .Where(f => !f.Name.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Length).FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // game code -> name as Windows lists it ("hk4e_global" -> "원신").
    private static Dictionary<string, string> NamesFromInstalledPrograms()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in BattleNetLibrary.InstalledPrograms)
        {
            var code = Regex.Match(program.UninstallString ?? "", @"--uninstall_game=([\w.]+)").Groups[1].Value;
            if (code.Length > 0) names[code] = program.Name;
        }
        return names;
    }
}
