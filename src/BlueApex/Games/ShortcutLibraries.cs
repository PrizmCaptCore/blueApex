using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BlueApex.Games;

/// <summary>
/// Launchers whose games are best found through the shortcuts they create: STOVE (sgup://...) and
/// Google Play Games for PC (googleplaygames://...), Neowiz's starter (browndust2:...). When such a launcher installs a game it puts a
/// shortcut in the Start menu (and usually on the desktop) whose target is its own protocol link;
/// those shortcuts are read here and started as they are, so the link format never has to be guessed
/// and keeps working when the launcher changes it.
/// </summary>
internal static class ShortcutLibraries
{
    private sealed record Launcher(string Source, string Scheme);

    private static readonly Launcher[] Launchers =
    {
        new("stove", "sgup://"),
        new("gplay", "googleplaygames://"),
        // BrownDust2: its shortcut says "(GPG)", but the browndust2: link is handled by Neowiz's own starter
        // (built on their Pmang starter), and Google Play Games does not list it as installed.
        new("neowiz", "browndust2:"),
    };

    private static IEnumerable<string> Folders => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    };

    public static IEnumerable<GameInfo> Scan()
    {
        var seen = new HashSet<string>();
        foreach (var folder in Folders.Where(Directory.Exists))
        {
            // Skip folders we may not read (one locked folder must not lose the rest); keep hidden files (the drawer hides desktop shortcuts).
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".lnk" or ".url")) continue;
                var link = ext == ".url" ? UrlOf(file) : ShortcutCommand(file);
                if (link == null) continue;
                var launcher = Launchers.FirstOrDefault(l => link.Contains(l.Scheme, StringComparison.OrdinalIgnoreCase));
                if (launcher == null) continue;
                var url = link[link.IndexOf(launcher.Scheme, StringComparison.OrdinalIgnoreCase)..].Trim('"', ' ');
                if (url.Length <= launcher.Scheme.Length) continue; // the launcher's own shortcut, not a game
                // The same game often has a Start-menu and a desktop shortcut: one entry per link.
                var id = $"{GameCatalog.IdPrefix}{launcher.Source}:{Key(url)}";
                if (!seen.Add(id)) continue;
                yield return new GameInfo(id, Path.GetFileNameWithoutExtension(file), launcher.Source, true, null, null, file)
                    { IconFile = ext == ".url" ? UrlIconOf(file) : null };
            }
        }
    }

    /// <summary>Starts the game the way its own shortcut does.</summary>
    public static void Launch(GameInfo game)
    {
        if (game.Exe == null || !File.Exists(game.Exe)) throw new InvalidOperationException("게임 바로가기가 없어졌습니다. 게임 카드를 다시 읽어 주세요.");
        Process.Start(new ProcessStartInfo(game.Exe) { UseShellExecute = true });
    }

    // A stable key for the link: the package id when it carries one (googleplaygames://launch/?id=com.x), else a hash.
    private static string Key(string url)
    {
        var marker = url.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var value = url[(marker + 3)..].Split('&')[0];
            if (value.Length > 0) return Uri.UnescapeDataString(value);
        }
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..12].ToLowerInvariant();
    }

    private static string? UrlOf(string urlFile) => UrlField(urlFile, "URL=");

    // The picture a .url names. Icons are loaded on worker threads, and there the shell answers a .url
    // with a blank-page icon instead of the one it names, so the drawer reads the icon file directly.
    private static string? UrlIconOf(string urlFile)
    {
        var icon = UrlField(urlFile, "IconFile=")?.Trim('"');
        return icon != null && File.Exists(icon) ? icon : null;
    }

    private static string? UrlField(string urlFile, string prefix)
    {
        try
        {
            return File.ReadLines(urlFile).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A .lnk's target plus its arguments, where launchers put their protocol link.
    private static string? ShortcutCommand(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            var shortcut = shell.CreateShortcut(lnk);
            var command = $"{(string)shortcut.TargetPath} {(string)shortcut.Arguments}".Trim();
            return command.Length > 0 ? command : LinkInsideShortcut(lnk);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return LinkInsideShortcut(lnk);
        }
    }

    // Google Play Games writes .lnk files that point at a URL instead of a file: the target is stored
    // as a shell item, so TargetPath comes back empty. The link is still in the file as plain text
    // (UTF-16 or ASCII), so look for one of our schemes there and read it up to the first non-URL character.
    private static string? LinkInsideShortcut(string lnk)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(lnk);
            if (info.Length > 64 * 1024) return null; // shortcuts are small; anything larger is not one
            bytes = File.ReadAllBytes(lnk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var text in new[] { Encoding.Unicode.GetString(bytes), Encoding.ASCII.GetString(bytes) })
            foreach (var launcher in Launchers)
            {
                var start = text.IndexOf(launcher.Scheme, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                var end = start;
                while (end < text.Length && text[end] > ' ' && text[end] < 0x7F && text[end] != '"') end++;
                return text[start..end];
            }
        return null;
    }
}
