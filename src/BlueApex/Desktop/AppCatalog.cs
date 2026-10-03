using System.Runtime.InteropServices;

namespace BlueApex.Desktop;

/// <summary>
/// Every installed app, from the shell's Applications folder (what the Start
/// menu's "All apps" shows): classic programs with Start-menu shortcuts and
/// Store apps alike. Ids are "app:" + the app's id inside that folder, which
/// <c>shell:AppsFolder\&lt;id&gt;</c> resolves back to for launching and icons.
/// </summary>
internal static class AppCatalog
{
    public const string IdPrefix = "app:";
    private const string AppsFolder = "shell:AppsFolder";

    public static bool IsAppId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>The shell parsing name for an app id, usable with SHCreateItemFromParsingName and ShellExecute.</summary>
    public static string ParsingName(string id) => AppsFolder + "\\" + id[IdPrefix.Length..];

    public static List<DesktopIcon> Scan()
    {
        var apps = new List<DesktopIcon>();
        var folderIid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(AppsFolder, IntPtr.Zero, ref folderIid, out var folder) < 0)
            return apps;

        var enumIid = typeof(IEnumShellItems).GUID;
        var bhid = BHID_EnumItems;
        folder.BindToHandler(IntPtr.Zero, ref bhid, ref enumIid, out var enumerator);

        while (enumerator.Next(1, out var item, out var fetched) == 0 && fetched == 1)
        {
            try
            {
                item.GetDisplayName(SIGDN_PARENTRELATIVEPARSING, out var appId);
                item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name);
                if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(name))
                    apps.Add(new DesktopIcon(IdPrefix + appId, name, 0, 0, false));
            }
            catch (COMException)
            {
                // an entry the shell cannot describe; skip it
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        Marshal.ReleaseComObject(enumerator);
        Marshal.ReleaseComObject(folder);
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    private static readonly Guid BHID_EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IEnumShellItems handler);
        void _GetParent();
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void _GetAttributes();
        void _Compare();
    }

    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);
        void _Skip();
        void _Reset();
        void _Clone();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItem item);
}
