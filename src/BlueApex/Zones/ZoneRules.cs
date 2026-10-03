using System.IO;
using System.Text.RegularExpressions;
using BlueApex.Desktop;

namespace BlueApex.Zones;

/// <summary>Matches desktop icons against zone patterns (see <see cref="Zone.Patterns"/>).</summary>
internal static class ZoneRules
{
    /// <summary>The first zone whose patterns match the icon, or null.</summary>
    public static Zone? Target(IEnumerable<Zone> zones, DesktopIcon icon)
    {
        var fileName = icon.Id.StartsWith("::", StringComparison.Ordinal) ? icon.Name : Path.GetFileName(icon.Id);
        var isFolder = !icon.Id.StartsWith("::", StringComparison.Ordinal) && Directory.Exists(icon.Id);
        return zones.FirstOrDefault(z => z.Patterns.Any(p => Matches(p, fileName, isFolder)));
    }

    public static bool Matches(string pattern, string fileName, bool isFolder)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;

        if (pattern.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
        {
            if (!isFolder) return false;
            pattern = pattern["folder:".Length..];
        }
        else if (pattern.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (isFolder) return false;
            pattern = pattern["file:".Length..];
        }

        var regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
