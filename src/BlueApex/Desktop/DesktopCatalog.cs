using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace BlueApex.Desktop;

/// <summary>
/// Everything on the desktop, hidden files included: the user's and the public
/// Desktop folders read directly, plus shell items (Recycle Bin...) from the view.
/// Hiding an icon means setting the file's Hidden attribute; explorer then leaves
/// it out of the desktop by itself and brings it back, at its old spot, when the
/// attribute is cleared.
/// </summary>
internal static class DesktopCatalog
{
    public static readonly string UserDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static readonly string PublicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public static List<DesktopIcon> Scan(DesktopIconView view)
    {
        var icons = ScanFiles();
        icons.AddRange(view.GetIcons().Where(i => i.IsShellItem));
        return icons;
    }

    /// <summary>
    /// The files and folders on both desktops, read straight from disk. Unlike <see cref="Scan"/> this
    /// does not touch explorer's icon view, so it keeps working while explorer restarts (installers do that).
    /// </summary>
    public static List<DesktopIcon> ScanFiles()
    {
        var icons = new List<DesktopIcon>();
        foreach (var folder in new[] { UserDesktop, PublicDesktop })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // vanished or locked mid-scan (an installer still writing it); next poll sees it
                }
                if (attributes.HasFlag(FileAttributes.System)) continue; // desktop.ini and the like
                icons.Add(new DesktopIcon(path, DisplayName(path), 0, 0, false, attributes.HasFlag(FileAttributes.Hidden)));
            }
        }
        return icons;
    }

    /// <summary>
    /// Sets or clears the Hidden attribute. False when the file cannot be changed (typically the public desktop without rights).
    /// Shell items ("::{CLSID}", e.g. the Recycle Bin) have no attribute; they go through explorer's per-user
    /// "desktop icons" setting instead (see <see cref="SetShellItemHidden"/>).
    /// </summary>
    public static bool SetHidden(string path, bool hidden)
    {
        if (path.StartsWith("::", StringComparison.Ordinal)) return SetShellItemHidden(path, hidden);
        try
        {
            var attributes = File.GetAttributes(path);
            var wanted = hidden ? attributes | FileAttributes.Hidden : attributes & ~FileAttributes.Hidden;
            if (wanted != attributes) File.SetAttributes(path, wanted);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Whether the current user may change attributes of files in the public desktop.</summary>
    public static bool CanHideInPublicDesktop()
    {
        var probe = Directory.EnumerateFiles(PublicDesktop).FirstOrDefault();
        if (probe == null) return true;
        try
        {
            File.SetAttributes(probe, File.GetAttributes(probe)); // a no-op write still needs the right
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Grants the current user attribute rights on the public desktop (inherited, so
    /// future installer shortcuts are covered) through one elevation prompt.
    /// Returns true when the command ran and succeeded.
    /// </summary>
    public static bool GrantPublicDesktopAccess()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) return false;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{PublicDesktop}\" /grant *{sid}:(OI)(CI)(WA,RA)",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the elevation prompt was declined
        }
    }

    /// <summary>Explorer's "show hidden files" option: when on, hidden icons still show (dimmed).</summary>
    public static bool ExplorerShowsHiddenFiles()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return key?.GetValue("Hidden") is int value && value == 1;
    }

    /// <summary>The visible entries of any folder (for portal zones), newest first, capped so a huge folder stays usable.</summary>
    public static List<DesktopIcon> EnumerateFolder(string folder, int limit = 1000)
    {
        var icons = new List<DesktopIcon>();
        if (!Directory.Exists(folder)) return icons;
        var entries = new DirectoryInfo(folder).EnumerateFileSystemInfos()
            .Where(e => !e.Attributes.HasFlag(FileAttributes.Hidden) && !e.Attributes.HasFlag(FileAttributes.System))
            .OrderByDescending(e => e is DirectoryInfo) // folders first
            .ThenByDescending(e => e.LastWriteTimeUtc)
            .Take(limit);
        foreach (var entry in entries)
            icons.Add(new DesktopIcon(entry.FullName, DisplayName(entry.FullName), 0, 0, false));
        return icons;
    }

    /// <summary>Creates a folder on the user's desktop, adding " (2)", " (3)"... if the name is taken. Returns its path.</summary>
    public static string CreateDesktopFolder(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var path = Path.Combine(UserDesktop, name);
        for (var n = 2; Directory.Exists(path) || File.Exists(path); n++)
            path = Path.Combine(UserDesktop, $"{name} ({n})");
        Directory.CreateDirectory(path);
        return path;
    }

    // The same switch as Settings > Personalization > Themes > Desktop icon settings: per user, no admin.
    private const string HideDesktopIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    /// <summary>Whether explorer's desktop-icon setting currently hides this shell item.</summary>
    public static bool IsShellItemHidden(string id)
    {
        using var key = Registry.CurrentUser.OpenSubKey(HideDesktopIconsKey);
        return key?.GetValue(id[2..]) is int value && value != 0;
    }

    /// <summary>Shows or hides a shell item ("::{CLSID}") on the desktop, then asks explorer to redraw it.</summary>
    private static bool SetShellItemHidden(string id, bool hidden)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(HideDesktopIconsKey))
                key.SetValue(id[2..], hidden ? 1 : 0, RegistryValueKind.DWord);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); // the desktop re-reads the setting
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    // The name explorer shows: no ".lnk"/".url", and extensions per the user's setting.
    public static string DisplayName(string path)
    {
        var info = new SHFILEINFO();
        return SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME) != IntPtr.Zero && info.szDisplayName.Length > 0
            ? info.szDisplayName
            : Path.GetFileNameWithoutExtension(path);
    }

    private const uint SHGFI_DISPLAYNAME = 0x200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
}
