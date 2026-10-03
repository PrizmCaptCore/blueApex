using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BlueApex.Desktop;
using BlueApex.Zones;

namespace BlueApex.Drawer;

/// <summary>
/// The home-screen / drawer model. The desktop shows only pinned icons; every
/// other file on the desktop gets the Hidden attribute, which makes explorer leave
/// it off the desktop without any icon being moved. The drawer lists everything,
/// hidden or not, with zones as categories.
///
/// Safety: hiding is a file attribute, so it survives anything and is undone the
/// same way. The ids this app hid are saved, every clean exit unhides them, and
/// the tray offers to unhide them at any time.
/// </summary>
internal sealed class DrawerManager : IDisposable
{
    private const string RecycleBinId = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);

    private readonly DesktopIconView _icons;
    private readonly DispatcherTimer _poll;
    private readonly LayoutFile _file;
    private readonly HashSet<string> _cannotHide = new(); // files whose attributes we may not change
    private Dictionary<string, DesktopIcon> _current = new();

    /// <summary>Raised after anything the drawer shows has changed.</summary>
    public event Action? Changed;

    /// <summary>Something worth a tray balloon: (title, message).</summary>
    public event Action<string, string>? Notice;

    public DrawerManager()
    {
        _icons = DesktopIconView.Connect();
        BackupStore.Save(_icons.GetIcons()); // the desktop as found

        _file = LayoutStore.Load();
        if (_file.Version < 3)
        {
            // Drawer mode: keep the Recycle Bin at hand, everything else goes into the drawer.
            _file.Version = 3;
            if (!_file.Pinned.Contains(RecycleBinId)) _file.Pinned.Add(RecycleBinId);
        }

        _current = DesktopCatalog.Scan(_icons).ToDictionary(i => i.Id);
        EnsureDefaultZone();
        Reconcile();
        Save();

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    public IReadOnlyList<Zone> Zones => _file.Zones;
    public IReadOnlyCollection<DesktopIcon> Icons => _current.Values;
    public string Hotkey => _file.Hotkey;

    /// <summary>Public-desktop files we could not hide for lack of rights. Empty once access is granted.</summary>
    public IReadOnlyCollection<string> CannotHide => _cannotHide;

    public IEnumerable<DesktopIcon> IconsOf(Zone zone) =>
        zone.Members.Select(id => _current.GetValueOrDefault(id)).OfType<DesktopIcon>();

    /// <summary>Pinned, or a shell item (which cannot be hidden), or a file we lack the rights to hide.</summary>
    public bool IsOnDesktop(string id) =>
        _file.Pinned.Contains(id) || (_current.TryGetValue(id, out var icon) && icon.IsShellItem) || _cannotHide.Contains(id);

    public bool IsPinned(string id) => _file.Pinned.Contains(id);
    public bool CanToggle(string id) => _current.TryGetValue(id, out var icon) && !icon.IsShellItem;

    public Zone? ZoneOf(string id) => _file.Zones.FirstOrDefault(z => z.Members.Contains(id));

    // --- pin / hide ---

    /// <summary>Shows the icon on the desktop; explorer puts it back where it used to be.</summary>
    public void Pin(string id)
    {
        if (IsPinned(id) || !_current.ContainsKey(id)) return;
        _file.Pinned.Add(id);
        Unhide(id);
        Save();
        Refresh();
        Changed?.Invoke();
    }

    /// <summary>Takes the icon off the desktop; it stays reachable in the drawer.</summary>
    public void Unpin(string id)
    {
        if (!_file.Pinned.Remove(id)) return;
        Hide(id);
        Save();
        Refresh();
        Changed?.Invoke();
    }

    private void Hide(string id)
    {
        if (!_current.TryGetValue(id, out var icon) || icon.IsShellItem || icon.Hidden) return;
        if (DesktopCatalog.SetHidden(id, true))
        {
            if (!_file.HiddenByApp.Contains(id)) _file.HiddenByApp.Add(id);
            _cannotHide.Remove(id);
        }
        else
        {
            _cannotHide.Add(id);
        }
    }

    private void Unhide(string id)
    {
        if (!_file.HiddenByApp.Remove(id)) return; // never clear a Hidden attribute the user set themselves
        DesktopCatalog.SetHidden(id, false);
    }

    /// <summary>Hides every unpinned file and shows every pinned one.</summary>
    private void Reconcile()
    {
        var before = _file.HiddenByApp.Count;
        foreach (var icon in _current.Values.ToList())
        {
            if (IsPinned(icon.Id)) Unhide(icon.Id);
            else Hide(icon.Id);
        }
        if (_file.HiddenByApp.Count != before)
            Log.Write($"hidden by app: {_file.HiddenByApp.Count} file(s), cannot hide: {_cannotHide.Count}");
    }

    /// <summary>Clears the Hidden attribute from every file this app hid. Used on exit and from the tray.</summary>
    public void UnhideAll()
    {
        var count = 0;
        foreach (var id in _file.HiddenByApp.ToList())
            if (DesktopCatalog.SetHidden(id, false)) count++;
        _file.HiddenByApp.Clear();
        if (count > 0) Log.Write($"unhid {count} file(s)");
        Save();
    }

    /// <summary>Asks for elevation once to make public-desktop files hideable, then hides what was pending.</summary>
    public bool GrantPublicDesktopAccess()
    {
        if (!DesktopCatalog.GrantPublicDesktopAccess()) return false;
        _cannotHide.Clear();
        Refresh();
        Reconcile();
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Puts icons back as a backup has them and pins them all, so the drawer leaves them alone.</summary>
    public void Restore(DesktopBackup backup)
    {
        foreach (var icon in backup.Icons)
            if (!_file.Pinned.Contains(icon.Id)) _file.Pinned.Add(icon.Id);
        Refresh();
        Reconcile();
        _icons.MoveIcons(backup.Icons.ToDictionary(i => i.Id, i => new NativeMethods.POINT { X = i.X, Y = i.Y }));
        Log.Write($"restored {backup.Icons.Count} icon(s) from backup {backup.Time:yyyy-MM-dd HH:mm:ss}");
        Refresh();
        Save();
        Changed?.Invoke();
    }

    public string BackupNow() => BackupStore.Save(_icons.GetIcons());

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

    // --- zones ---

    /// <summary>The catch-all zone: where icons go when no rule claims them. Always exists.</summary>
    public Zone DefaultZone => _file.Zones.First(z => z.Id == _file.DefaultZoneId);

    public bool IsDefault(Zone zone) => zone.Id == _file.DefaultZoneId;

    public void SetDefaultZone(Zone zone)
    {
        _file.DefaultZoneId = zone.Id;
        Save();
        Changed?.Invoke();
    }

    // Makes sure a default zone exists and that every icon belongs to some zone.
    private void EnsureDefaultZone()
    {
        if (_file.DefaultZoneId == null || _file.Zones.All(z => z.Id != _file.DefaultZoneId))
        {
            var zone = new Zone { Title = "기본" };
            _file.Zones.Insert(0, zone);
            _file.DefaultZoneId = zone.Id;
        }
        var owned = _file.Zones.SelectMany(z => z.Members).ToHashSet();
        foreach (var icon in _current.Values.Where(i => !owned.Contains(i.Id)).OrderBy(i => i.Name))
            DefaultZone.Members.Add(icon.Id);
    }

    /// <summary>Zones other than the default: the ones whose rules can claim an icon.</summary>
    private IEnumerable<Zone> RuleZones => _file.Zones.Where(z => !IsDefault(z));

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

    /// <summary>Removes the category; its icons go to the default zone. The default zone cannot be removed.</summary>
    public void RemoveZone(Zone zone)
    {
        if (IsDefault(zone) || !_file.Zones.Remove(zone)) return;
        DefaultZone.Members.AddRange(zone.Members);
        Save();
        Changed?.Invoke();
    }

    public void SetPatterns(Zone zone, IEnumerable<string> patterns)
    {
        zone.Patterns = patterns.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        Save();
    }

    /// <summary>Puts the icon in a zone (null = the default zone), keeping its pinned state.</summary>
    public void MoveToZone(string id, Zone? zone)
    {
        foreach (var z in _file.Zones) z.Members.Remove(id);
        (zone ?? DefaultZone).Members.Add(id);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Sorts the default zone's icons into the first other zone whose patterns match. Returns how many moved.</summary>
    public int ApplyRulesToUnassigned()
    {
        var sorted = 0;
        foreach (var icon in IconsOf(DefaultZone).ToList())
        {
            var target = ZoneRules.Target(RuleZones, icon);
            if (target == null) continue;
            DefaultZone.Members.Remove(icon.Id);
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
        var file = icon.IsShellItem ? "shell:" + icon.Id : icon.Id;
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    /// <summary>Sends the file or folder to the Recycle Bin (undoable there). Returns false if the shell refused.</summary>
    public bool Delete(DesktopIcon icon)
    {
        if (icon.IsShellItem) return false;
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = icon.Id + "\0\0", // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
        };
        var result = SHFileOperation(ref operation);
        var ok = result == 0 && !operation.fAnyOperationsAborted;
        Log.Write($"delete {icon.Name}: {(ok ? "sent to recycle bin" : $"failed ({result})")}");
        if (ok) Poll(); // drop it from the drawer right away
        return ok;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    public static void OpenLocation(DesktopIcon icon)
    {
        if (icon.IsShellItem) return;
        Process.Start("explorer.exe", $"/select,\"{icon.Id}\"");
    }

    // --- polling ---

    private void Refresh()
    {
        _current = DesktopCatalog.Scan(_icons).ToDictionary(i => i.Id);
    }

    /// <summary>Runs the poll right away.</summary>
    public void PollNow() => Poll();

    private void Poll()
    {
        List<DesktopIcon> icons;
        try
        {
            icons = DesktopCatalog.Scan(_icons);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException)
        {
            return; // explorer busy or restarting; try again next tick
        }

        var changed = false;
        var seen = icons.Select(i => i.Id).ToHashSet();
        var previous = _current;
        _current = icons.ToDictionary(i => i.Id);

        foreach (var icon in icons)
        {
            if (!previous.ContainsKey(icon.Id))
            {
                // New on the desktop: file it by rule (else the default zone), then into the drawer unless pinned.
                var target = ZoneRules.Target(RuleZones, icon) ?? DefaultZone;
                target.Members.Add(icon.Id);
                Log.Write($"new {icon.Name} -> {target.Title}");
                if (!IsPinned(icon.Id) && !icon.IsShellItem)
                {
                    Hide(icon.Id);
                    Notice?.Invoke("서랍에 넣었습니다", $"{icon.Name} → {target.Title}");
                }
                changed = true;
            }
            else if (!IsPinned(icon.Id) && !icon.Hidden && !icon.IsShellItem && !_cannotHide.Contains(icon.Id))
            {
                Hide(icon.Id); // something unhid it (the user, an installer): back into the drawer
            }
            else if (IsPinned(icon.Id) && icon.Hidden && _file.HiddenByApp.Contains(icon.Id))
            {
                Unhide(icon.Id);
            }
        }

        foreach (var id in previous.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            // Gone from the desktop (deleted, moved away).
            foreach (var z in _file.Zones) z.Members.Remove(id);
            _file.Pinned.Remove(id);
            _file.HiddenByApp.Remove(id);
            _cannotHide.Remove(id);
            changed = true;
        }

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
        UnhideAll(); // never leave the desktop empty behind us
    }
}
