using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using BlueApex.Desktop;

namespace BlueApex.Drawer;

/// <summary>
/// The round "open the drawer" button on the desktop, for mouse- and touch-only
/// use. Drawn on the desktop layer; <see cref="DesktopLayerInput"/> delivers its
/// clicks and drags.
/// </summary>
internal sealed class DesktopButton : IDesktopLayerItem, IDisposable
{
    private const double SizeDip = 56;

    private readonly DesktopHost _desktop;
    private readonly DrawerManager _drawer;
    private readonly Action _open;
    private readonly DesktopLayerWindow _window;
    private readonly int _size;
    private int _x, _y;

    public DesktopButton(DesktopHost desktop, DrawerManager drawer, Action open)
    {
        _desktop = desktop;
        _drawer = drawer;
        _open = open;
        var scale = desktop.Dpi / 96.0;
        _size = (int)Math.Round(SizeDip * scale);

        (_x, _y) = drawer.ButtonPosition ?? DefaultPosition(scale);
        var (areaWidth, areaHeight) = desktop.IconAreaSize;
        _x = Math.Clamp(_x, 0, Math.Max(0, areaWidth - _size));
        _y = Math.Clamp(_y, 0, Math.Max(0, areaHeight - _size));
        _window = new DesktopLayerWindow(desktop, BuildFace(), _x, _y, _size, _size, 0.9, _size / 2);
    }

    // Bottom centre, just above the taskbar, like a phone's home button.
    private (int X, int Y) DefaultPosition(double scale)
    {
        var (areaWidth, _) = _desktop.IconAreaSize;
        var workBottom = (int)Math.Round(SystemParameters.WorkArea.Bottom * scale);
        return ((areaWidth - _size) / 2, workBottom - _size - (int)Math.Round(20 * scale));
    }

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

    public NativeMethods.RECT Bounds => new() { Left = _x, Top = _y, Right = _x + _size, Bottom = _y + _size };
    public bool Movable => true;

    public void MoveTo(int x, int y)
    {
        _x = x;
        _y = y;
        _window.SetBounds(_x, _y, _size, _size);
    }

    public void Moved() => _drawer.ButtonPosition = (_x, _y);
    public void Click() => _open();

    public void RightClick(int screenX, int screenY) =>
        DesktopPopupMenu.Show(screenX, screenY,
            ("서랍 열기", _open),
            ("버튼 위치 초기화", () =>
            {
                var (x, y) = DefaultPosition(_desktop.Dpi / 96.0);
                MoveTo(x, y);
                _drawer.ButtonPosition = null;
            }));

    public void Dispose() => _window.Dispose();
}
