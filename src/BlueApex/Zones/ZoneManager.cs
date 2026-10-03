using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using BlueApex.Desktop;

namespace BlueApex.Zones;

/// <summary>
/// Owns the zones: keeps each zone's member icons laid out in a grid, notices
/// icons the user drags into or out of a zone, and persists the layout.
///
/// Icon positions are polled rather than hooked: explorer handles the drag
/// itself, and a position that differs from the last one we recorded means the
/// icon moved. Moved icons that are selected were dropped by the user (a drag
/// selects what it drags, and the selection survives the drop), so they change
/// zone membership; moved icons that are not selected were rearranged by
/// explorer (resolution change, refresh) and are simply put back on their slots.
/// </summary>
internal sealed class ZoneManager : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly DesktopHost _desktop;
    private readonly DesktopIconView _icons;
    private readonly ZoneLayout _layout;
    private readonly CellSize _cell;
    private readonly Dictionary<Guid, ZoneSurface> _surfaces = new();
    private readonly Dictionary<string, NativeMethods.POINT> _known = new();
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _reload;
    private readonly FileSystemWatcher _watcher;
    private readonly bool _restoreSnapToGrid;
    private readonly bool _restoreAutoArrange;
    private LayoutFile _file;
    private DateTime _lastSave;

    public ZoneManager(DesktopHost desktop)
    {
        _desktop = desktop;
        _icons = DesktopIconView.Connect();

        // Explorer must not reposition icons on its own, or they drift off their slots.
        _restoreSnapToGrid = _icons.SnapToGrid;
        _restoreAutoArrange = _icons.AutoArrange;
        if (_restoreSnapToGrid) _icons.SnapToGrid = false;
        if (_restoreAutoArrange) _icons.AutoArrange = false;

        _cell = _icons.Spacing;
        var scale = desktop.Dpi / 96.0;
        HeaderHeight = (int)Math.Round(ZoneSurface.HeaderHeightDip * scale);
        GripSize = (int)Math.Round(ZoneSurface.GripSizeDip * scale);
        Padding = (int)Math.Round(8 * scale);
        _layout = new ZoneLayout(_cell, HeaderHeight, Padding);

        // Snapshot the desktop as found, before any icon is touched.
        BackupStore.Save(_icons.GetIcons());

        _file = LayoutStore.Load();
        Apply();
        Save();

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();

        // The layout file is the only way to move or rename a zone until there is
        // mouse handling, so edits to it are picked up live.
        _reload = new DispatcherTimer { Interval = ReloadDelay };
        _reload.Tick += (_, _) => { _reload.Stop(); Reload(); };
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(LayoutStore.FilePath)!, Path.GetFileName(LayoutStore.FilePath));
        FileSystemEventHandler onChanged = (_, _) =>
        {
            if (DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(1.5)) return; // our own write
            Application.Current.Dispatcher.BeginInvoke(() => { _reload.Stop(); _reload.Start(); });
        };
        _watcher.Changed += onChanged;
        _watcher.Created += onChanged; // editors that save via rename
        _watcher.Renamed += (s, e) => onChanged(s, e);
        _watcher.EnableRaisingEvents = true;
    }

    public IReadOnlyList<Zone> Zones => _file.Zones;

    /// <summary>Title bar height in physical pixels.</summary>
    public int HeaderHeight { get; }

    /// <summary>Space between the zone edge and the icon grid, physical pixels.</summary>
    public int Padding { get; }

    /// <summary>Side of the resize grip square in the bottom-right corner, physical pixels.</summary>
    public int GripSize { get; }

    public (int Width, int Height) MinZoneSize => _layout.SizeFor(1, 1);

    public ZoneLayout Layout => _layout;

    public (int Width, int Height) IconArea => _desktop.IconAreaSize;

    /// <summary>Whether an icon cell (as last observed) covers the point, in icon-view coordinates.</summary>
    public bool IsIconAt(int x, int y) =>
        _known.Values.Any(p => x >= p.X && x < p.X + _cell.Width && y >= p.Y && y < p.Y + _cell.Height);

    /// <summary>Moves/resizes the zone window only; call <see cref="ArrangeIcons"/> to bring the icons along.</summary>
    public void SetZoneBounds(Zone zone, int x, int y, int width, int height)
    {
        zone.X = x;
        zone.Y = y;
        zone.Width = width;
        zone.Height = height;
        _surfaces[zone.Id].SetBounds(zone);
    }

    public void ArrangeIcons()
    {
        try
        {
            ArrangeAll(_icons.GetIcons());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // explorer busy; the poll will catch up
        }
    }

    public void SaveLayout() => Save();

    /// <summary>
    /// Starts a drag of a zone: its member icons are resolved once, and each
    /// <see cref="ZoneDrag.FollowZone"/> moves them all with one call. Dispose
    /// when the drag ends, then call <see cref="ArrangeIcons"/> to settle.
    /// </summary>
    public ZoneDrag BeginDrag(Zone zone) => new(zone, _layout, _icons.CreateBatch(zone.Members));

    public sealed class ZoneDrag : IDisposable
    {
        private readonly Zone _zone;
        private readonly ZoneLayout _layout;
        private readonly DesktopIconView.IconBatch _batch;
        private readonly int[] _slots;

        internal ZoneDrag(Zone zone, ZoneLayout layout, DesktopIconView.IconBatch batch)
        {
            _zone = zone;
            _layout = layout;
            _batch = batch;
            _slots = batch.Ids.Select(id => zone.Members.IndexOf(id)).ToArray();
        }

        public void FollowZone()
        {
            var positions = new NativeMethods.POINT[_slots.Length];
            for (var i = 0; i < _slots.Length; i++)
                positions[i] = _layout.SlotPosition(_zone, _slots[i]);
            _batch.Move(positions);
        }

        public void Dispose() => _batch.Dispose();
    }

    /// <summary>Adds an empty 4x3 zone near the middle of the screen, adopting any icons already there.</summary>
    public Zone AddZone()
    {
        var (width, height) = _layout.SizeFor(4, 3);
        var (areaWidth, areaHeight) = _desktop.IconAreaSize;
        var offset = _file.Zones.Count * 24;
        return AddZone((areaWidth - width) / 2 + offset, (areaHeight - height) / 2 + offset, width, height);
    }

    /// <summary>Adds a zone with the given bounds (icon-view coordinates), adopting any icons already inside.</summary>
    public Zone AddZone(int x, int y, int width, int height)
    {
        var (minWidth, minHeight) = MinZoneSize;
        var zone = new Zone
        {
            Title = $"새 구역 {_file.Zones.Count + 1}",
            X = x,
            Y = y,
            Width = Math.Max(width, minWidth),
            Height = Math.Max(height, minHeight),
        };
        _file.Zones.Add(zone);
        _surfaces[zone.Id] = new ZoneSurface(_desktop, zone, EffectiveStyle(zone));

        var icons = _icons.GetIcons();
        Adopt(zone, icons);
        ArrangeAll(icons);
        Save();
        return zone;
    }

    public string BackupNow() => BackupStore.Save(_icons.GetIcons());

    /// <summary>
    /// Puts icons back where a backup has them. Zones stay but give up their
    /// members, otherwise the next poll would pull the icons straight back.
    /// Icons missing from the backup are left alone.
    /// </summary>
    public void Restore(DesktopBackup backup)
    {
        foreach (var zone in _file.Zones)
            zone.Members.Clear();

        var positions = backup.Icons.ToDictionary(i => i.Id, i => new NativeMethods.POINT { X = i.X, Y = i.Y });
        _icons.MoveIcons(positions);
        Log.Write($"restored {positions.Count} icon(s) from backup {backup.Time:yyyy-MM-dd HH:mm:ss}");

        ArrangeAll(_icons.GetIcons());
        Save();
    }

    /// <summary>Whether the point (icon-view coordinates) is inside a zone as currently shown.</summary>
    public bool IsZoneAt(int x, int y) => _file.Zones.Any(f => _layout.Contains(f, x, y));

    /// <summary>Roll the zone up to its title bar (members parked off-screen) or back down.</summary>
    public void ToggleRolled(Zone zone)
    {
        zone.Rolled = !zone.Rolled;
        _surfaces[zone.Id].SetBounds(zone);
        ArrangeIcons();
        Save();
    }

    /// <summary>Quick hide: all desktop icons and zones disappear until toggled back. Not persisted.</summary>
    public bool Hidden { get; private set; }

    public void ToggleHidden()
    {
        Hidden = !Hidden;
        NativeMethods.ShowWindow(_desktop.IconView, Hidden ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOWNA);
        foreach (var surface in _surfaces.Values)
            surface.Visible = !Hidden;
    }

    /// <summary>The zone's look with every field resolved: its own style over the default over the built-in.</summary>
    public ZoneStyle EffectiveStyle(Zone zone) =>
        (zone.Style ?? new ZoneStyle()).Over(_file.DefaultStyle).Over(ZoneStyle.BuiltIn);

    /// <summary>The look a zone with no style of its own gets.</summary>
    public ZoneStyle DefaultStyle => _file.DefaultStyle.Over(ZoneStyle.BuiltIn);

    /// <summary>Shows a style on the zone without saving it (live preview while editing).</summary>
    public void PreviewStyle(Zone zone, ZoneStyle style) =>
        _surfaces[zone.Id].ApplyStyle(style.Over(ZoneStyle.BuiltIn));

    /// <summary>Saves a style for one zone, or (asDefault) for the layout and clears the zone's own.</summary>
    public void SetStyle(Zone zone, ZoneStyle? style, bool asDefault)
    {
        if (asDefault)
        {
            _file.DefaultStyle = style ?? new ZoneStyle();
            zone.Style = null;
        }
        else
        {
            zone.Style = style;
        }
        foreach (var z in _file.Zones)
            _surfaces[z.Id].ApplyStyle(EffectiveStyle(z));
        Save();
    }

    public void SetPatterns(Zone zone, IEnumerable<string> patterns)
    {
        zone.Patterns = patterns.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        Save();
    }

    /// <summary>
    /// Sorts every icon that belongs to no zone into the first zone whose
    /// patterns match it. Icons already in a zone are left where the user put them.
    /// Returns how many icons moved.
    /// </summary>
    public int ApplyRulesToUnowned()
    {
        var icons = _icons.GetIcons();
        var owned = _file.Zones.SelectMany(z => z.Members).ToHashSet();
        var sorted = 0;
        foreach (var icon in icons.Where(i => !owned.Contains(i.Id)).OrderBy(i => i.Name))
        {
            var target = ZoneRules.Target(_file.Zones, icon);
            if (target == null) continue;
            target.Members.Add(icon.Id);
            sorted++;
            Log.Write($"rule: {icon.Name} -> {target.Title}");
        }
        if (sorted > 0)
        {
            ArrangeAll(icons);
            Save();
        }
        return sorted;
    }

    public void RenameZone(Zone zone, string title)
    {
        zone.Title = title;
        _surfaces[zone.Id].Title = title;
        Save();
    }

    /// <summary>Removes the zone; its icons stay where they are, no longer managed.</summary>
    public void RemoveZone(Zone zone)
    {
        if (!_file.Zones.Remove(zone)) return;
        _surfaces[zone.Id].Dispose();
        _surfaces.Remove(zone.Id);
        Save();
    }

    private void Reload()
    {
        Log.Write("reload layout file");
        try
        {
            _file = LayoutStore.Load();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return; // mid-edit or malformed; keep the current state and wait for the next change
        }
        foreach (var surface in _surfaces.Values) surface.Dispose();
        _surfaces.Clear();
        Apply();
    }

    /// <summary>Creates surfaces for the loaded zones and lays their icons out.</summary>
    private void Apply()
    {
        foreach (var zone in _file.Zones)
            _surfaces[zone.Id] = new ZoneSurface(_desktop, zone, EffectiveStyle(zone));

        var icons = _icons.GetIcons();
        foreach (var zone in _file.Zones)
            Adopt(zone, icons);
        ArrangeAll(icons);
    }

    private void Poll()
    {
        IReadOnlyList<DesktopIcon> icons;
        try
        {
            icons = _icons.GetIcons();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return; // explorer busy or restarting; try again next tick
        }

        var moved = icons.Where(i => !_known.TryGetValue(i.Id, out var p) || p.X != i.X || p.Y != i.Y).ToList();
        // Unselected moves are mostly our own zone drags being observed; log those as a count only.
        foreach (var icon in moved.Where(i => i.Selected || !_known.ContainsKey(i.Id)))
            Log.Write($"moved {(_known.TryGetValue(icon.Id, out var prev) ? $"({prev.X},{prev.Y})" : "new")} -> ({icon.X},{icon.Y}) selected={icon.Selected} {icon.Name}");
        var quietMoves = moved.Count(i => !i.Selected && _known.ContainsKey(i.Id));
        if (quietMoves > 0)
            Log.Write($"moved {quietMoves} unselected icon(s)");
        var removed = _known.Keys.Except(icons.Select(i => i.Id)).ToList();
        if (moved.Count == 0 && removed.Count == 0)
            return;

        var changed = false;
        foreach (var id in removed)
        {
            _known.Remove(id);
            changed |= RemoveMember(id);
        }

        // Dropped by the user: the selection moves as one group and lands spread out
        // around the drop point, so the zone that catches any of it takes all of it.
        // Reading order keeps the group's relative arrangement.
        var dropped = moved.Where(i => i.Selected).OrderBy(i => i.Y).ThenBy(i => i.X).ToList();
        if (dropped.Count > 0)
        {
            var target = dropped
                .Select(ZoneAt)
                .OfType<Zone>()
                .GroupBy(f => f)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
            Log.Write($"drop {dropped.Count} icon(s) [{string.Join(", ", dropped.Select(i => i.Name))}] -> {target?.Title ?? "none"}");

            foreach (var icon in dropped)
                changed |= RemoveMember(icon.Id);
            if (target != null)
            {
                var first = dropped[0];
                var slot = _layout.SlotFromPoint(target, first.X + _cell.Width / 2, first.Y + _cell.Height / 2, target.Members.Count);
                target.Members.InsertRange(slot, dropped.Select(i => i.Id));
                changed = true;
            }
        }

        // Newly appeared icons (downloads, installers): a matching rule decides, else the zone they landed in.
        foreach (var icon in moved.Where(i => !i.Selected && !_known.ContainsKey(i.Id)))
        {
            var ruled = ZoneRules.Target(_file.Zones, icon);
            var target = ruled ?? ZoneAt(icon);
            Log.Write($"new {icon.Name} at ({icon.X},{icon.Y}) -> {target?.Title ?? "none"}{(ruled != null ? " (rule)" : "")}");
            if (target == null) continue;
            var slot = ruled != null
                ? target.Members.Count
                : _layout.SlotFromPoint(target, icon.X + _cell.Width / 2, icon.Y + _cell.Height / 2, target.Members.Count);
            target.Members.Insert(slot, icon.Id);
            changed = true;
        }

        // Members moved by explorer go back to their slots; other icons keep their new spots.
        ArrangeAll(icons);
        if (changed) Save();
    }

    private Zone? ZoneAt(DesktopIcon icon) =>
        _file.Zones.FirstOrDefault(f => _layout.Contains(f, icon.X + _cell.Width / 2, icon.Y + _cell.Height / 2));

    /// <summary>Makes every unowned icon whose center lies inside the zone a member, in reading order.</summary>
    private void Adopt(Zone zone, IReadOnlyList<DesktopIcon> icons)
    {
        var owned = _file.Zones.SelectMany(f => f.Members).ToHashSet();
        var inside = icons
            .Where(i => !owned.Contains(i.Id) && _layout.Contains(zone, i.X + _cell.Width / 2, i.Y + _cell.Height / 2))
            .OrderBy(i => i.Y).ThenBy(i => i.X);
        foreach (var icon in inside)
        {
            Log.Write($"adopt {icon.Name} ({icon.X},{icon.Y}) into {zone.Title}");
            zone.Members.Add(icon.Id);
        }
    }

    /// <summary>Moves every member icon that is not on its slot, then re-records all positions.</summary>
    private void ArrangeAll(IReadOnlyList<DesktopIcon> icons)
    {
        var byId = icons.ToDictionary(i => i.Id);
        var moves = new Dictionary<string, NativeMethods.POINT>();
        foreach (var zone in _file.Zones)
        {
            zone.Members.RemoveAll(id => !byId.ContainsKey(id));
            for (var slot = 0; slot < zone.Members.Count; slot++)
            {
                var icon = byId[zone.Members[slot]];
                var target = _layout.SlotPosition(zone, slot);
                if (icon.X != target.X || icon.Y != target.Y)
                    moves[icon.Id] = target;
            }
        }

        if (moves.Count > 0)
        {
            Log.Write($"arrange: {string.Join(", ", moves.Select(m => $"{byId[m.Key].Name}->({m.Value.X},{m.Value.Y})"))}");
            _icons.MoveIcons(moves);
            icons = _icons.GetIcons(); // record what explorer actually did, not what we asked for
        }

        _known.Clear();
        foreach (var icon in icons)
            _known[icon.Id] = new NativeMethods.POINT { X = icon.X, Y = icon.Y };
    }

    private bool RemoveMember(string id)
    {
        var removed = false;
        foreach (var zone in _file.Zones)
            removed |= zone.Members.Remove(id);
        return removed;
    }

    private void Save()
    {
        _lastSave = DateTime.UtcNow;
        LayoutStore.Save(_file);
    }

    public void Dispose()
    {
        _poll.Stop();
        _reload.Stop();
        _watcher.Dispose();
        if (Hidden) ToggleHidden(); // never leave the desktop icons hidden behind us
        foreach (var surface in _surfaces.Values) surface.Dispose();
        _surfaces.Clear();

        try
        {
            if (_restoreSnapToGrid) _icons.SnapToGrid = true;
            if (_restoreAutoArrange) _icons.AutoArrange = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // explorer is gone; nothing to restore
        }
    }
}
