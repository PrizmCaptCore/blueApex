using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace BlueApex.Ui;

/// <summary>
/// The design tokens every screen draws from: surfaces, text, accent, radii,
/// spacing, type sizes. Change a value here and it changes everywhere; nothing
/// else in the app is allowed to spell out a colour or a size of its own.
///
/// Direction: Fluent dark (Windows 11) with the launcher's rounder, roomier grid.
/// Everything sits on the wallpaper, so surfaces are dark and translucent and
/// text is white; the accent follows the Windows accent colour.
/// </summary>
internal static class Theme
{
    // --- colour tokens ---

    /// <summary>Full-screen dim behind the drawer.</summary>
    public static readonly Brush Scrim = Frozen(0xD8, 0x10, 0x10, 0x14);

    /// <summary>A card / panel on the drawer or the desktop layer.</summary>
    public static readonly Brush Card = Frozen(0xC8, 0x1E, 0x1E, 0x24);

    /// <summary>A card of a different kind (the all-apps card), slightly bluer.</summary>
    public static readonly Brush CardAlt = Frozen(0xC8, 0x1E, 0x24, 0x2E);

    /// <summary>A card that is a valid drop target under the cursor.</summary>
    public static readonly Brush CardHover = Frozen(0xE0, 0x2E, 0x3A, 0x50);

    /// <summary>Opaque panel for the desktop layer (which cannot blend per pixel): widgets, the drawer button.</summary>
    public static readonly Brush Panel = Frozen(0xFF, 0x1E, 0x1E, 0x24);

    /// <summary>Text input background.</summary>
    public static readonly Brush Input = Frozen(0xFF, 0x2A, 0x2A, 0x30);

    /// <summary>Hairline around panels and inputs.</summary>
    public static readonly Brush Border = Frozen(0x40, 0xFF, 0xFF, 0xFF);

    /// <summary>Row-style control at rest (group header bars).</summary>
    public static readonly Brush GroupBar = Frozen(0x28, 0xFF, 0xFF, 0xFF);

    /// <summary>Small button fill at rest.</summary>
    public static readonly Brush ButtonFill = Frozen(0x40, 0xFF, 0xFF, 0xFF);

    /// <summary>Hover highlight over anything.</summary>
    public static readonly Brush Hover = Frozen(0x30, 0xFF, 0xFF, 0xFF);

    public static readonly Brush Text = Brushes.White;
    public static readonly Brush TextDim = Frozen(0xB0, 0xFF, 0xFF, 0xFF);
    public static readonly Brush TextFaint = Frozen(0x80, 0xFF, 0xFF, 0xFF);

    /// <summary>The Windows accent colour (Settings → Personalization → Colors), with a fallback blue.</summary>
    public static readonly Color AccentColor = ReadSystemAccent() ?? Color.FromRgb(0x4C, 0xAF, 0xF5);
    public static readonly Brush Accent = Frozen(AccentColor);
    public static readonly Brush Selection = Frozen(WithAlpha(AccentColor, 0x60));
    public static readonly Brush MarqueeFill = Frozen(WithAlpha(AccentColor, 0x30));
    public static readonly Brush MarqueeStroke = Frozen(WithAlpha(AccentColor, 0xC0));

    /// <summary>Destructive actions (delete drop target).</summary>
    public static readonly Color DangerColor = Color.FromRgb(0xC0, 0x39, 0x2B);

    // --- shape and space tokens (DIPs) ---

    public const double RadiusCard = 14;
    public const double RadiusTile = 8;
    public const double RadiusControl = 6;
    public const double RadiusPill = 12;

    public const double Space1 = 4;
    public const double Space2 = 8;
    public const double Space3 = 12;
    public const double Space4 = 16;

    public const double CardWidth = 440;
    public const double TileWidth = 100;
    public const double TileHeight = 104;
    public const double IconSize = 48;

    // --- type tokens ---

    public static readonly FontFamily Font = new("Segoe UI Variable, Segoe UI, Malgun Gothic");
    public const double FontSmall = 12;
    public const double FontBody = 13;
    public const double FontAction = 15;
    public const double FontTitle = 16;
    public const double FontLarge = 20;
    /// <summary>Hero numerals (the clock).</summary>
    public const double FontDisplay = 52;

    /// <summary>Gives a window the app's font and panel background so dialogs match the rest.</summary>
    public static void ApplyWindow(Window window)
    {
        window.FontFamily = Font;
        window.FontSize = FontBody;
        window.Background = Panel;
        window.Foreground = Text;
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Brush Frozen(byte a, byte r, byte g, byte b) => Frozen(Color.FromArgb(a, r, g, b));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // DWM stores the accent as 0xAABBGGRR.
    private static Color? ReadSystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            // fall back below
        }
        return null;
    }
}
