using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlueApex.Ui;

/// <summary>
/// The app's reusable visual pieces, each built from <see cref="Theme"/> tokens.
/// Screens compose these instead of styling elements by hand, so a piece can be
/// redesigned in one place: change <see cref="Card"/> and every card changes.
/// </summary>
internal static class Parts
{
    /// <summary>A rounded translucent card with a title row; <paramref name="headerRight"/> goes at the row's right end.</summary>
    public static (Border Card, StackPanel Body, TextBlock Title) Card(string title, UIElement? headerRight = null, bool alternate = false)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = Theme.Text,
            FontSize = Theme.FontTitle,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(Theme.Space1 + 2, 0, Theme.Space1 + 2, Theme.Space2 + 2),
        };
        var row = new DockPanel();
        if (headerRight != null)
        {
            DockPanel.SetDock(headerRight, Dock.Right);
            row.Children.Add(headerRight);
        }
        row.Children.Add(titleBlock);
        var body = new StackPanel { Children = { row } };
        var card = new Border
        {
            Width = Theme.CardWidth,
            MinHeight = 140,
            Margin = new Thickness(Theme.Space3),
            Padding = new Thickness(Theme.Space3),
            CornerRadius = new CornerRadius(Theme.RadiusCard),
            Background = alternate ? Theme.CardAlt : Theme.Card,
            Child = body,
        };
        return (card, body, titleBlock);
    }

    /// <summary>An icon tile: image on top, name below, optional accent dot badge.</summary>
    public static (Border Tile, Image Image) Tile(string name, bool badge, string? tooltip = null)
    {
        var image = new Image { Width = Theme.IconSize, Height = Theme.IconSize, Margin = new Thickness(0, 6, 0, 4) };
        var top = new Grid { Children = { image } };
        if (badge)
            top.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = Theme.Accent,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 8, 0),
            });
        var label = new TextBlock
        {
            Text = name,
            Foreground = Theme.Text,
            FontSize = Theme.FontSmall,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
        };
        var tile = new Border
        {
            Width = Theme.TileWidth,
            Height = Theme.TileHeight,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(Theme.RadiusTile),
            Background = Brushes.Transparent,
            Child = new StackPanel { Children = { top, label } },
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tooltip ?? name,
        };
        return (tile, image);
    }

    /// <summary>A collapsible group's header bar: "▸ A (12)".</summary>
    public static (Border Bar, TextBlock Arrow) GroupHeader(string text, bool open)
    {
        var arrow = new TextBlock { Foreground = Theme.TextDim, FontSize = Theme.FontSmall, Width = 16, Text = open ? "▾" : "▸" };
        var label = new TextBlock { Foreground = Theme.Text, FontSize = Theme.FontBody, FontWeight = FontWeights.SemiBold, Text = text };
        var bar = new Border
        {
            Background = Theme.GroupBar,
            CornerRadius = new CornerRadius(Theme.RadiusControl),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(Theme.Space1, Theme.Space1, Theme.Space1, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = "control",
            Child = new DockPanel { Children = { arrow, label } },
        };
        return (bar, arrow);
    }

    /// <summary>A small inline button ("더 보기", "수동 ▾"). Colours come from the implicit Button style.</summary>
    public static Button SmallButton(string text) => new()
    {
        Content = text,
        FontSize = Theme.FontSmall,
        Padding = new Thickness(10, 4, 10, 4),
        Margin = new Thickness(Theme.Space1 + 2, Theme.Space2, Theme.Space1 + 2, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        Tag = "control",
    };

    /// <summary>A roomy call-to-action button ("+ 새 구역").</summary>
    public static Button BigButton(string text) => new()
    {
        Content = text,
        FontSize = Theme.FontAction,
        Padding = new Thickness(18, 10, 18, 10),
    };

    /// <summary>A big labelled pill used as a drop target; the colour says what it does.</summary>
    public static Border Pill(string label, Color color)
    {
        var brush = new SolidColorBrush(Theme.WithAlpha(color, 0xC0));
        brush.Freeze();
        return new Border
        {
            Padding = new Thickness(28, 14, 28, 14),
            Margin = new Thickness(Theme.Space3, 0, Theme.Space3, 0),
            CornerRadius = new CornerRadius(Theme.RadiusPill),
            Background = brush,
            AllowDrop = true,
            Child = new TextBlock { Text = label, Foreground = Theme.Text, FontSize = Theme.FontAction, FontWeight = FontWeights.SemiBold },
        };
    }

    /// <summary>An opaque rounded panel for the desktop layer (widgets, button face), with the app's hairline.</summary>
    public static Border Panel(UIElement child, double corner = Theme.RadiusCard, Thickness? padding = null) => new()
    {
        Background = Theme.Panel,
        BorderBrush = Theme.Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(corner),
        Padding = padding ?? new Thickness(Theme.Space4, Theme.Space3, Theme.Space4, Theme.Space3),
        Child = child,
    };

    /// <summary>A small modal dialog window with the app's look; put content in and call ShowDialog.</summary>
    public static Window Dialog(string title, UIElement content)
    {
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            Content = content,
        };
        Theme.ApplyWindow(window);
        return window;
    }

    /// <summary>The "확인 / 취소" row for dialogs.</summary>
    public static (StackPanel Row, Button Ok, Button Cancel) DialogButtons()
    {
        var ok = new Button { Content = "확인", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, Theme.Space2, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Width = 80 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        return (row, ok, cancel);
    }
}
