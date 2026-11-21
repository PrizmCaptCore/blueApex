using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BlueApex.Desktop;
using BlueApex.Ui;

namespace BlueApex.Drawer;

/// <summary>
/// "What is playing and what comes next", popped up over the dock's list button: the current track on top
/// (marked), then the rest in order. Clicking a row goes to that track. Like <see cref="VolumePopup"/> it is a
/// small window of its own (the dock only hears clicks) and closes when the user clicks elsewhere or presses Esc.
/// Where the rows come from is the caller's business: Spotify's queue, or the list of opened files.
/// </summary>
internal static class QueuePopup
{
    /// <param name="Current">The one playing now.</param>
    /// <param name="Pick">Goes to this track; null for a row that cannot be picked.</param>
    internal sealed record Row(string Title, string Subtitle, bool Current, Func<Task>? Pick);

    private const double ListWidth = 320, MaxListHeight = 380, Gap = 8;

    private static Window? _open;

    /// <param name="centerX">Middle of the dock button on screen (DIPs).</param>
    /// <param name="dockTop">The dock's top edge (DIPs): the list sits above it, or below <paramref name="dockBottom"/> when there is no room.</param>
    /// <param name="load">Reads the rows; called again after a pick, since the order then changes.</param>
    public static void Show(double centerX, double dockTop, double dockBottom, string title, Func<Task<List<Row>>> load, string empty)
    {
        _open?.Close();

        var rows = new StackPanel { Width = ListWidth };
        var status = new TextBlock { Foreground = Theme.TextDim, FontSize = Theme.FontSmall, TextWrapping = TextWrapping.Wrap, Width = ListWidth, Margin = new Thickness(0, 0, 0, Theme.Space1) };
        var body = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, Foreground = Theme.Text, FontSize = Theme.FontBody, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, Theme.Space1) },
                status,
                new ScrollViewer { Content = rows, MaxHeight = MaxListHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            },
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
            Content = Parts.Panel(body, corner: Theme.RadiusControl, padding: new Thickness(Theme.Space3)),
        };

        var busy = false;   // a pick is on its way; further clicks wait for it
        var closed = false;

        // "async void": nothing waits for these, so failures are caught here and shown in the status line.
        async void Load()
        {
            status.Text = rows.Children.Count == 0 ? "불러오는 중..." : status.Text;
            try
            {
                var list = await load();
                if (closed) return;
                rows.Children.Clear();
                foreach (var row in list) rows.Children.Add(Draw(row));
                status.Text = list.Count == 0 ? empty : "누르면 그 곡으로 넘어갑니다.";
            }
            catch (SpotifyException ex)
            {
                if (!closed) status.Text = ex.Message;
            }
        }

        async void Pick(Row row)
        {
            if (busy || row.Pick == null) return;
            busy = true;
            status.Text = "넘어가는 중: " + row.Title;
            try
            {
                await row.Pick();
                await Task.Delay(1200); // the player needs a moment before "what comes next" reflects the jump
                if (!closed) Load();
            }
            catch (SpotifyException ex)
            {
                if (!closed) status.Text = ex.Message;
            }
            finally
            {
                busy = false;
            }
        }

        FrameworkElement Draw(Row row)
        {
            var text = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = (row.Current ? "▶  " : "") + row.Title, Foreground = Theme.Text, FontSize = Theme.FontBody,
                        FontWeight = row.Current ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            };
            if (row.Subtitle.Length > 0)
                text.Children.Add(new TextBlock { Text = row.Subtitle, Foreground = Theme.TextDim, FontSize = Theme.FontSmall, TextTrimming = TextTrimming.CharacterEllipsis });
            var rest = row.Current ? Theme.Selection : Brushes.Transparent; // transparent, not null: null would not take clicks
            var border = new Border
            {
                Child = text,
                Background = rest,
                CornerRadius = new CornerRadius(Theme.RadiusControl),
                Padding = new Thickness(Theme.Space2, Theme.Space1, Theme.Space2, Theme.Space1),
                Cursor = row.Pick != null ? Cursors.Hand : Cursors.Arrow,
            };
            if (row.Pick != null)
            {
                border.MouseEnter += (_, _) => border.Background = Theme.Hover;
                border.MouseLeave += (_, _) => border.Background = rest;
                border.MouseLeftButtonUp += (_, _) => Pick(row);
            }
            return border;
        }

        // Its size changes when the rows arrive; each time, keep it centred on the button and resting on the dock.
        void Place()
        {
            var area = SystemParameters.WorkArea;
            window.Left = Math.Clamp(centerX - window.ActualWidth / 2, area.Left, Math.Max(area.Left, area.Right - window.ActualWidth));
            var above = dockTop - window.ActualHeight - Gap;
            window.Top = above >= area.Top ? above : dockBottom + Gap;
        }
        window.SizeChanged += (_, _) => Place();
        window.Loaded += (_, _) => Place();
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
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
            closed = true;
            if (_open == window) _open = null;
        };

        _open = window;
        window.Show();
        // Without really taking the foreground, "clicked elsewhere" would never be noticed and it would stay up.
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(window).Handle);
        window.Activate();
        Load();
    }
}
