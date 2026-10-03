using System.Windows;
using System.Windows.Interop;

namespace BlueApex.Desktop;

/// <summary>
/// A window that lives on the desktop below the icons and shows WPF content.
///
/// Two windows are involved: a plain Win32 layered child of Progman that owns
/// alpha, shape and position, and a WPF HwndSource inside it that draws. This is
/// the only arrangement the desktop composites (verified on build 26300): only
/// WS_EX_LAYERED children of Progman show up, only whole-window alpha works there,
/// and HwndSource strips WS_EX_LAYERED if asked to carry it itself. The window
/// never receives mouse input (the icon view covers it); a low-level mouse hook
/// decides what a click on it means.
/// </summary>
internal sealed class DesktopLayerWindow : IDisposable
{
    private readonly DesktopHost _desktop;
    private readonly System.Windows.Forms.NativeWindow _frame = new();
    private readonly HwndSource _content;
    private int _cornerRadius;
    private (int Width, int Height) _size;

    /// <param name="x">Icon-view coordinates, physical pixels.</param>
    public DesktopLayerWindow(DesktopHost desktop, FrameworkElement content, int x, int y, int width, int height, double opacity, int cornerRadius)
    {
        _desktop = desktop;
        _cornerRadius = cornerRadius;
        var origin = desktop.IconToHost(x, y);
        _frame.CreateHandle(new System.Windows.Forms.CreateParams
        {
            Caption = "BlueApexLayer",
            Parent = desktop.Progman,
            Style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN,
            ExStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE,
            X = origin.X,
            Y = origin.Y,
            Width = width,
            Height = height,
        });
        _size = (width, height);
        Opacity = opacity;
        ApplyShape();

        _content = new HwndSource(new HwndSourceParameters("BlueApexLayerContent")
        {
            ParentWindow = _frame.Handle,
            WindowStyle = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
            PositionX = 0,
            PositionY = 0,
            Width = width,
            Height = height,
        });
        _content.CompositionTarget.RenderMode = RenderMode.SoftwareOnly; // the path verified to render inside the layered frame
        _content.RootVisual = content;

        desktop.PlaceBelowIcons(_frame.Handle);
    }

    public IntPtr Handle => _frame.Handle;

    public double Opacity
    {
        set => NativeMethods.SetLayeredWindowAttributes(_frame.Handle, 0, (byte)Math.Round(Math.Clamp(value, 0.05, 1) * 255), NativeMethods.LWA_ALPHA);
    }

    public bool Visible
    {
        set => NativeMethods.ShowWindow(_frame.Handle, value ? NativeMethods.SW_SHOWNA : NativeMethods.SW_HIDE);
    }

    public void SetBounds(int x, int y, int width, int height)
    {
        var origin = _desktop.IconToHost(x, y);
        _size = (width, height);
        NativeMethods.SetWindowPos(_frame.Handle, IntPtr.Zero, origin.X, origin.Y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        ApplyShape();
        NativeMethods.SetWindowPos(_content.Handle, IntPtr.Zero, 0, 0, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    // Rounded corners come from a window region, since alpha is per-window.
    // The system owns the region after SetWindowRgn, so it is not deleted here.
    private void ApplyShape()
    {
        var region = NativeMethods.CreateRoundRectRgn(0, 0, _size.Width + 1, _size.Height + 1, _cornerRadius * 2, _cornerRadius * 2);
        NativeMethods.SetWindowRgn(_frame.Handle, region, true);
    }

    public void Dispose()
    {
        _content.Dispose();
        _frame.DestroyHandle();
    }
}
