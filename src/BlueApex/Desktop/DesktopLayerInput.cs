using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace BlueApex.Desktop;

/// <summary>Something drawn on the desktop layer that reacts to the mouse: the drawer button, a widget.</summary>
internal interface IDesktopLayerItem
{
    /// <summary>Icon-view coordinates, physical pixels.</summary>
    NativeMethods.RECT Bounds { get; }

    bool Movable { get; }

    /// <summary>Live position update while being dragged.</summary>
    void MoveTo(int x, int y);

    /// <summary>The drag ended; persist the position.</summary>
    void Moved();

    /// <param name="x">Where inside the item (physical pixels from its top-left).</param>
    void Click(int x, int y);

    /// <param name="screenX">Physical screen pixels.</param>
    void RightClick(int screenX, int screenY);
}

/// <summary>An item with a region that scrolls when dragged (a widget's list) instead of moving the whole item.</summary>
internal interface IDesktopLayerScrollable
{
    /// <param name="x">Physical pixels from the item's top-left.</param>
    bool ScrollsAt(int x, int y);

    /// <param name="deltaX">Physical pixels; positive = the finger moved left, so the content follows left.</param>
    /// <param name="deltaY">Physical pixels; positive = the finger moved up.</param>
    void ScrollBy(int deltaX, int deltaY);

    /// <param name="notches">Mouse-wheel notches; positive = wheel rolled towards the user (scroll down).</param>
    void Wheel(int notches);
}

/// <summary>
/// Watches for a left-button release over the desktop (most likely something was just dropped there),
/// so the drawer can sort it in at once instead of at the next poll. The dock and widgets take their
/// own clicks (<see cref="DesktopFloatWindow"/>), so the hook never swallows anything.
///
/// The low-level mouse hook lives on its own thread with its own message loop. Windows runs a
/// low-level hook on the thread that installed it, and every mouse move in the system waits for it:
/// on the UI thread, any busy moment (startup building the drawer, decoding hundreds of icons)
/// froze the cursor, and a hook that answers too slowly is silently removed by Windows.
/// The callback only hit-tests and posts to the dispatcher.
/// </summary>
internal sealed class DesktopLayerInput : IDisposable
{
    private readonly DesktopHost _desktop;
    private readonly Action _desktopMouseUp;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelMouseProc _proc; // kept alive for the hook's lifetime
    private readonly Thread _thread;
    private IntPtr _hook;
    private uint _threadId;

    /// <param name="desktopMouseUp">Called (on the dispatcher) after a left release over the desktop, i.e. after a drop.</param>
    public DesktopLayerInput(DesktopHost desktop, Action desktopMouseUp)
    {
        _desktop = desktop;
        _desktopMouseUp = desktopMouseUp;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = HookCallback;
        using var installed = new ManualResetEventSlim();
        var error = 0;
        _thread = new Thread(() =>
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) error = Marshal.GetLastWin32Error();
            installed.Set();
            if (_hook == IntPtr.Zero) return;
            // The hook is called from inside this loop; WM_QUIT (from Dispose) ends it.
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
            NativeMethods.UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "BlueApex mouse hook" };
        _thread.Start();
        installed.Wait();
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(error, "마우스 훅을 설치하지 못했습니다.");
    }

    private bool IsDesktopAt(NativeMethods.POINT screen) =>
        NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(screen), NativeMethods.GA_ROOT) == _desktop.Progman;

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && (uint)wParam == NativeMethods.WM_LBUTTONUP)
            {
                var point = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam).Point;
                if (IsDesktopAt(point)) _dispatcher.BeginInvoke(_desktopMouseUp); // a drop on the desktop, most likely
            }
        }
        catch (Exception ex)
        {
            Log.Write($"layer input hook error: {ex}");
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
