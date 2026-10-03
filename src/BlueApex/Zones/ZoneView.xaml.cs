using System.Windows.Controls;
using System.Windows.Media;

namespace BlueApex.Zones;

public partial class ZoneView : UserControl
{
    public ZoneView()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    internal void Apply(ZoneStyle style)
    {
        Body.Background = Brush(style.Background, ZoneStyle.BuiltIn.Background!);
        Header.Background = Brush(style.HeaderBackground, ZoneStyle.BuiltIn.HeaderBackground!);
        TitleText.Foreground = Brush(style.TitleColor, ZoneStyle.BuiltIn.TitleColor!);
        TitleText.FontSize = style.FontSize ?? ZoneStyle.BuiltIn.FontSize!.Value;
        // Grip follows the title color so it stays visible on light backgrounds.
        Grip.Stroke = Brush(style.TitleColor, ZoneStyle.BuiltIn.TitleColor!);
        Grip.Opacity = 0.5;
    }

    private static SolidColorBrush Brush(string? color, string fallback)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color ?? fallback));
        }
        catch (FormatException)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        }
    }
}
