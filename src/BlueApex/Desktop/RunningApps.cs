using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BlueApex.Desktop;

/// <summary>An app with at least one window the taskbar would show, its windows in z-order (top first).</summary>
/// <param name="Key">"aumid:&lt;AppUserModelID&gt;" when the window carries one (Store apps, many modern apps), else the exe path in lower case.</param>
/// <param name="IconSource">What <see cref="Drawer.IconLoader"/> should draw: the exe, or the app's shell:AppsFolder entry.</param>
internal sealed record RunningApp(string Key, string Name, string? ExePath, string? AppId, string IconSource, IReadOnlyList<IntPtr> Windows);

/// <summary>
/// The apps that have windows open, as the taskbar sees them: visible, unowned, not tool windows,
/// not cloaked (background Store apps), with a title. Grouped by app so one app is one dock slot.
/// </summary>
internal static class RunningApps
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    public static List<RunningApp> Scan()
    {
        var self = (uint)Environment.ProcessId;
        var windows = new List<(IntPtr Hwnd, string Title, string? Exe, string? AppId)>();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (IsAppWindow(hwnd, self, out var title, out var pid))
                    windows.Add((hwnd, title, ExeOf(pid), AppIdOf(hwnd)));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or System.ComponentModel.Win32Exception)
            {
                // a window that vanished mid-scan
            }
            return true;
        }, IntPtr.Zero);

        return windows
            .GroupBy(w => w.AppId != null ? "aumid:" + w.AppId : (w.Exe ?? "").ToLowerInvariant())
            .Where(g => g.Key.Length > 0)
            .Select(g =>
            {
                var first = g.First();
                var icon = first.AppId != null ? "shell:AppsFolder\\" + first.AppId : first.Exe!;
                return new RunningApp(g.Key, NameOf(first.Exe, first.Title), first.Exe, first.AppId, icon, g.Select(w => w.Hwnd).ToList());
            })
            .ToList();
    }

    /// <summary>Brings the app's top window forward, restoring it if minimized.</summary>
    public static void Activate(RunningApp app)
    {
        var hwnd = app.Windows[0];
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        // The click reached us through a mouse hook, so Windows may refuse a plain SetForegroundWindow;
        // SwitchToThisWindow is what Alt+Tab uses and is allowed here.
        if (!NativeMethods.SetForegroundWindow(hwnd)) SwitchToThisWindow(hwnd, true);
    }

    private static bool IsAppWindow(IntPtr hwnd, uint self, out string title, out uint pid)
    {
        title = "";
        pid = 0;
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var cls = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return false;
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return false;
        var text = new StringBuilder(length + 1);
        GetWindowText(hwnd, text, text.Capacity);
        title = text.ToString();
        NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        return pid != self;
    }

    private static string? ExeOf(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // The product name the exe advertises ("Google Chrome"), else the window title.
    private static string NameOf(string? exe, string title)
    {
        if (exe != null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
            {
                // fall back to the title
            }
        }
        return title;
    }

    // System.AppUserModel.ID from the window's property store; Store apps hosted in
    // ApplicationFrameHost.exe are only told apart by this.
    private static string? AppIdOf(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            if (store.GetValue(ref key, out var value) != 0) return null;
            try
            {
                return value.vt == VT_LPWSTR && value.pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public IntPtr pointer;
        public IntPtr pointer2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT value);
}
