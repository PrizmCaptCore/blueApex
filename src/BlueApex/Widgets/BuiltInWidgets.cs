using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BlueApex.Sdk;

namespace BlueApex.Widgets;

/// <summary>Shared look for the built-in widgets: a dark rounded panel. Opacity and the actual rounding come from the layer window.</summary>
internal static class WidgetStyle
{
    public const double CornerDip = 14;
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
internal sealed class ClockProvider : IWidgetProvider
{
    public string Id => "clock";
    public string DisplayName => "시계";
    public (int Width, int Height) DefaultSizeDip => (260, 120);
    public IWidget Create(IWidgetContext context) => new ClockWidget();

    private sealed class ClockWidget : IWidget
    {
        private readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 52, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock _date = new() { Foreground = WidgetStyle.Dim, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

        public ClockWidget()
        {
            View = WidgetStyle.Panel(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _time, _date } });
            Tick();
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        public FrameworkElement View { get; }

        private void Tick()
        {
            var now = DateTime.Now;
            _time.Text = now.ToString("HH:mm");
            _date.Text = now.ToString("M월 d일 dddd", CultureInfo.GetCultureInfo("ko-KR"));
        }

        public void Dispose() => _timer.Stop();
    }
}

/// <summary>A sticky note; the text lives in the widget's settings.</summary>
internal sealed class MemoProvider : IWidgetProvider
{
    public string Id => "memo";
    public string DisplayName => "메모";
    public (int Width, int Height) DefaultSizeDip => (260, 200);
    public IWidget Create(IWidgetContext context) => new MemoWidget(context);

    private sealed class MemoWidget : IWidget
    {
        private readonly IWidgetContext _context;
        private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };

        public MemoWidget(IWidgetContext context)
        {
            _context = context;
            var header = new TextBlock { Text = "메모", Foreground = WidgetStyle.Dim, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            View = WidgetStyle.Panel(new DockPanel { Children = { header, _text } });
            Show(context.Settings);
        }

        public FrameworkElement View { get; }

        private void Show(string text) => _text.Text = string.IsNullOrWhiteSpace(text) ? "(클릭해서 메모 편집)" : text;

        public void OnClick() => Edit();

        public IEnumerable<WidgetMenuItem> MenuItems => new[] { new WidgetMenuItem("메모 편집...", Edit) };

        private void Edit()
        {
            var text = _context.AskText("메모", _context.Settings, null, multiline: true);
            if (text == null) return;
            _context.Settings = text.Trim();
            Show(_context.Settings);
        }

        public void Dispose() { }
    }
}

/// <summary>Stands in for a widget whose plugin is not installed, so its spot in the layout is kept.</summary>
internal sealed class MissingProvider : IWidgetProvider
{
    public MissingProvider(string id) => Id = id;
    public string Id { get; }
    public string DisplayName => $"(없는 위젯: {Id})";
    public (int Width, int Height) DefaultSizeDip => (220, 100);

    public IWidget Create(IWidgetContext context) => new MissingWidget(Id);

    private sealed class MissingWidget : IWidget
    {
        public MissingWidget(string id) => View = WidgetStyle.Panel(new TextBlock
        {
            Text = $"위젯 플러그인을 찾을 수 없습니다:\n{id}",
            Foreground = WidgetStyle.Dim,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        public FrameworkElement View { get; }
        public void Dispose() { }
    }
}
