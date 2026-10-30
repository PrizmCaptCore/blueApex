using System.Diagnostics;
using System.IO;

namespace BlueApex.Drawer;

/// <summary>
/// What a dock favourite id means, shared by the home-screen dock and the drawer's favourites
/// column. An id is a desktop item path, an exe path, a game id ("game:..."), or "aumid:&lt;AppUserModelID&gt;"
/// for a Store app.
/// </summary>
internal static class DockApps
{
    public static bool IsStoreApp(string id) => id.StartsWith("aumid:", StringComparison.Ordinal);

    /// <summary>The name to show: the drawer's name for the item, else the Store app id or file name.</summary>
    public static string NameOf(DrawerManager drawer, string id)
    {
        if (drawer.Find(id) is { } icon) return icon.Name;
        if (IsStoreApp(id)) return id[6..];
        return Path.GetFileNameWithoutExtension(id);
    }

    /// <summary>What <see cref="IconLoader"/> should draw for the id.</summary>
    public static string IconSource(string id) => IsStoreApp(id) ? "shell:AppsFolder\\" + id[6..] : id;

    /// <summary>Starts the favourite (a new instance; switching to a running window is the dock's job).</summary>
    public static void Launch(DrawerManager drawer, string id)
    {
        try
        {
            if (IsStoreApp(id))
                Process.Start("explorer.exe", "shell:AppsFolder\\" + id[6..]);
            else if (drawer.Find(id) is { } icon)
                DrawerManager.Launch(icon);
            else
                Process.Start(new ProcessStartInfo(id) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Write($"dock: could not start {id}: {ex.Message}");
        }
    }
}
