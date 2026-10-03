using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BlueApex.Desktop;

namespace BlueApex.Drawer;

/// <summary>
/// The round "open the drawer" button on the desktop, for mouse- and touch-only
/// use. It sits below the icons like the old zones did, so a low-level mouse hook
/// turns a click on it into opening the drawer and a drag into moving it.
/// </summary>
internal sealed class DesktopButton : IDisposable
{
    private const double SizeDip = 56;
    private const int DragThreshold = 10; // physical px

    private readonly DesktopHost _desktop;
    private readonly DrawerManager _drawer;
    private readonly Action _open;
    private readonly Dispatcher _dispatcher;
    private readonly DesktopLayerWindow _window;
    private readonly int _size;
    private readonly NativeMethods.LowLevelMouseProc _proc; // kept alive for the hook's lifetime
    private readonly IntPtr _hook;

    private int _x, _y;
    private bool _pressed, _dragging;
    private NativeMethods.POINT _pressPoint;
    private (int X, int Y) _pressOrigin;

    public DesktopButton(DesktopHost desktop, DrawerManager drawer, Action open)
    {
        _desktop = desktop;
        _drawer = drawer;
        _open = open;
        _dispatcher = Dispatcher.CurrentDispatcher;
        var scale = desktop.Dpi / 96.0;
        _size = (int)Math.Round(SizeDip * scale);

        (_x, _y) = drawer.ButtonPosition ?? DefaultPosition(scale);
        Clamp();
        _window = new DesktopLayerWindow(desktop, BuildFace(), _x, _y, _size, _size, 0.9, _size / 2);
        Reserve();

        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "마우스 훅을 설치하지 못했습니다.");
    }

    // Bottom centre, just above the taskbar, like a phone's home button.
    private (int X, int Y) DefaultPosition(double scale)
    {
        var (areaWidth, _) = _desktop.IconAreaSize;
        var workBottom = (int)Math.Round(SystemParameters.WorkArea.Bottom * scale);
        return ((areaWidth - _size) / 2, workBottom - _size - (int)Math.Round(20 * scale));
    }

    private void Clamp()
    {
        var (areaWidth, areaHeight) = _desktop.IconAreaSize;
        _x = Math.Clamp(_x, 0, Math.Max(0, areaWidth - _size));
        _y = Math.Clamp(_y, 0, Math.Max(0, areaHeight - _size));
    }

    private void Reserve() =>
        _drawer.Reserved = new NativeMethods.RECT { Left = _x, Top = _y, Right = _x + _size, Bottom = _y + _size };

    // A dark disc with a 3x3 grid of dots: the usual "all apps" glyph.
    private static FrameworkElement BuildFace()
    {
        var dots = new UniformGrid { Rows = 3, Columns = 3, Width = 26, Height = 26 };
        for (var i = 0; i < 9; i++)
            dots.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(SizeDip / 2),
            Child = dots,
        };
    }

    public bool Visible
    {
        set => _window.Visible = value;
    }

    private bool Contains(NativeMethods.POINT screen)
    {
        if (NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(screen), NativeMethods.GA_ROOT) != _desktop.Progman)
            return false; // another window is in front of the desktop here
        var p = _desktop.ScreenToIcon(screen);
        return p.X >= _x && p.X < _x + _size && p.Y >= _y && p.Y < _y + _size;
    }

    // Only hit-testing and state here; see ZoneMouseInteraction for why nothing heavier may run in a hook.
    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var message = (uint)wParam;
                var point = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam).Point;

                if (!_pressed)
                {
                    if (message == NativeMethods.WM_LBUTTONDOWN && Contains(point))
                    {
                        _pressed = true;
                        _dragging = false;
                        _pressPoint = point;
                        _pressOrigin = (_x, _y);
                        return 1; // swallowed: explorer never sees it
                    }
                }
                else if (message == NativeMethods.WM_MOUSEMOVE)
                {
                    var dx = point.X - _pressPoint.X;
                    var dy = point.Y - _pressPoint.Y;
                    if (_dragging || Math.Abs(dx) >= DragThreshold || Math.Abs(dy) >= DragThreshold)
                    {
                        _dragging = true;
                        _x = _pressOrigin.X + dx;
                        _y = _pressOrigin.Y + dy;
                        Clamp();
                        _window.SetBounds(_x, _y, _size, _size);
                    }
                }
                else if (message == NativeMethods.WM_LBUTTONUP)
                {
                    _pressed = false;
                    if (_dragging)
                    {
                        _dragging = false;
                        Reserve();
                        _dispatcher.BeginInvoke(() => _drawer.ButtonPosition = (_x, _y));
                    }
                    else
                    {
                        _dispatcher.BeginInvoke(_open);
                    }
                    return 1;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"button hook error: {ex}");
            _pressed = false;
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        NativeMethods.UnhookWindowsHookEx(_hook);
        _window.Dispose();
    }
}
