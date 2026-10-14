using System.IO;

namespace BlueApex.Desktop;

/// <summary>Writes desktop shortcuts for games ("바탕화면에 꺼내기" on a game tile).</summary>
internal static class ShortcutWriter
{
    /// <summary>
    /// Creates "&lt;name&gt;.url" in the folder: an Internet-shortcut file that opens a URL such as
    /// <c>steam://rungameid/...</c>, showing <paramref name="iconFile"/>'s icon (an .exe/.ico/.dll).
    /// A taken name gets " (2)", " (3)"...
    /// </summary>
    public static string CreateUrlShortcut(string name, string url, string? iconFile, string folder)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var path = Path.Combine(folder, name + ".url");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} ({n}).url");
        var lines = new List<string> { "[InternetShortcut]", "URL=" + url };
        if (iconFile != null && File.Exists(iconFile))
        {
            lines.Add("IconFile=" + iconFile);
            lines.Add("IconIndex=0");
        }
        File.WriteAllLines(path, lines);
        return path;
    }
}
