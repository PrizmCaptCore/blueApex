namespace BlueApex.Desktop;

/// <summary>The drawer's name order, shared by zone sorting and the games card.</summary>
internal static class NameOrder
{
    /// <summary>
    /// Name order. <paramref name="latinFirst"/> puts names starting with a Latin letter or
    /// digit (A→Z) before everything else (가나다...); otherwise the culture's own order,
    /// which on a Korean system lists Hangul first.
    /// </summary>
    public static List<DesktopIcon> Sort(IEnumerable<DesktopIcon> icons, bool latinFirst)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        return latinFirst
            ? icons.OrderBy(a => IsLatinOrDigit(a.Name) ? 0 : 1).ThenBy(a => a.Name, byName).ToList()
            : icons.OrderBy(a => a.Name, byName).ToList();
    }

    private static bool IsLatinOrDigit(string name) =>
        name.Length > 0 && name[0] < 0x0250 && char.IsLetterOrDigit(name[0]);
}
