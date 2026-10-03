namespace BlueApex.Zones;

/// <summary>
/// A zone: a titled rectangle on the desktop (icon-view coordinates, physical
/// pixels) and the ordered list of icons laid out inside it. Serialized to the
/// layout file as-is.
/// </summary>
internal sealed class Zone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "새 구역";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Rolled up: only the title bar shows and the members are parked off-screen. Width/Height keep the full size.</summary>
    public bool Rolled { get; set; }

    /// <summary>This zone's own look; null means the layout's default style.</summary>
    public ZoneStyle? Style { get; set; }

    /// <summary>Icon ids (<see cref="Desktop.DesktopIcon.Id"/>) in slot order.</summary>
    public List<string> Members { get; set; } = new();

    /// <summary>
    /// Auto-sort rules: wildcards matched against the file name ("*.url", "Steam*"),
    /// optionally prefixed with "folder:" or "file:". New icons matching a pattern
    /// go into this zone; the first zone in the layout with a match wins.
    /// </summary>
    public List<string> Patterns { get; set; } = new();
}
