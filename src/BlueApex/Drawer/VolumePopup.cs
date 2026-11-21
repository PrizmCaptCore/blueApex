using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using BlueApex.Desktop;
using BlueApex.Ui;

namespace BlueApex.Drawer;

/// <summary>
/// The little volume bar that pops up over the dock's speaker button: speaker (click = mute) | gauge | percent.
/// Drag or click the gauge, or roll the mouse wheel anywhere on it. It closes when the user clicks elsewhere
/// (or presses Esc). The dock cannot hold a draggable bar itself (it only hears clicks), hence a window of its own.
/// </summary>
internal static class VolumePopup
{
    private const double BarWidth = 180, BarHeight = 6, Knob = 14, RowHeight = 28, Gap = 8;
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private static Window? _open;

    /// <param name="centerX">Where the speaker button's middle is on screen (DIPs).</param>
    /// <param name="dockTop">The dock's top edge (DIPs); the bar sits above it...</param>
    /// <param name="dockBottom">...or below this edge when there is no room above.</param>
    /// <param name="save">Called with the volume when the user lets go, so it is written to disk once, not per pixel.</param>
    /// <param name="mutedChanged">Called when mute flips, so the dock can redraw its speaker glyph.</param>
    public static void Show(double centerX, double dockTop, double dockBottom, BackgroundPlayer player, Action<double> save, Action mutedChanged)
    {
        _open?.Close();

        var track = new Border { Width = BarWidth, Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2), Background = Theme.ButtonFill };
        var fill = new Border { Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2), Background = Theme.Text };
        var knob = new Ellipse { Width = Knob, Height = Knob, Fill = Theme.Text };
        Canvas.SetTop(track, (RowHeight - BarHeight) / 2);
        Canvas.SetTop(fill, (RowHeight - BarHeight) / 2);
        Canvas.SetTop(knob, (RowHeight - Knob) / 2);
        // Transparent, not null: a null background would only take clicks on the thin bar itself.
        var bar = new Canvas { Width = BarWidth, Height = RowHeight, Background = Brushes.Transparent, Cursor = Cursors.Hand, Children = { track, fill, knob } };
        var speaker = new TextBlock
        {
            FontFamily = new FontFamily(IconFont), FontSize = Theme.FontTitle, Foreground = Theme.Text, Width = RowHeight,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, Theme.Space1, 0),
        };
        var percent = new TextBlock
        {
            FontSize = Theme.FontSmall, Foreground = Theme.TextDim, Width = 44,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { speaker, bar, percent } };

        var wasMuted = player.Muted;
        void Update()
        {
            var v = player.Muted ? 0 : player.Volume;
            fill.Width = Knob / 2 + (BarWidth - Knob) * v; // ends under the knob's middle
            Canvas.SetLeft(knob, (BarWidth - Knob) * v);
            percent.Text = player.Muted ? "음소거" : $"{v * 100:0}%";
            speaker.Text = player.Muted || v == 0 ? "" : ""; // crossed-out speaker / speaker
            if (player.Muted == wasMuted) return;
            wasMuted = player.Muted;
            mutedChanged();
        }
        void Set(double v)
        {
            player.Muted = false; // moving the bar means "I want sound"
            player.Volume = Math.Clamp(v, 0, 1);
            Update();
        }
        // The knob's middle follows the mouse, so the ends of the bar are 0% and 100% exactly.
        double At(MouseEventArgs e) => (e.GetPosition(bar).X - Knob / 2) / (BarWidth - Knob);

        bar.MouseLeftButtonDown += (_, e) =>
        {
            bar.CaptureMouse(); // keep following the mouse even when it leaves the bar while dragging
            Set(At(e));
        };
        bar.MouseMove += (_, e) =>
        {
            if (bar.IsMouseCaptured) Set(At(e));
        };
        bar.MouseLeftButtonUp += (_, _) =>
        {
            bar.ReleaseMouseCapture();
            save(player.Volume);
        };
        speaker.MouseLeftButtonUp += (_, _) =>
        {
            player.Muted = !player.Muted;
            Update();
        };

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true, // lets the rounded panel be the whole visible shape
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = Theme.Font,
            Content = Parts.Panel(row, corner: Theme.RadiusControl, padding: new Thickness(Theme.Space2, Theme.Space1, Theme.Space3, Theme.Space1)),
        };
        window.MouseWheel += (_, e) =>
        {
            Set(player.Volume + (e.Delta > 0 ? 0.05 : -0.05));
            save(player.Volume);
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
        // Its size is only known once laid out; then centre it on the button, above the dock (below if no room).
        window.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            window.Left = Math.Clamp(centerX - window.ActualWidth / 2, area.Left, Math.Max(area.Left, area.Right - window.ActualWidth));
            var above = dockTop - window.ActualHeight - Gap;
            window.Top = above >= area.Top ? above : dockBottom + Gap;
        };
        // Clicking anywhere else closes it. Close() inside a close already under way would throw, hence the flag.
        var closing = false;
        window.Closing += (_, _) => closing = true;
        window.Deactivated += (_, _) =>
        {
            if (!closing) window.Close();
        };
        window.Closed += (_, _) =>
        {
            if (_open == window) _open = null;
        };

        Update();
        _open = window;
        window.Show();
        // Without really taking the foreground, "clicked elsewhere" would never be noticed and it would stay up.
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(window).Handle);
        window.Activate();
    }
}
