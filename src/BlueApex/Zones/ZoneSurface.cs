using System.Windows.Interop;
using BlueApex.Desktop;

namespace BlueApex.Zones;

/// <summary>
/// One zone's background: a translucent child window of the desktop, sitting
/// below the icons. It never receives mouse input (the icon view covers it), so
/// it only draws.
///
/// Two windows are involved:
///   frame   - plain Win32 layered child of Progman; owns alpha, shape, position
///   content - WPF HwndSource, child of the frame; draws the zone
///
/// Why not a single WPF window (all verified on build 26300):
///  - Progman has no redirection surface, so only WS_EX_LAYERED children are composited.
///  - Per-pixel alpha (UpdateLayeredWindow / UsesPerPixelTransparency) is not
///    composited there either; only whole-window alpha (LWA_ALPHA) is.
///  - HwndSource manages WS_EX_LAYERED itself and strips it when set by hand.
/// </summary>
internal sealed class ZoneSurface : IDisposable
{
    /// <summary>Height of the title bar, in device-independent pixels (must match ZoneView.xaml).</summary>
    public const double HeaderHeightDip = 30;

    /// <summary>Side of the resize grip square in the bottom-right corner, in DIPs (must match ZoneView.xaml).</summary>
    public const double GripSizeDip = 20;

    private readonly DesktopHost _desktop;
    private readonly System.Windows.Forms.NativeWindow _frame = new();
    private readonly HwndSource _content;
    private readonly ZoneView _view;
    private int _cornerRadius = ZoneStyle.BuiltIn.CornerRadius!.Value;
    private (int Width, int Height) _size;

    /// <param name="style">A fully resolved style (no nulls); see <see cref="ZoneStyle.Over"/>.</param>
    public ZoneSurface(DesktopHost desktop, Zone zone, ZoneStyle style)
    {
        _desktop = desktop;
        var origin = desktop.IconToHost(zone.X, ShownTop(zone));
        var height = ShownHeight(zone);
        _frame.CreateHandle(new System.Windows.Forms.CreateParams
        {
            Caption = "Zone",
            Parent = desktop.Progman,
            Style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS |
                    NativeMethods.WS_CLIPCHILDREN,
            ExStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE,
            X = origin.X,
            Y = origin.Y,
            Width = zone.Width,
            Height = height,
        });
        _size = (zone.Width, height);

        _content = new HwndSource(new HwndSourceParameters("ZoneContent")
        {
            ParentWindow = _frame.Handle,
            WindowStyle = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
            PositionX = 0,
            PositionY = 0,
            Width = zone.Width,
            Height = height,
        });
        // Software rendering is the path verified to show up inside the layered
        // frame; the content is static, so it costs nothing.
        _content.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        _view = new ZoneView { Title = zone.Title, HeaderAtBottom = zone.HeaderAtBottom };
        _content.RootVisual = _view;
        ApplyStyle(style);

        desktop.PlaceBelowIcons(_frame.Handle);
    }

    /// <summary>Applies colors, font size, whole-window opacity and corner radius.</summary>
    public void ApplyStyle(ZoneStyle style)
    {
        var opacity = Math.Clamp(style.Opacity ?? ZoneStyle.BuiltIn.Opacity!.Value, 0.05, 1);
        NativeMethods.SetLayeredWindowAttributes(_frame.Handle, 0, (byte)Math.Round(opacity * 255), NativeMethods.LWA_ALPHA);
        _cornerRadius = Math.Clamp(style.CornerRadius ?? ZoneStyle.BuiltIn.CornerRadius!.Value, 0, 60);
        ApplyShape(_size.Width, _size.Height);
        _view.Apply(style);
    }

    public IntPtr Handle => _frame.Handle;

    public string Title
    {
        set => _view.Title = value;
    }

    public bool Visible
    {
        set => NativeMethods.ShowWindow(_frame.Handle, value ? NativeMethods.SW_SHOWNA : NativeMethods.SW_HIDE);
    }

    /// <summary>Applies the zone position and size; a rolled-up zone shows only its title bar.</summary>
    public void SetBounds(Zone zone)
    {
        var origin = _desktop.IconToHost(zone.X, ShownTop(zone));
        var height = ShownHeight(zone);
        _size = (zone.Width, height);
        _view.HeaderAtBottom = zone.HeaderAtBottom;
        NativeMethods.SetWindowPos(_frame.Handle, IntPtr.Zero, origin.X, origin.Y, zone.Width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        ApplyShape(zone.Width, height);
        NativeMethods.SetWindowPos(_content.Handle, IntPtr.Zero, 0, 0, zone.Width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private int HeaderPixels => (int)Math.Round(HeaderHeightDip * _desktop.Dpi / 96.0);

    private int ShownHeight(Zone zone) => zone.Rolled ? HeaderPixels : zone.Height;

    // A rolled zone shows only its title bar, which sits at the box bottom when HeaderAtBottom.
    private int ShownTop(Zone zone) => zone.Rolled && zone.HeaderAtBottom ? zone.Y + zone.Height - HeaderPixels : zone.Y;

    // Rounded corners have to come from a window region, since alpha is per-window.
    // The system owns the region after SetWindowRgn, so it is not deleted here.
    private void ApplyShape(int width, int height)
    {
        var region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, _cornerRadius * 2, _cornerRadius * 2);
        NativeMethods.SetWindowRgn(_frame.Handle, region, true);
    }

    public void Dispose()
    {
        _content.Dispose();
        _frame.DestroyHandle();
    }
}
