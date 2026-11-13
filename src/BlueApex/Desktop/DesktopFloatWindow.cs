using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace BlueApex.Desktop;

/// <summary>
/// A home-screen surface (widget, dock): a borderless, per-pixel-transparent top-level window kept
/// directly above the desktop in z-order, the way Rainmeter places its skins. Being a real top-level
/// window gives it anti-aliased rounded corners and translucent backgrounds with crisp text, which the
/// old "child of Progman" layer could not do (only whole-window alpha and hard-edged regions there).
///
/// It sits below every app window, so it shows when the desktop does. <see cref="DesktopZOrder"/>
/// lifts it while the desktop is in front (Win+D, a click on the desktop) and sinks it again after.
/// Mouse input arrives normally and is routed to the <see cref="IDesktopLayerItem"/> that owns it:
/// press-and-drag moves it (or scrolls a scrollable part), click, right-click, wheel.
/// </summary>
internal sealed class DesktopFloatWindow : IDisposable
{
    private const int DragThreshold = 10; // physical px

    private readonly DesktopHost _desktop;
    private readonly IDesktopLayerItem _item;
    private readonly Window _window;
    private IntPtr _hwnd;

    private bool _pressed, _dragging, _scrolling;
    private NativeMethods.POINT _pressScreen, _lastScreen;
    private (int X, int Y) _pressOrigin;

    /// <param name="x">Icon-view coordinates, physical pixels (the same coordinates widgets have always saved).</param>
    /// <param name="cornerDip">Content is clipped to this radius, so even a plugin that paints a square background gets round, smooth corners.</param>
    public DesktopFloatWindow(DesktopHost desktop, IDesktopLayerItem item, FrameworkElement content, int x, int y, int width, int height, double cornerDip = Ui.Theme.RadiusCard)
    {
        _desktop = desktop;
        _item = item;
        _cornerDip = cornerDip;
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Content = Rounded(content),
            Title = "BlueApex",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, // placed in physical pixels right after the handle exists
            Top = -32000,
        };
        _window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            // Tool window: no Alt+Tab entry. No-activate: clicking never steals focus or raises it.
            var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            SetBounds(x, y, width, height);
        };
        _window.MouseLeftButtonDown += OnLeftDown;
        _window.MouseMove += OnMove;
        _window.MouseLeftButtonUp += OnLeftUp;
        _window.MouseRightButtonUp += OnRightUp;
        _window.MouseWheel += OnWheel;
        _window.Show();
        DesktopZOrder.Register(this);
    }

    internal IntPtr Handle => _hwnd;

    public void SetBounds(int x, int y, int width, int height)
    {
        if (_hwnd == IntPtr.Zero) return;
        var screen = _desktop.IconToScreen(x, y);
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, screen.X, screen.Y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Swaps what the window draws (the dock redraws when its apps change).</summary>
    public void SetContent(FrameworkElement content) => _window.Content = Rounded(content);

    private readonly double _cornerDip;

    // A host that clips whatever it shows to a rounded rectangle of its own size (anti-aliased).
    private FrameworkElement Rounded(FrameworkElement content)
    {
        var host = new System.Windows.Controls.Grid { Children = { content } };
        host.SizeChanged += (_, e) =>
            host.Clip = new RectangleGeometry(new Rect(e.NewSize), _cornerDip, _cornerDip);
        return host;
    }

    public bool Visible
    {
        set { if (value) _window.Show(); else _window.Hide(); }
    }

    // --- mouse, in physical screen pixels so drags match the saved icon-view coordinates ---

    private NativeMethods.POINT Cursor()
    {
        GetCursorPos(out var p);
        return p;
    }

    // Cursor position inside the window, physical pixels from its top-left.
    private (int X, int Y) Relative(NativeMethods.POINT screen)
    {
        NativeMethods.GetWindowRect(_hwnd, out var r);
        return (screen.X - r.Left, screen.Y - r.Top);
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _dragging = false;
        _pressScreen = _lastScreen = Cursor();
        _pressOrigin = (_item.Bounds.Left, _item.Bounds.Top);
        var (rx, ry) = Relative(_pressScreen);
        _scrolling = _item is IDesktopLayerScrollable s && s.ScrollsAt(rx, ry);
        _window.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        var now = Cursor();
        var dx = now.X - _pressScreen.X;
        var dy = now.Y - _pressScreen.Y;
        if (!_dragging && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;
        _dragging = true;
        if (_scrolling)
        {
            ((IDesktopLayerScrollable)_item).ScrollBy(_lastScreen.X - now.X, _lastScreen.Y - now.Y);
        }
        else if (_item.Movable)
        {
            var (areaWidth, areaHeight) = _desktop.IconAreaSize;
            var b = _item.Bounds;
            var x = Math.Clamp(_pressOrigin.X + dx, 0, Math.Max(0, areaWidth - (b.Right - b.Left)));
            var y = Math.Clamp(_pressOrigin.Y + dy, 0, Math.Max(0, areaHeight - (b.Bottom - b.Top)));
            _item.MoveTo(x, y);
        }
        _lastScreen = now;
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        _window.ReleaseMouseCapture();
        e.Handled = true;
        if (_dragging)
        {
            if (!_scrolling) Guard(_item.Moved);
            return;
        }
        var (rx, ry) = Relative(Cursor());
        Guard(() => _item.Click(rx, ry));
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        var p = Cursor();
        Guard(() => _item.RightClick(p.X, p.Y));
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_item is not IDesktopLayerScrollable s) return;
        var (rx, ry) = Relative(Cursor());
        if (!s.ScrollsAt(rx, ry)) return;
        var notches = -e.Delta / 120; // positive delta = away from the user
        if (notches != 0) Guard(() => s.Wheel(notches));
        e.Handled = true;
    }

    // Widget code (plugins included) runs behind these calls; an exception must not take the app down.
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write($"home-screen item error: {ex}");
        }
    }

    public void Dispose()
    {
        DesktopZOrder.Unregister(this);
        _window.Close();
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativeMethods.POINT point);
}

/// <summary>
/// Keeps every <see cref="DesktopFloatWindow"/> just above the desktop: below all app windows while
/// an app is in front, lifted to the top while the desktop itself is (Win+D, or the user clicked it),
/// so the home screen never ends up behind the wallpaper. Driven by the system foreground-change event.
/// </summary>
internal static class DesktopZOrder
{
    private static readonly List<DesktopFloatWindow> Windows = new();
    private static WinEventDelegate? _callback; // kept alive for the hook's lifetime
    private static IntPtr _hook;
    private static IntPtr _progman;

    public static void Register(DesktopFloatWindow window)
    {
        Windows.Add(window);
        if (_hook == IntPtr.Zero)
        {
            _progman = NativeMethods.FindWindow("Progman", null);
            _callback = OnForeground;
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
        Arrange(DesktopInFront());
    }

    public static void Unregister(DesktopFloatWindow window)
    {
        Windows.Remove(window);
        if (Windows.Count == 0 && _hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private static bool DesktopInFront()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var cls = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(foreground, cls, cls.Capacity);
        return cls.ToString() is "Progman" or "WorkerW";
    }

    private static void OnForeground(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (Windows.Any(w => w.Handle == hwnd)) return; // one of ours (should not happen: they never activate)
            Arrange(DesktopInFront());
        }
        catch (Exception ex)
        {
            Log.Write($"z-order event error: {ex.Message}");
        }
    }

    // Lifted: top of the normal (non-topmost) band. Sunk: directly above Progman, under every app.
    private static void Arrange(bool lifted)
    {
        foreach (var window in Windows.ToList())
        {
            if (window.Handle == IntPtr.Zero) continue;
            IntPtr after;
            if (lifted)
            {
                after = HWND_TOP;
            }
            else
            {
                if (_progman == IntPtr.Zero) _progman = NativeMethods.FindWindow("Progman", null);
                after = GetWindow(_progman, GW_HWNDPREV); // the window just above the desktop; we go below it
                if (after == window.Handle) continue;     // already in place
                if (after == IntPtr.Zero) after = HWND_TOP;
            }
            NativeMethods.SetWindowPos(window.Handle, after, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint GW_HWNDPREV = 3;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
}
