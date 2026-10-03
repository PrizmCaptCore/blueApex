using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using BlueApex.Desktop;

namespace BlueApex.Zones;

/// <summary>
/// Lets the user move a zone by its title bar and resize it by its edges.
///
/// Zone windows sit below explorer's icon view, which gets every mouse event on
/// the desktop. So a low-level mouse hook watches for a left press that lands on
/// the desktop over a zone's title bar or edge (and not on an icon), swallows
/// it so explorer does not start a rubber-band selection, and tracks the drag.
///
/// The hook callback itself only hit-tests and records state. Everything that
/// talks to explorer (moving icons) is posted to the dispatcher: COM refuses
/// outgoing calls from inside a low-level hook (RPC_E_CANTCALLOUT_ININPUTSYNCCALL),
/// and an exception escaping the callback kills the process and freezes the
/// mouse until Windows drops the hook.
/// </summary>
internal sealed class ZoneMouseInteraction : IDisposable
{
    // Title bar moves; the grip drawn in the bottom-right corner resizes. Nothing else is intercepted.
    private enum Hit { None, Title, Grip }

    private readonly DesktopHost _desktop;
    private readonly ZoneManager _zones;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelMouseProc _proc; // kept alive for the hook's lifetime
    private readonly IntPtr _hook;

    // Set by the hook, read by dispatcher work; all on the UI thread.
    private readonly ZoneMenu _menu;
    private Zone? _menuZone;
    private Zone? _active;
    private Hit _hit;
    private NativeMethods.POINT _pressPoint;
    private (int X, int Y, int Width, int Height) _pressBounds;
    private NativeMethods.POINT _latestPoint;
    private bool _movePending;
    private ZoneManager.ZoneDrag? _session;

    // Double-click tracking (title: roll up; empty desktop: quick hide).
    private const int DoubleClickSlop = 8; // physical px
    private static readonly object QuickHideTarget = new();
    private (object? Target, long Time, NativeMethods.POINT Point) _lastPress;
    private bool _swallowNextLeftUp;

    // Drawing a new zone with a right-drag on empty desktop.
    private const int DrawThreshold = 12; // physical px before a press counts as a drag
    private static readonly UIntPtr ReplayMarker = new(0x4446_4D52); // "DFMR": tags our replayed clicks
    private bool _drawing;
    private NativeMethods.POINT _drawStart;
    private Zone? _draft;
    private ZoneSurface? _draftSurface;

    public ZoneMouseInteraction(DesktopHost desktop, ZoneManager zones)
    {
        _desktop = desktop;
        _zones = zones;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _menu = new ZoneMenu(zones);
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "마우스 훅을 설치하지 못했습니다.");
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var message = (uint)wParam;
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var point = info.Point;

                if (info.ExtraInfo == ReplayMarker)
                {
                    // our own replayed right-click: let explorer have it
                }
                else if (_drawing)
                {
                    if (message == NativeMethods.WM_MOUSEMOVE)
                    {
                        _latestPoint = point;
                        if (!_movePending)
                        {
                            _movePending = true;
                            _dispatcher.BeginInvoke(ApplyDraw);
                        }
                    }
                    else if (message == NativeMethods.WM_RBUTTONUP)
                    {
                        _drawing = false;
                        _dispatcher.BeginInvoke(EndDraw);
                        return 1;
                    }
                }
                else if (_active == null)
                {
                    if (message == NativeMethods.WM_LBUTTONUP && _swallowNextLeftUp)
                    {
                        _swallowNextLeftUp = false;
                        return 1;
                    }
                    if (message == NativeMethods.WM_LBUTTONDOWN)
                    {
                        var target = Find(point, out var hit);
                        if (target != null && hit == Hit.Title && IsDoubleClick(target, point))
                        {
                            // Title double-click: roll the zone up or down instead of starting a drag.
                            _swallowNextLeftUp = true;
                            _dispatcher.BeginInvoke(() => _zones.ToggleRolled(target));
                            return 1;
                        }
                        if (target != null && TryBegin(point))
                        {
                            _dispatcher.BeginInvoke(StartDrag);
                            return 1; // swallowed: explorer never sees the press
                        }
                        if (target == null && IsDesktopAt(point) && (_zones.Hidden || IsEmptyDesktopAt(point)))
                        {
                            // Empty-desktop double-click: quick hide / show everything. The first
                            // click reaches explorer as usual; only the second is swallowed.
                            if (IsDoubleClick(QuickHideTarget, point))
                            {
                                _swallowNextLeftUp = true;
                                _dispatcher.BeginInvoke(_zones.ToggleHidden);
                                return 1;
                            }
                        }
                    }
                    if (message == NativeMethods.WM_RBUTTONDOWN && _menuZone == null)
                    {
                        // Right press on a title bar: swallow the pair and open our menu on release,
                        // so explorer never shows the desktop menu there.
                        _menuZone = Find(point, out var hit) is { } zone && hit == Hit.Title ? zone : null;
                        if (_menuZone != null) return 1;

                        // Right press on empty desktop: may become a new zone if dragged.
                        // Swallowed now; a plain click is replayed to explorer on release.
                        if (!_zones.Hidden && IsEmptyDesktopAt(point))
                        {
                            _drawing = true;
                            _drawStart = point;
                            _latestPoint = point;
                            return 1;
                        }
                    }
                    if (message == NativeMethods.WM_RBUTTONUP && _menuZone != null)
                    {
                        var zone = _menuZone;
                        _menuZone = null;
                        _dispatcher.BeginInvoke(() => _menu.Show(zone, point.X, point.Y));
                        return 1;
                    }
                }
                else if (message == NativeMethods.WM_MOUSEMOVE)
                {
                    // Not swallowed, or the cursor would freeze. Moves are coalesced:
                    // one pending update applies the latest point.
                    _latestPoint = point;
                    if (!_movePending)
                    {
                        _movePending = true;
                        _dispatcher.BeginInvoke(ApplyMove);
                    }
                }
                else if (message == NativeMethods.WM_LBUTTONUP)
                {
                    _active = null;
                    _dispatcher.BeginInvoke(EndDrag);
                    return 1;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"mouse hook error: {ex}");
            _active = null;
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool TryBegin(NativeMethods.POINT screen)
    {
        var zone = Find(screen, out var hit);
        if (zone == null) return false;

        _active = zone;
        _hit = hit;
        _pressPoint = _desktop.ScreenToIcon(screen);
        _latestPoint = screen;
        _pressBounds = (zone.X, zone.Y, zone.Width, zone.Height);
        return true;
    }

    /// <summary>The zone whose title bar or grip is under the screen point, if the desktop is exposed there.</summary>
    private Zone? Find(NativeMethods.POINT screen, out Hit hit)
    {
        hit = Hit.None;
        if (_zones.Hidden || !IsDesktopAt(screen))
            return null; // another window is in front of the desktop here

        var point = _desktop.ScreenToIcon(screen);
        foreach (var zone in _zones.Zones)
        {
            hit = HitTest(zone, point);
            if (hit == Hit.None) continue;
            // An icon cell can reach into the grip square; the grip wins there since it is
            // the only way to resize. Elsewhere, clicks on icons are explorer's.
            if (hit != Hit.Grip && _zones.IsIconAt(point.X, point.Y)) return null;
            return zone;
        }
        return null;
    }

    private Hit HitTest(Zone zone, NativeMethods.POINT p)
    {
        if (!_zones.Layout.Contains(zone, p.X, p.Y))
            return Hit.None;

        var grip = _zones.GripSize;
        if (!zone.Rolled && p.X >= zone.X + zone.Width - grip && p.Y >= zone.Y + zone.Height - grip)
            return Hit.Grip;
        return p.Y < zone.Y + _zones.HeaderHeight ? Hit.Title : Hit.None;
    }

    /// <summary>Whether the desktop (icons shown or hidden) is what is under the screen point.</summary>
    private bool IsDesktopAt(NativeMethods.POINT screen) =>
        NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(screen), NativeMethods.GA_ROOT) == _desktop.Progman;

    /// <summary>
    /// Double-click detection for the low-level hook, which never reports WM_LBUTTONDBLCLK.
    /// Returns true when this press is the second of a double-click on the same target.
    /// </summary>
    private bool IsDoubleClick(object target, NativeMethods.POINT screen)
    {
        var now = Environment.TickCount64;
        var isDouble = _lastPress.Target == target
                       && now - _lastPress.Time <= NativeMethods.GetDoubleClickTime()
                       && Math.Abs(screen.X - _lastPress.Point.X) <= DoubleClickSlop
                       && Math.Abs(screen.Y - _lastPress.Point.Y) <= DoubleClickSlop;
        _lastPress = isDouble ? default : (target, now, screen);
        return isDouble;
    }

    // --- dispatcher work (may call into explorer) ---

    private void StartDrag()
    {
        var zone = _active;
        if (zone == null) return; // released before this ran
        try
        {
            _session = _zones.BeginDrag(zone);
        }
        catch (COMException ex)
        {
            Log.Write($"drag: could not resolve member icons, they will settle on release ({ex.Message})");
        }
    }

    private void ApplyMove()
    {
        _movePending = false;
        var zone = _active;
        if (zone == null) return;

        var point = _desktop.ScreenToIcon(_latestPoint);
        var dx = point.X - _pressPoint.X;
        var dy = point.Y - _pressPoint.Y;
        var (x, y, width, height) = _pressBounds;
        var (minWidth, minHeight) = _zones.MinZoneSize;
        var (areaWidth, areaHeight) = _zones.IconArea;

        if (_hit == Hit.Title)
        {
            x = Math.Clamp(x + dx, 0, areaWidth - width);
            y = Math.Clamp(y + dy, 0, areaHeight - height);
        }
        else
        {
            width = Math.Clamp(width + dx, minWidth, areaWidth - x);
            height = Math.Clamp(height + dy, minHeight, areaHeight - y);
        }

        _zones.SetZoneBounds(zone, x, y, width, height);
        try
        {
            _session?.FollowZone();
        }
        catch (COMException ex)
        {
            Log.Write($"drag: icon move failed ({ex.Message})");
        }
    }

    /// <summary>Desktop exposed, no zone and no icon cell at the screen point.</summary>
    private bool IsEmptyDesktopAt(NativeMethods.POINT screen)
    {
        if (!IsDesktopAt(screen))
            return false;
        var point = _desktop.ScreenToIcon(screen);
        return !_zones.IsIconAt(point.X, point.Y) && !_zones.IsZoneAt(point.X, point.Y);
    }

    private (int X, int Y, int Width, int Height) DraftBounds()
    {
        var a = _desktop.ScreenToIcon(_drawStart);
        var b = _desktop.ScreenToIcon(_latestPoint);
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return (x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private void ApplyDraw()
    {
        _movePending = false;
        if (!_drawing) return;

        var (x, y, width, height) = DraftBounds();
        if (_draft == null)
        {
            if (Math.Abs(_latestPoint.X - _drawStart.X) < DrawThreshold && Math.Abs(_latestPoint.Y - _drawStart.Y) < DrawThreshold)
                return; // not a drag yet
            _draft = new Zone { Title = "새 구역", X = x, Y = y, Width = Math.Max(width, 1), Height = Math.Max(height, 1) };
            _draftSurface = new ZoneSurface(_desktop, _draft);
            return;
        }

        _draft.X = x;
        _draft.Y = y;
        _draft.Width = Math.Max(width, 1);
        _draft.Height = Math.Max(height, 1);
        _draftSurface!.SetBounds(_draft);
    }

    private void EndDraw()
    {
        if (_draft == null)
        {
            // A plain right-click: hand it to explorer after all, tagged so the hook passes it through.
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, ReplayMarker);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTUP, 0, 0, 0, ReplayMarker);
            return;
        }

        var (x, y, width, height) = DraftBounds();
        _draftSurface!.Dispose();
        _draftSurface = null;
        _draft = null;

        var zone = _zones.AddZone(x, y, width, height);
        var title = ZoneMenu.AskTitle(zone.Title);
        if (!string.IsNullOrWhiteSpace(title))
            _zones.RenameZone(zone, title);
    }

    private void EndDrag()
    {
        Log.Write("drag end");
        _session?.Dispose();
        _session = null;
        _zones.ArrangeIcons();
        _zones.SaveLayout();
    }

    public void Dispose()
    {
        NativeMethods.UnhookWindowsHookEx(_hook);
        _session?.Dispose();
        _draftSurface?.Dispose();
        _menu.Dispose();
    }
}
