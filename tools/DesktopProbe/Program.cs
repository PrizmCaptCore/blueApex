// Read-only diagnostic: dumps the desktop window hierarchy and the desktop icon
// list (names, positions, view flags). Run it after a Windows update to see
// whether the structure the app depends on has changed. It changes nothing.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels

        DumpOsVersion();
        DumpWindowTree();
        try
        {
            DumpDesktopIcons();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[icons] FAILED: {ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
            return 1;
        }
        return 0;
    }

    private static void DumpOsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        Console.WriteLine("== OS ==");
        Console.WriteLine($"build {key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")}  " +
                          $"display {key?.GetValue("DisplayVersion")}  branch {key?.GetValue("BuildBranch")}");
        Console.WriteLine();
    }

    private static void DumpWindowTree()
    {
        Console.WriteLine("== Desktop windows (top-level Progman/WorkerW, in z-order, children in z-order) ==");
        Native.EnumWindows((hwnd, _) =>
        {
            var cls = Native.ClassName(hwnd);
            if (cls is "Progman" or "WorkerW")
                DumpWindow(hwnd, 0);
            return true;
        }, IntPtr.Zero);
        Console.WriteLine();
    }

    private static void DumpWindow(IntPtr hwnd, int depth)
    {
        Native.GetWindowRect(hwnd, out var r);
        var style = Native.GetWindowLongPtr(hwnd, -16).ToInt64() & 0xFFFFFFFF;
        var exStyle = Native.GetWindowLongPtr(hwnd, -20).ToInt64() & 0xFFFFFFFF;
        Console.WriteLine($"{new string(' ', depth * 2)}{Native.ClassName(hwnd)} 0x{hwnd.ToInt64():X} " +
                          $"\"{Native.Title(hwnd)}\" visible={Native.IsWindowVisible(hwnd)} " +
                          $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) style=0x{style:X8} ex=0x{exStyle:X8}");

        for (var child = Native.GetWindow(hwnd, Native.GW_CHILD);
             child != IntPtr.Zero;
             child = Native.GetWindow(child, Native.GW_HWNDNEXT))
        {
            DumpWindow(child, depth + 1);
        }
    }

    private static void DumpDesktopIcons()
    {
        Console.WriteLine("== Desktop icons (IFolderView) ==");

        var view = DesktopView.Find();
        var folderView = (IFolderView2)view;

        folderView.GetCurrentFolderFlags(out var flags);
        folderView.GetViewModeAndIconSize(out var viewMode, out var iconSize);
        folderView.GetSpacing(out var spacing);
        folderView.ItemCount(Native.SVGIO_ALLVIEW, out var count);

        Console.WriteLine($"flags=0x{flags:X} autoArrange={(flags & 0x1) != 0} snapToGrid={(flags & 0x4) != 0} " +
                          $"viewMode={viewMode} iconSize={iconSize} spacing={spacing.X}x{spacing.Y} items={count}");

        var folderIid = typeof(IShellFolder).GUID;
        folderView.GetFolder(ref folderIid, out var folderObj);
        var folder = (IShellFolder)folderObj;

        for (var i = 0; i < count; i++)
        {
            folderView.Item(i, out var pidl);
            try
            {
                folderView.GetItemPosition(pidl, out var pt);
                var name = DisplayName(folder, pidl, Native.SHGDN_NORMAL);
                var parsing = DisplayName(folder, pidl, Native.SHGDN_FORPARSING);
                Console.WriteLine($"  [{i,3}] ({pt.X,5},{pt.Y,5})  {name}  <{parsing}>");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static string DisplayName(IShellFolder folder, IntPtr pidl, uint flags)
    {
        var strret = Marshal.AllocCoTaskMem(Native.STRRET_SIZE);
        try
        {
            var hr = folder.GetDisplayNameOf(pidl, flags, strret);
            if (hr < 0) return $"<hr 0x{hr:X8}>";
            Native.StrRetToBSTR(strret, pidl, out var name);
            return name;
        }
        finally
        {
            Marshal.FreeCoTaskMem(strret);
        }
    }
}

internal static class DesktopView
{
    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;

    // The documented route to the desktop's shell view: ShellWindows -> desktop
    // browser -> active view. No window-class lookups or message injection.
    public static object Find()
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows)!)!;
        object? location = 0; // CSIDL_DESKTOP
        object? root = null;
        var dispatch = shellWindows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH)
                       ?? throw new InvalidOperationException("Desktop shell window not found (is explorer.exe running?)");

        var browserSid = SID_STopLevelBrowser;
        var browserIid = typeof(IShellBrowser).GUID;
        ((IServiceProvider)dispatch).QueryService(ref browserSid, ref browserIid, out var browserObj);
        ((IShellBrowser)browserObj).QueryActiveShellView(out var view);
        return view;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
}

// Slots that are never called are declared as parameterless placeholders; only
// their order matters for the vtable layout.

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    void _get_Count();
    void _Item();
    void _NewEnum();
    void _Register();
    void _RegisterPending();
    void _Revoke();
    void _OnNavigate();
    void _OnActivated();
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? FindWindowSW([In] ref object? pvarLoc, [In] ref object? pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    void QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    void _GetWindow();
    void _ContextSensitiveHelp();
    void _InsertMenusSB();
    void _SetMenuSB();
    void _RemoveMenusSB();
    void _SetStatusTextSB();
    void _EnableModelessSB();
    void _TranslateAcceleratorSB();
    void _BrowseObject();
    void _GetViewStateStream();
    void _GetControlWindow();
    void _SendControlMsg();
    void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
}

[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    void _ParseDisplayName();
    void _EnumObjects();
    void _BindToObject();
    void _BindToStorage();
    void _CompareIDs();
    void _CreateViewObject();
    void _GetAttributesOf();
    void _GetUIObjectOf();
    [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
}

[ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    // IFolderView
    void GetCurrentViewMode(out uint pViewMode);
    void SetCurrentViewMode(uint viewMode);
    void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void Item(int iItemIndex, out IntPtr ppidl);
    void ItemCount(uint uFlags, out int pcItems);
    void Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void GetSelectionMarkedItem(out int piItem);
    void GetFocusedItem(out int piItem);
    void GetItemPosition(IntPtr pidl, out POINT ppt);
    void GetSpacing(out POINT ppt);
    void GetDefaultSpacing(out POINT ppt);
    [PreserveSig] int GetAutoArrange();
    void SelectItem(int iItem, uint dwFlags);
    void SelectAndPositionItems(uint cidl, [In] IntPtr[] apidl, [In] POINT[] apt, uint dwFlags);

    // IFolderView2
    void _SetGroupBy();
    void _GetGroupBy();
    void _SetViewProperty();
    void _GetViewProperty();
    void _SetTileViewProperties();
    void _SetExtendedTileViewProperties();
    void _SetText();
    void SetCurrentFolderFlags(uint dwMask, uint dwFlags);
    void GetCurrentFolderFlags(out uint pdwFlags);
    void _GetSortColumnCount();
    void _SetSortColumns();
    void _GetSortColumns();
    void _GetItem();
    void _GetVisibleItem();
    void _GetSelectedItem();
    void _GetSelection();
    void _GetSelectionState();
    void _InvokeVerbOnSelection();
    void SetViewModeAndIconSize(uint uViewMode, int iImageSize);
    void GetViewModeAndIconSize(out uint puViewMode, out int piImageSize);
}

internal static class Native
{
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_CHILD = 5;
    public const uint SVGIO_ALLVIEW = 2;
    public const uint SHGDN_NORMAL = 0;
    public const uint SHGDN_FORPARSING = 0x8000;
    public const int STRRET_SIZE = 272;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int max);

    [DllImport("shlwapi.dll")]
    public static extern int StrRetToBSTR(IntPtr pstr, IntPtr pidl, [MarshalAs(UnmanagedType.BStr)] out string pbstr);

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
