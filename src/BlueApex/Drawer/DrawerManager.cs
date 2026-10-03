using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BlueApex.Desktop;
using BlueApex.Zones;

namespace BlueApex.Drawer;

/// <summary>
/// The home-screen / drawer model. The desktop shows only pinned icons; every
/// other icon is parked off-screen (far above the top edge) and reachable through
/// the drawer, where zones act as categories.
///
/// Safety: parking is a fixed vertical offset, so any icon can be brought back by
/// undoing it, whether or not this app remembers it. <see cref="UnparkAll"/> runs
/// on every clean exit; the tray backup/restore covers the rest.
/// </summary>
internal sealed class DrawerManager : IDisposable
{
    public const int ParkOffset = 8000;
    private const string RecycleBinId = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);

    private readonly DesktopIconView _icons;
    private readonly bool _restoreSnapToGrid;
    private readonly bool _restoreAutoArrange;
    private readonly CellSize _cell;
    private readonly DispatcherTimer _poll;
    private readonly LayoutFile _file;
    private Dictionary<string, DesktopIcon> _current = new();

    /// <summary>Raised after anything the drawer shows has changed.</summary>
    public event Action? Changed;

    /// <summary>Something worth a tray balloon: (title, message).</summary>
    public event Action<string, string>? Notice;

    public DrawerManager()
    {
        _icons = DesktopIconView.Connect();

        // Explorer must not reposition icons on its own, or parked icons would be pulled back.
        _restoreSnapToGrid = _icons.SnapToGrid;
        _restoreAutoArrange = _icons.AutoArrange;
        if (_restoreSnapToGrid) _icons.SnapToGrid = false;
        if (_restoreAutoArrange) _icons.AutoArrange = false;
        _cell = _icons.Spacing;

        var icons = _icons.GetIcons();
        BackupStore.Save(icons); // the desktop as found, before anything is parked

        _file = LayoutStore.Load();
        if (_file.Version < 2)
        {
            // First run in drawer mode: keep the Recycle Bin at hand, park the rest.
            _file.Version = 2;
            if (icons.Any(i => i.Id == RecycleBinId)) _file.Pinned.Add(RecycleBinId);
        }

        _current = icons.ToDictionary(i => i.Id);
        ParkUnpinned();
        Save();

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    public IReadOnlyList<Zone> Zones => _file.Zones;
    public IReadOnlyCollection<DesktopIcon> Icons => _current.Values;
    public string Hotkey => _file.Hotkey;

    public IEnumerable<DesktopIcon> IconsOf(Zone zone) =>
        zone.Members.Select(id => _current.GetValueOrDefault(id)).OfType<DesktopIcon>();

    /// <summary>Icons in no zone, by name.</summary>
    public IEnumerable<DesktopIcon> Unassigned
    {
        get
        {
            var owned = _file.Zones.SelectMany(z => z.Members).ToHashSet();
            return _current.Values.Where(i => !owned.Contains(i.Id)).OrderBy(i => i.Name);
        }
    }

    public bool IsPinned(string id) => _file.Pinned.Contains(id);
    public Zone? ZoneOf(string id) => _file.Zones.FirstOrDefault(z => z.Members.Contains(id));
    public static bool IsParked(DesktopIcon icon) => icon.Y < -ParkOffset / 2;

    // --- pin / park ---

    /// <summary>Shows the icon on the desktop, in the first free grid cell.</summary>
    public void Pin(string id)
    {
        if (IsPinned(id) || !_current.TryGetValue(id, out var icon)) return;
        _file.Pinned.Add(id);
        if (IsParked(icon))
            Move(id, FreeCell());
        else if (icon.Y < 0 || icon.Y >= NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN))
            Move(id, FreeCell()); // off-screen for some other reason: bring it where it can be seen
        Save();
        Changed?.Invoke();
    }

    /// <summary>Takes the icon off the desktop; it stays reachable in the drawer.</summary>
    public void Unpin(string id)
    {
        if (!_file.Pinned.Remove(id)) return;
        if (_current.TryGetValue(id, out var icon) && !IsParked(icon))
            Move(id, new NativeMethods.POINT { X = icon.X, Y = icon.Y - ParkOffset });
        Save();
        Changed?.Invoke();
    }

    /// <summary>Parks every unpinned icon that is on screen, and brings back any pinned icon that is parked.</summary>
    private void ParkUnpinned()
    {
        var moves = new Dictionary<string, NativeMethods.POINT>();
        foreach (var icon in _current.Values)
        {
            var pinned = IsPinned(icon.Id);
            if (!pinned && !IsParked(icon))
                moves[icon.Id] = new NativeMethods.POINT { X = icon.X, Y = icon.Y - ParkOffset };
            else if (pinned && IsParked(icon))
                moves[icon.Id] = ReturnPosition(icon);
        }
        if (moves.Count == 0) return;
        Log.Write($"park/unpark {moves.Count} icon(s)");
        _icons.MoveIcons(moves);
        Refresh();
    }

    // Where a parked icon goes when it comes back: its old spot if that is on screen, else a free cell.
    private NativeMethods.POINT ReturnPosition(DesktopIcon icon)
    {
        var y = icon.Y + ParkOffset;
        return y >= 0 && y < NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) && icon.X >= 0
            ? new NativeMethods.POINT { X = icon.X, Y = y }
            : FreeCell();
    }

    /// <summary>Brings every parked icon back to where it was parked from. Used on exit.</summary>
    public void UnparkAll()
    {
        var moves = _current.Values.Where(IsParked)
            .ToDictionary(i => i.Id, i => new NativeMethods.POINT { X = i.X, Y = i.Y + ParkOffset });
        if (moves.Count == 0) return;
        Log.Write($"unpark {moves.Count} icon(s)");
        _icons.MoveIcons(moves);
        Refresh();
    }

    /// <summary>Puts icons back as a backup has them and pins them all, so the drawer leaves them alone.</summary>
    public void Restore(DesktopBackup backup)
    {
        var positions = backup.Icons.ToDictionary(i => i.Id, i => new NativeMethods.POINT { X = i.X, Y = i.Y });
        _icons.MoveIcons(positions);
        foreach (var id in positions.Keys)
            if (!_file.Pinned.Contains(id)) _file.Pinned.Add(id);
        Log.Write($"restored {positions.Count} icon(s) from backup {backup.Time:yyyy-MM-dd HH:mm:ss}");
        Refresh();
        Save();
        Changed?.Invoke();
    }

    private void Move(string id, NativeMethods.POINT to)
    {
        _icons.MoveIcons(new Dictionary<string, NativeMethods.POINT> { [id] = to });
        Refresh();
    }

    /// <summary>A desktop rectangle (icon-view pixels) that pinned icons must not be placed on, e.g. the drawer button.</summary>
    public NativeMethods.RECT Reserved { get; set; }

    /// <summary>Saved drawer-button position, or null for the default spot.</summary>
    public (int X, int Y)? ButtonPosition
    {
        get => _file.ButtonX is { } x && _file.ButtonY is { } y ? (x, y) : null;
        set
        {
            _file.ButtonX = value?.X;
            _file.ButtonY = value?.Y;
            Save();
        }
    }

    // The first empty cell of explorer's grid, scanning each column top to bottom like explorer does.
    private NativeMethods.POINT FreeCell()
    {
        var onScreen = _current.Values.Where(i => !IsParked(i)).ToList();
        var originX = onScreen.Count > 0 ? onScreen.Min(i => ((i.X % _cell.Width) + _cell.Width) % _cell.Width) : _cell.Width / 6;
        var originY = onScreen.Count > 0 ? onScreen.Min(i => ((i.Y % _cell.Height) + _cell.Height) % _cell.Height) : _cell.Height / 3;
        var taken = onScreen.Select(i => ((i.X - originX) / _cell.Width, (i.Y - originY) / _cell.Height)).ToHashSet();
        var columns = Math.Max(1, (NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN) - originX) / _cell.Width);
        var rows = Math.Max(1, (NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) - originY) / _cell.Height);
        for (var col = 0; col < columns; col++)
            for (var row = 0; row < rows; row++)
            {
                if (taken.Contains((col, row))) continue;
                var x = originX + col * _cell.Width;
                var y = originY + row * _cell.Height;
                var overlapsReserved = x < Reserved.Right && x + _cell.Width > Reserved.Left && y < Reserved.Bottom && y + _cell.Height > Reserved.Top;
                if (!overlapsReserved)
                    return new NativeMethods.POINT { X = x, Y = y };
            }
        return new NativeMethods.POINT { X = originX, Y = originY };
    }

    // --- zones ---

    public Zone AddZone(string title)
    {
        var zone = new Zone { Title = title };
        _file.Zones.Add(zone);
        Save();
        Changed?.Invoke();
        return zone;
    }

    public void RenameZone(Zone zone, string title)
    {
        zone.Title = title;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Removes the category; its icons become unassigned (and stay pinned or parked as they were).</summary>
    public void RemoveZone(Zone zone)
    {
        _file.Zones.Remove(zone);
        Save();
        Changed?.Invoke();
    }

    public void SetPatterns(Zone zone, IEnumerable<string> patterns)
    {
        zone.Patterns = patterns.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        Save();
    }

    /// <summary>Puts the icon in a zone (or in none), keeping its pinned/parked state.</summary>
    public void MoveToZone(string id, Zone? zone)
    {
        foreach (var z in _file.Zones) z.Members.Remove(id);
        zone?.Members.Add(id);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Sorts every unassigned icon into the first zone whose patterns match. Returns how many moved.</summary>
    public int ApplyRulesToUnassigned()
    {
        var sorted = 0;
        foreach (var icon in Unassigned.ToList())
        {
            var target = ZoneRules.Target(_file.Zones, icon);
            if (target == null) continue;
            target.Members.Add(icon.Id);
            sorted++;
        }
        if (sorted > 0)
        {
            Save();
            Changed?.Invoke();
        }
        return sorted;
    }

    // --- actions ---

    public static void Launch(DesktopIcon icon)
    {
        var file = icon.Id.StartsWith("::", StringComparison.Ordinal) ? "shell:" + icon.Id : icon.Id;
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    public static void OpenLocation(DesktopIcon icon)
    {
        if (icon.Id.StartsWith("::", StringComparison.Ordinal)) return;
        Process.Start("explorer.exe", $"/select,\"{icon.Id}\"");
    }

    // --- polling ---

    private void Refresh()
    {
        _current = _icons.GetIcons().ToDictionary(i => i.Id);
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

        var changed = false;
        var moves = new Dictionary<string, NativeMethods.POINT>();
        var seen = icons.Select(i => i.Id).ToHashSet();

        foreach (var icon in icons)
        {
            if (!_current.ContainsKey(icon.Id))
            {
                // New on the desktop: file it by rule, then into the drawer unless pinned.
                var target = ZoneRules.Target(_file.Zones, icon);
                target?.Members.Add(icon.Id);
                Log.Write($"new {icon.Name} -> {target?.Title ?? "기타"}");
                if (!IsPinned(icon.Id) && !IsParked(icon))
                {
                    moves[icon.Id] = new NativeMethods.POINT { X = icon.X, Y = icon.Y - ParkOffset };
                    Notice?.Invoke("서랍에 넣었습니다", $"{icon.Name} → {target?.Title ?? "기타"}");
                }
                changed = true;
            }
            else if (!IsPinned(icon.Id) && !IsParked(icon))
            {
                // Explorer pulled a parked icon back (display change, refresh): park it again.
                moves[icon.Id] = new NativeMethods.POINT { X = icon.X, Y = icon.Y - ParkOffset };
            }
            else if (IsPinned(icon.Id) && IsParked(icon))
            {
                moves[icon.Id] = ReturnPosition(icon);
            }
        }

        foreach (var id in _current.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            // Gone from the desktop (deleted, moved away).
            foreach (var z in _file.Zones) z.Members.Remove(id);
            _file.Pinned.Remove(id);
            changed = true;
        }

        if (moves.Count > 0)
            _icons.MoveIcons(moves);

        _current = (moves.Count > 0 ? _icons.GetIcons() : icons).ToDictionary(i => i.Id);
        if (changed)
        {
            Save();
            Changed?.Invoke();
        }
    }

    private void Save() => LayoutStore.Save(_file);

    public void Dispose()
    {
        _poll.Stop();
        try
        {
            UnparkAll(); // never leave the desktop empty behind us
            if (_restoreSnapToGrid) _icons.SnapToGrid = true;
            if (_restoreAutoArrange) _icons.AutoArrange = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // explorer is gone; nothing to restore
        }
    }
}
