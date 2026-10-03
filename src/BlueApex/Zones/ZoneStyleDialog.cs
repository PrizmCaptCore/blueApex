using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlueApex.Zones;

/// <summary>
/// "구역 꾸미기": opacity, colors, font size and corner radius, previewed live
/// on the zone while editing. Can save for this zone only or as the default
/// for every zone without a style of its own.
/// </summary>
internal static class ZoneStyleDialog
{
    public static void Show(ZoneManager zones, Zone zone)
    {
        var style = zones.EffectiveStyle(zone).Clone();
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        void Preview() => zones.PreviewStyle(zone, style);

        AddSlider(grid, "투명도", (style.Opacity ?? 0.7) * 100, 10, 100, v => style.Opacity = v / 100, Preview, v => $"{v:0}%");
        AddColor(grid, "배경색", () => style.Background!, c => style.Background = c, Preview);
        AddColor(grid, "제목 배경색", () => style.HeaderBackground!, c => style.HeaderBackground = c, Preview);
        AddColor(grid, "제목 글자색", () => style.TitleColor!, c => style.TitleColor = c, Preview);
        AddSlider(grid, "글자 크기", style.FontSize ?? 13, 9, 28, v => style.FontSize = Math.Round(v), Preview, v => $"{v:0}");
        AddSlider(grid, "모서리 둥글기", style.CornerRadius ?? 12, 0, 40, v => style.CornerRadius = (int)Math.Round(v), Preview, v => $"{v:0}");

        var asDefault = new CheckBox { Content = "모든 구역의 기본값으로 저장", Margin = new Thickness(0, 12, 0, 0), IsChecked = zone.Style == null };
        var reset = new Button { Content = "기본값으로", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "확인", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Width = 80 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { reset, ok, cancel },
        };

        var window = new Window
        {
            Title = "구역 꾸미기: " + zone.Title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            Content = new StackPanel { Children = { grid, new StackPanel { Margin = new Thickness(16, 0, 16, 16), Children = { asDefault, buttons } } } },
        };

        var result = false;
        ok.Click += (_, _) =>
        {
            zones.SetStyle(zone, style, asDefault.IsChecked == true);
            result = true;
            window.Close();
        };
        reset.Click += (_, _) =>
        {
            // Back to the plain built-in look for this zone (or for all, if saving as default).
            zones.SetStyle(zone, null, asDefault.IsChecked == true);
            result = true;
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            if (!result) zones.SetStyle(zone, zone.Style, false); // undo the preview
        };
        window.ShowDialog();
    }

    private static void AddSlider(Grid grid, string label, double value, double min, double max,
        Action<double> set, Action preview, Func<double, string> format)
    {
        var row = AddRow(grid, label);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = format(value), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            text.Text = format(e.NewValue);
            preview();
        };
        Place(grid, row, slider, text);
    }

    private static void AddColor(Grid grid, string label, Func<string> get, Action<string> set, Action preview)
    {
        var row = AddRow(grid, label);
        var button = new Button { Height = 26, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Paint() => button.Content = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(get())),
            Height = 16,
            Margin = new Thickness(2),
            Child = new TextBlock { Text = get(), HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.Gray },
        };
        Paint();
        button.Click += (_, _) =>
        {
            var current = System.Drawing.ColorTranslator.FromHtml(get());
            using var dialog = new System.Windows.Forms.ColorDialog { Color = current, FullOpen = true };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            set($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
            Paint();
            preview();
        };
        Place(grid, row, button, null);
    }

    private static int AddRow(Grid grid, string label)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = grid.RowDefinitions.Count - 1;
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        return row;
    }

    private static void Place(Grid grid, int row, UIElement control, UIElement? trailing)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        if (trailing == null) return;
        Grid.SetRow(trailing, row);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);
    }
}
