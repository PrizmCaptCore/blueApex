using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BlueApex.Sdk;

namespace WeatherWidget;

/// <summary>
/// Current temperature and sky for a latitude/longitude, from Open-Meteo (no API
/// key). Shows how a plugin keeps settings, uses the dispatcher for network
/// results, and adds a right-click menu entry.
/// </summary>
public sealed class WeatherProvider : IWidgetProvider
{
    public string Id => "weather";
    public string DisplayName => "날씨";
    public (int Width, int Height) DefaultSizeDip => (260, 130);
    public IWidget Create(IWidgetContext context) => new WeatherWidget(context);
}

internal sealed class WeatherWidget : IWidget
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string DefaultLocation = "37.57,126.98"; // Seoul

    private readonly IWidgetContext _context;
    private readonly TextBlock _temperature = new() { Foreground = Brushes.White, FontSize = 44, FontWeight = FontWeights.Light };
    private readonly TextBlock _sky = new() { Foreground = Brushes.White, FontSize = 15, Opacity = 0.85 };
    private readonly TextBlock _place = new() { Foreground = Brushes.White, FontSize = 12, Opacity = 0.6 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private CancellationTokenSource _cancel = new();

    public WeatherWidget(IWidgetContext context)
    {
        _context = context;
        if (string.IsNullOrWhiteSpace(_context.Settings)) _context.Settings = DefaultLocation;

        View = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _temperature, _sky, _place },
            },
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public FrameworkElement View { get; }

    public IEnumerable<WidgetMenuItem> MenuItems => new[]
    {
        new WidgetMenuItem("위치 설정 (위도,경도)...", () =>
        {
            var text = _context.AskText("날씨 위치", _context.Settings, "위도,경도 형식. 예: 37.57,126.98 (서울)");
            if (string.IsNullOrWhiteSpace(text)) return;
            _context.Settings = text.Trim();
            Refresh();
        }),
        new WidgetMenuItem("지금 새로 고침", Refresh),
    };

    public void OnClick() => Refresh();

    private async void Refresh()
    {
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;

        var parts = _context.Settings.Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            Show("--", "위치 설정이 잘못되었습니다", _context.Settings);
            return;
        }

        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code&timezone=auto");
            using var json = JsonDocument.Parse(await Http.GetStringAsync(url, token));
            var current = json.RootElement.GetProperty("current");
            var temperature = current.GetProperty("temperature_2m").GetDouble();
            var code = current.GetProperty("weather_code").GetInt32();
            Show($"{temperature:0.#}°", Describe(code), $"{lat:0.##}, {lon:0.##} · {DateTime.Now:HH:mm}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException)
        {
            _context.Log($"weather fetch failed: {ex.Message}");
            Show("--", "날씨를 가져오지 못했습니다", "클릭하면 다시 시도");
        }
    }

    // Open-Meteo WMO weather codes, roughly grouped.
    private static string Describe(int code) => code switch
    {
        0 => "맑음",
        1 or 2 => "대체로 맑음",
        3 => "흐림",
        45 or 48 => "안개",
        >= 51 and <= 57 => "이슬비",
        >= 61 and <= 67 => "비",
        >= 71 and <= 77 => "눈",
        >= 80 and <= 82 => "소나기",
        85 or 86 => "눈 소나기",
        >= 95 => "뇌우",
        _ => "알 수 없음",
    };

    private void Show(string temperature, string sky, string place) => _context.Dispatcher.Invoke(() =>
    {
        _temperature.Text = temperature;
        _sky.Text = sky;
        _place.Text = place;
    });

    public void Dispose()
    {
        _timer.Stop();
        _cancel.Cancel();
    }
}
