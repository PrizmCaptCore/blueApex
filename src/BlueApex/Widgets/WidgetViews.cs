using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BlueApex.Widgets;

/// <summary>Shared look: a dark rounded panel. Opacity and the actual rounding come from the layer window.</summary>
internal static class WidgetStyle
{
    public static readonly double CornerDip = 14;
    public static readonly Brush Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24));
    public static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

    public static Border Panel(UIElement child) => new()
    {
        Background = Background,
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(CornerDip),
        Padding = new Thickness(16, 12, 16, 12),
        Child = child,
    };
}

/// <summary>Time and date, refreshed every second.</summary>
internal sealed class ClockView : UserControl
{
    public static readonly (int Width, int Height) DefaultSizeDip = (260, 120);

    private readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 52, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _date = new() { Foreground = WidgetStyle.Dim, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ClockView()
    {
        Content = WidgetStyle.Panel(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _time, _date },
        });
        Tick();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString("HH:mm");
        _date.Text = now.ToString("M월 d일 dddd", CultureInfo.GetCultureInfo("ko-KR"));
    }
}

/// <summary>A sticky note. Edited through the widget's right-click menu, since the layer gets no keyboard input.</summary>
internal sealed class MemoView : UserControl
{
    public static readonly (int Width, int Height) DefaultSizeDip = (260, 200);

    private readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public MemoView()
    {
        Content = WidgetStyle.Panel(new DockPanel
        {
            Children =
            {
                Dock(new TextBlock { Text = "메모", Foreground = WidgetStyle.Dim, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) }),
                _text,
            },
        });
    }

    private static UIElement Dock(UIElement element)
    {
        DockPanel.SetDock(element, System.Windows.Controls.Dock.Top);
        return element;
    }

    public string Text
    {
        get => _text.Text;
        set => _text.Text = string.IsNullOrWhiteSpace(value) ? "(우클릭 → 메모 편집)" : value;
    }
}
