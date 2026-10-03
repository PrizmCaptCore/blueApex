namespace BlueApex.Zones;

/// <summary>
/// How a zone looks. Every field is optional: a zone's own style is layered over
/// the layout's default style, which is layered over <see cref="BuiltIn"/>.
/// Colors are "#RRGGBB". Opacity applies to the whole zone window (background,
/// title and text alike), because that is the only translucency the desktop composites.
/// </summary>
internal sealed class ZoneStyle
{
    public double? Opacity { get; set; }
    public string? Background { get; set; }
    public string? HeaderBackground { get; set; }
    public string? TitleColor { get; set; }
    public double? FontSize { get; set; }
    public int? CornerRadius { get; set; }

    public static readonly ZoneStyle BuiltIn = new()
    {
        Opacity = 0.7,
        Background = "#202020",
        HeaderBackground = "#101010",
        TitleColor = "#FFFFFF",
        FontSize = 13,
        CornerRadius = 12,
    };

    /// <summary>This style's values where set, otherwise the fallback's.</summary>
    public ZoneStyle Over(ZoneStyle fallback) => new()
    {
        Opacity = Opacity ?? fallback.Opacity,
        Background = Background ?? fallback.Background,
        HeaderBackground = HeaderBackground ?? fallback.HeaderBackground,
        TitleColor = TitleColor ?? fallback.TitleColor,
        FontSize = FontSize ?? fallback.FontSize,
        CornerRadius = CornerRadius ?? fallback.CornerRadius,
    };

    public ZoneStyle Clone() => Over(new ZoneStyle());
}
