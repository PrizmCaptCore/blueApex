using BlueApex.Desktop;

namespace BlueApex.Zones;

/// <summary>Grid placement of member icons inside a zone body (below the header).</summary>
internal sealed class ZoneLayout
{
    private readonly CellSize _cell;
    private readonly int _headerHeight;
    private readonly int _padding;

    public ZoneLayout(CellSize cell, int headerHeight, int padding)
    {
        _cell = cell;
        _headerHeight = headerHeight;
        _padding = padding;
    }

    // Members of a rolled-up zone are kept this far above the screen. Explorer accepts
    // negative positions; this stays within the 16-bit range the list view uses.
    private const int ParkOffset = 8000;

    public int Columns(Zone zone) => Math.Max(1, (zone.Width - 2 * _padding) / _cell.Width);

    /// <summary>Height the zone currently occupies on screen: just the title bar when rolled up.</summary>
    public int ShownHeight(Zone zone) => zone.Rolled ? _headerHeight : zone.Height;

    /// <summary>Whether the point lies in the zone as currently shown.</summary>
    public bool Contains(Zone zone, int px, int py) =>
        px >= zone.X && px < zone.X + zone.Width && py >= zone.Y && py < zone.Y + ShownHeight(zone);

    /// <summary>Top-left icon position for a slot. Slots past the last row spill below the zone; rolled zones park off-screen.</summary>
    public NativeMethods.POINT SlotPosition(Zone zone, int slot)
    {
        var columns = Columns(zone);
        return new NativeMethods.POINT
        {
            X = zone.X + _padding + slot % columns * _cell.Width,
            Y = zone.Y + _headerHeight + _padding + slot / columns * _cell.Height - (zone.Rolled ? ParkOffset : 0),
        };
    }

    /// <summary>Slot nearest to a point inside the zone, clamped to [0, memberCount].</summary>
    public int SlotFromPoint(Zone zone, int px, int py, int memberCount)
    {
        var columns = Columns(zone);
        var col = Math.Clamp((px - zone.X - _padding) / _cell.Width, 0, columns - 1);
        var row = Math.Max(0, (py - zone.Y - _headerHeight - _padding) / _cell.Height);
        return Math.Min(row * columns + col, memberCount);
    }

    /// <summary>Size for a zone that fits the given number of icon columns and rows.</summary>
    public (int Width, int Height) SizeFor(int columns, int rows) =>
        (2 * _padding + columns * _cell.Width, _headerHeight + 2 * _padding + rows * _cell.Height);
}
