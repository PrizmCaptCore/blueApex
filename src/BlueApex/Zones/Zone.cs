namespace BlueApex.Zones;

/// <summary>
/// A zone: one card in the drawer, holding an ordered list of icons (or, for a portal,
/// showing a folder's contents). Serialized to the layout file as-is.
/// </summary>
internal sealed class Zone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "새 구역";

    /// <summary>Folded: the card in the drawer shows only its title (click the title to toggle).</summary>
    public bool Rolled { get; set; }

    /// <summary>
    /// A "portal": instead of members, the card shows the live contents of this
    /// folder (e.g. the Pictures folder). Null for a normal zone.
    /// </summary>
    public string? PortalPath { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPortal => PortalPath != null;

    /// <summary>
    /// How the card lays its items out: "manual" (drag order, flat), or "name" / "date" / "type",
    /// which fold the items into collapsible groups (letters, recency buckets, kinds).
    /// </summary>
    public string SortMode { get; set; } = "manual";

    /// <summary>Icon ids (<see cref="Desktop.DesktopIcon.Id"/>) in slot order.</summary>
    public List<string> Members { get; set; } = new();

    /// <summary>
    /// Auto-sort rules: wildcards matched against the file name ("*.url", "Steam*"),
    /// optionally prefixed with "folder:" or "file:". New icons matching a pattern
    /// go into this zone; the first zone in the layout with a match wins.
    /// </summary>
    public List<string> Patterns { get; set; } = new();
}
