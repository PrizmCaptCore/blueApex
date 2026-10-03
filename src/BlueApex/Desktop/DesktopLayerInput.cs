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

    /// <param name="deltaY">Physical pixels; positive scrolls the content up (finger drag upwards).</param>
    void ScrollBy(int deltaY);

    /// <param name="notches">Mouse-wheel notches; positive = wheel rolled towards the user (scroll down).</param>
    void Wheel(int notches);
}

/// <summary>
/// One low-level mouse hook for every item on the desktop layer. Those windows
/// sit below explorer's icon view and never get input of their own, so the hook
/// turns presses over an item into clicks, drags and right-clicks, and swallows
/// them so explorer does not see them. Everything heavier than hit-testing is
/// posted to the dispatcher (see ZoneMouseInteraction for why).
/// </summary>
internal sealed class DesktopLayerInput : IDisposable
{
    private const int DragThreshold = 10; // physical px

    private readonly DesktopHost _desktop;
    private readonly Action _desktopMouseUp;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelMouseProc _proc; // kept alive for the hook's lifetime
    private readonly IntPtr _hook;

    private IDesktopLayerItem? _pressed;
    private IDesktopLayerScrollable? _scrolling; // the press landed on a scrollable part of _pressed
    private IDesktopLayerItem? _rightPressed;
    private bool _dragging;
    private NativeMethods.POINT _pressPoint;
    private int _lastY;
    private (int X, int Y) _pressOrigin;

    /// <summary>Hit-tested from the end, so later items are "on top".</summary>
    public List<IDesktopLayerItem> Items { get; } = new();

    /// <param name="desktopMouseUp">Called (on the dispatcher) after any left release over the desktop that was not on an item, i.e. after a drop.</param>
    public DesktopLayerInput(DesktopHost desktop, Action desktopMouseUp)
    {
        _desktop = desktop;
        _desktopMouseUp = desktopMouseUp;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "마우스 훅을 설치하지 못했습니다.");
    }

    private bool IsDesktopAt(NativeMethods.POINT screen) =>
        NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(screen), NativeMethods.GA_ROOT) == _desktop.Progman;

    private IDesktopLayerItem? ItemAt(NativeMethods.POINT screen)
    {
        if (!IsDesktopAt(screen)) return null; // another window is in front of the desktop here
        var p = _desktop.ScreenToIcon(screen);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var b = Items[i].Bounds;
            if (p.X >= b.Left && p.X < b.Right && p.Y >= b.Top && p.Y < b.Bottom) return Items[i];
        }
        return null;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var message = (uint)wParam;
                var point = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam).Point;

                if (_pressed != null)
                {
                    if (message == NativeMethods.WM_MOUSEMOVE)
                    {
                        var dx = point.X - _pressPoint.X;
                        var dy = point.Y - _pressPoint.Y;
                        if (_scrolling != null)
                        {
                            // Dragging a list: scroll it instead of moving the item.
                            if (_dragging || Math.Abs(dy) >= DragThreshold)
                            {
                                _dragging = true;
                                var step = _lastY - point.Y;
                                _lastY = point.Y;
                                var target = _scrolling;
                                _dispatcher.BeginInvoke(() => target.ScrollBy(step));
                            }
                        }
                        else if (_pressed.Movable && (_dragging || Math.Abs(dx) >= DragThreshold || Math.Abs(dy) >= DragThreshold))
                        {
                            _dragging = true;
                            var (areaWidth, areaHeight) = _desktop.IconAreaSize;
                            var b = _pressed.Bounds;
                            var x = Math.Clamp(_pressOrigin.X + dx, 0, Math.Max(0, areaWidth - (b.Right - b.Left)));
                            var y = Math.Clamp(_pressOrigin.Y + dy, 0, Math.Max(0, areaHeight - (b.Bottom - b.Top)));
                            _pressed.MoveTo(x, y);
                        }
                    }
                    else if (message == NativeMethods.WM_LBUTTONUP)
                    {
                        var item = _pressed;
                        var scrolled = _scrolling != null && _dragging;
                        _pressed = null;
                        _scrolling = null;
                        if (scrolled)
                        {
                            _dragging = false; // a scroll ends quietly: no click, nothing to save
                        }
                        else if (_dragging)
                        {
                            _dragging = false;
                            _dispatcher.BeginInvoke(item.Moved);
                        }
                        else
                        {
                            var p = _desktop.ScreenToIcon(point);
                            var b = item.Bounds;
                            _dispatcher.BeginInvoke(() => item.Click(p.X - b.Left, p.Y - b.Top));
                        }
                        return 1;
                    }
                }
                else if (message == NativeMethods.WM_LBUTTONDOWN)
                {
                    var item = ItemAt(point);
                    if (item != null)
                    {
                        _pressed = item;
                        _dragging = false;
                        _pressPoint = point;
                        _lastY = point.Y;
                        _pressOrigin = (item.Bounds.Left, item.Bounds.Top);
                        var rel = _desktop.ScreenToIcon(point);
                        _scrolling = item is IDesktopLayerScrollable s && s.ScrollsAt(rel.X - item.Bounds.Left, rel.Y - item.Bounds.Top) ? s : null;
                        return 1; // swallowed: explorer never sees it
                    }
                }
                else if (message == NativeMethods.WM_LBUTTONUP)
                {
                    if (IsDesktopAt(point)) _dispatcher.BeginInvoke(_desktopMouseUp); // a drop on the desktop, most likely
                }
                else if (message == NativeMethods.WM_MOUSEWHEEL)
                {
                    // The wheel over a widget's list scrolls it; explorer never sees the event.
                    var item = ItemAt(point);
                    if (item is IDesktopLayerScrollable scrollable)
                    {
                        var rel = _desktop.ScreenToIcon(point);
                        if (scrollable.ScrollsAt(rel.X - item.Bounds.Left, rel.Y - item.Bounds.Top))
                        {
                            var delta = (short)((Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam).MouseData >> 16) & 0xFFFF);
                            var notches = -delta / 120; // WHEEL_DELTA; positive delta = away from the user
                            if (notches != 0) _dispatcher.BeginInvoke(() => scrollable.Wheel(notches));
                            return 1;
                        }
                    }
                }
                else if (message == NativeMethods.WM_RBUTTONDOWN)
                {
                    _rightPressed = ItemAt(point);
                    if (_rightPressed != null) return 1;
                }
                else if (message == NativeMethods.WM_RBUTTONUP && _rightPressed != null)
                {
                    var item = _rightPressed;
                    _rightPressed = null;
                    _dispatcher.BeginInvoke(() => item.RightClick(point.X, point.Y));
                    return 1;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"layer input hook error: {ex}");
            _pressed = null;
            _scrolling = null;
            _rightPressed = null;
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        NativeMethods.UnhookWindowsHookEx(_hook);
    }
}
