using System.Runtime.InteropServices;

namespace BlueApex.Desktop;

/// <summary>
/// Locates explorer's desktop windows and places our windows between the icon
/// layer and the wallpaper layer.
///
/// Expected layout (Windows 11 24H2 and later, verified on build 26300):
///   Progman              (no redirection surface: only layered children are composited)
///     SHELLDLL_DefView   (icons)
///       SysListView32    (icon positions are in this window's client coordinates)
///     [our windows]
///     WorkerW            (wallpaper; Wallpaper Engine parents itself here)
/// If a Windows update breaks this, run tools/DesktopProbe to see the new layout.
/// </summary>
internal sealed class DesktopHost
{
    // Undocumented but long-standing: asks Progman to move the wallpaper into
    // its own WorkerW so other windows can sit between it and the icons.
    private const uint WM_SPAWN_WORKER = 0x052C;

    public IntPtr Progman { get; }
    public IntPtr IconView { get; }
    public IntPtr IconList { get; }

    private DesktopHost(IntPtr progman, IntPtr iconView, IntPtr iconList)
    {
        Progman = progman;
        IconView = iconView;
        IconList = iconList;
    }

    public static DesktopHost Find()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            throw new NotSupportedException("Progman 창을 찾을 수 없습니다. explorer.exe가 실행 중인지 확인하세요.");

        if (NativeMethods.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) == IntPtr.Zero)
            NativeMethods.SendMessageTimeout(progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        var iconView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var iconList = iconView == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.FindWindowEx(iconView, IntPtr.Zero, "SysListView32", null);
        if (iconList == IntPtr.Zero)
            throw new NotSupportedException(
                "지원하지 않는 바탕화면 창 구조입니다(Progman 아래에 SHELLDLL_DefView/SysListView32 없음). " +
                "tools/DesktopProbe를 실행해 현재 구조를 확인하세요.");

        return new DesktopHost(progman, iconView, iconList);
    }

    public uint Dpi => NativeMethods.GetDpiForWindow(Progman);

    /// <summary>Size of the icon area (the whole virtual screen) in physical pixels.</summary>
    public (int Width, int Height) IconAreaSize
    {
        get
        {
            NativeMethods.GetClientRect(IconList, out var r);
            return (r.Right, r.Bottom);
        }
    }

    /// <summary>Converts icon-view coordinates to coordinates of the window our zones are parented to.</summary>
    /// <summary>Converts a screen point (physical pixels) to icon-view coordinates.</summary>
    public NativeMethods.POINT ScreenToIcon(NativeMethods.POINT screen)
    {
        NativeMethods.ScreenToClient(IconList, ref screen);
        return screen;
    }

    public NativeMethods.POINT IconToHost(int x, int y)
    {
        var pt = new NativeMethods.POINT { X = x, Y = y };
        NativeMethods.MapWindowPoints(IconList, Progman, ref pt, 1);
        return pt;
    }

    /// <summary>Puts a child of <see cref="Progman"/> directly below the icons in z-order.</summary>
    public void PlaceBelowIcons(IntPtr hwnd)
    {
        NativeMethods.SetWindowPos(hwnd, IconView, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}

internal static class NativeMethods
{
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint LWA_ALPHA = 0x2;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;
    public const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT point, uint count);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    // Low-level mouse hook
    public const int WH_MOUSE_LL = 14;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder buffer, int max);

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
}
