using System.Runtime.InteropServices;

namespace BlueApex.Desktop;

/// <param name="Id">Shell parsing name: a file path, or ::{CLSID} for items like the Recycle Bin. Stable across sessions.</param>
/// <param name="X">Position in the icon view's client coordinates (physical pixels).</param>
/// <param name="Selected">Whether the icon is selected in the view. After a drag and drop, the dropped icons are the selected ones.</param>
/// <param name="Hidden">File has the Hidden attribute, so explorer does not show it on the desktop.</param>
internal sealed record DesktopIcon(string Id, string Name, int X, int Y, bool Selected, bool Hidden = false)
{
    /// <summary>Shell items like the Recycle Bin: not files, so they cannot be hidden by attribute.</summary>
    public bool IsShellItem => Id.StartsWith("::", StringComparison.Ordinal);
}

/// <summary>Size of one icon cell (explorer's icon spacing), physical pixels.</summary>
internal readonly record struct CellSize(int Width, int Height);

/// <summary>
/// Reads and sets desktop icon positions through the shell's documented view
/// interfaces (IFolderView), from our own process. Nothing is injected into explorer.
/// </summary>
internal sealed class DesktopIconView
{
    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;

    private readonly IFolderView2 _view;
    private readonly IShellView _shellView;
    private readonly IShellFolder _folder;

    /// <summary>
    /// Asks explorer to persist the current icon positions. Explorer re-applies its
    /// last saved positions after a user drop, which undid every position set
    /// through the API until this was called after them.
    /// </summary>
    public void SaveViewState() => _shellView.SaveViewState();

    /// <summary>
    /// Takes icons out of the desktop view (they are not listed or drawn until put
    /// back) without touching the files or their saved positions. Returns the ids
    /// that were actually removed.
    /// </summary>
    public IReadOnlyList<string> RemoveFromView(IReadOnlyCollection<string> ids)
    {
        var folderView = (IShellFolderView)_view;
        var removed = new List<string>();
        var pidls = new List<(string Id, IntPtr Pidl)>();
        try
        {
            ForEachItemOwned(pidl =>
            {
                var id = DisplayName(pidl, ShellNative.SHGDN_FORPARSING);
                if (!ids.Contains(id)) return false;
                pidls.Add((id, pidl));
                return true;
            });
            foreach (var (id, pidl) in pidls)
                if (folderView.RemoveObject(pidl, out _) >= 0)
                    removed.Add(id);
        }
        finally
        {
            foreach (var (_, pidl) in pidls)
                Marshal.FreeCoTaskMem(pidl);
        }
        return removed;
    }

    /// <summary>Puts items back into the desktop view, given their parsing names (file paths or ::{CLSID}).</summary>
    public IReadOnlyList<string> AddToView(IReadOnlyCollection<string> ids)
    {
        var folderView = (IShellFolderView)_view;
        var added = new List<string>();
        foreach (var id in ids)
        {
            var pidl = IntPtr.Zero;
            try
            {
                if (ShellNative.SHParseDisplayName(id, IntPtr.Zero, out pidl, 0, out _) < 0) continue;
                var child = ShellNative.ILFindLastID(pidl);
                if (folderView.AddObject(child, out _) >= 0)
                    added.Add(id);
            }
            finally
            {
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
        }
        return added;
    }

    private DesktopIconView(IFolderView2 view, IShellFolder folder)
    {
        _view = view;
        _shellView = (IShellView)view;
        _folder = folder;
    }

    /// <summary>The desktop view's automation object (ShellFolderView), for late-bound calls.</summary>
    public static object DesktopDocument()
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows)!)!;
        object? location = 0;
        object? root = null;
        dynamic dispatch = shellWindows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH)!;
        return dispatch.Document;
    }

    public static DesktopIconView Connect()
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows)!)!;
        object? location = 0; // CSIDL_DESKTOP
        object? root = null;
        var dispatch = shellWindows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH)
                       ?? throw new InvalidOperationException("바탕화면 셸 창을 찾을 수 없습니다. explorer.exe가 실행 중인지 확인하세요.");

        var browserSid = SID_STopLevelBrowser;
        var browserIid = typeof(IShellBrowser).GUID;
        ((IOleServiceProvider)dispatch).QueryService(ref browserSid, ref browserIid, out var browser);
        ((IShellBrowser)browser).QueryActiveShellView(out var shellView);

        var view = (IFolderView2)shellView;
        var folderIid = typeof(IShellFolder).GUID;
        view.GetFolder(ref folderIid, out var folder);
        return new DesktopIconView(view, (IShellFolder)folder);
    }

    public CellSize Spacing
    {
        get
        {
            _view.GetSpacing(out var pt);
            return new CellSize(pt.X, pt.Y);
        }
    }

    public bool AutoArrange
    {
        get => (Flags & ShellNative.FWF_AUTOARRANGE) != 0;
        set => _view.SetCurrentFolderFlags(ShellNative.FWF_AUTOARRANGE, value ? ShellNative.FWF_AUTOARRANGE : 0);
    }

    public bool SnapToGrid
    {
        get => (Flags & ShellNative.FWF_SNAPTOGRID) != 0;
        set => _view.SetCurrentFolderFlags(ShellNative.FWF_SNAPTOGRID, value ? ShellNative.FWF_SNAPTOGRID : 0);
    }

    private uint Flags
    {
        get
        {
            _view.GetCurrentFolderFlags(out var flags);
            return flags;
        }
    }

    public IReadOnlyList<DesktopIcon> GetIcons()
    {
        var icons = new List<DesktopIcon>();
        ForEachItem(pidl =>
        {
            _view.GetItemPosition(pidl, out var pt);
            _view.GetSelectionState(pidl, out var state);
            icons.Add(new DesktopIcon(DisplayName(pidl, ShellNative.SHGDN_FORPARSING),
                DisplayName(pidl, ShellNative.SHGDN_NORMAL), pt.X, pt.Y, (state & ShellNative.SVSI_SELECTED) != 0));
        });
        return icons;
    }

    /// <summary>Selects exactly the given icons (keyed by <see cref="DesktopIcon.Id"/>), deselecting all others.</summary>
    public void Select(IReadOnlyCollection<string> ids)
    {
        var pidls = new List<IntPtr>();
        try
        {
            ForEachItemOwned(pidl =>
            {
                if (ids.Contains(DisplayName(pidl, ShellNative.SHGDN_FORPARSING)))
                {
                    pidls.Add(pidl);
                    return true;
                }
                return false;
            });
            // Positions must be null here: explorer applies them even without SVSI_POSITIONITEM.
            // SVSI_DESELECTOTHERS is applied per item, so it is used on the first item only.
            if (pidls.Count == 0) return;
            _view.SelectAndPositionItems(1, pidls.ToArray(), null, ShellNative.SVSI_SELECT | ShellNative.SVSI_DESELECTOTHERS);
            if (pidls.Count > 1)
                _view.SelectAndPositionItems((uint)pidls.Count - 1, pidls.Skip(1).ToArray(), null, ShellNative.SVSI_SELECT);
        }
        finally
        {
            foreach (var pidl in pidls)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>Moves the given icons (keyed by <see cref="DesktopIcon.Id"/>) in one call. Unknown ids are ignored.</summary>
    public void MoveIcons(IReadOnlyDictionary<string, NativeMethods.POINT> positions)
    {
        var pidls = new List<IntPtr>();
        var points = new List<NativeMethods.POINT>();
        try
        {
            ForEachItemOwned(pidl =>
            {
                if (!positions.TryGetValue(DisplayName(pidl, ShellNative.SHGDN_FORPARSING), out var pt))
                    return false;
                pidls.Add(pidl);
                points.Add(pt);
                return true;
            });

            if (pidls.Count > 0)
            {
                _view.SelectAndPositionItems((uint)pidls.Count, pidls.ToArray(), points.ToArray(), ShellNative.SVSI_POSITIONITEM);
                SaveViewState(); // or explorer reverts these on the next user drop
            }
        }
        finally
        {
            foreach (var pidl in pidls)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>
    /// Resolves the given icons once so they can be repositioned repeatedly with a
    /// single cross-process call each time (used while a zone is being dragged).
    /// Ids that are not on the desktop are skipped; <see cref="IconBatch.Ids"/> says which remain.
    /// </summary>
    public IconBatch CreateBatch(IReadOnlyList<string> ids)
    {
        var found = new Dictionary<string, IntPtr>();
        ForEachItemOwned(pidl =>
        {
            var id = DisplayName(pidl, ShellNative.SHGDN_FORPARSING);
            if (!ids.Contains(id) || found.ContainsKey(id))
                return false;
            found[id] = pidl;
            return true;
        });
        var present = ids.Where(found.ContainsKey).ToArray();
        return new IconBatch(_view, present, present.Select(id => found[id]).ToArray());
    }

    public sealed class IconBatch : IDisposable
    {
        private readonly IFolderView2 _view;
        private IntPtr[] _pidls;

        internal IconBatch(IFolderView2 view, string[] ids, IntPtr[] pidls)
        {
            _view = view;
            Ids = ids;
            _pidls = pidls;
        }

        public IReadOnlyList<string> Ids { get; }

        /// <param name="positions">One position per entry of <see cref="Ids"/>, same order.</param>
        public void Move(NativeMethods.POINT[] positions)
        {
            if (_pidls.Length > 0)
                _view.SelectAndPositionItems((uint)_pidls.Length, _pidls, positions, ShellNative.SVSI_POSITIONITEM);
        }

        public void Dispose()
        {
            foreach (var pidl in _pidls)
                Marshal.FreeCoTaskMem(pidl);
            _pidls = Array.Empty<IntPtr>();
        }
    }

    private void ForEachItem(Action<IntPtr> action)
    {
        ForEachItemOwned(pidl =>
        {
            action(pidl);
            return false;
        });
    }

    /// <summary>Visits every item; the callback returns true to take ownership of the pidl (and free it later).</summary>
    private void ForEachItemOwned(Func<IntPtr, bool> visit)
    {
        _view.ItemCount(ShellNative.SVGIO_ALLVIEW, out var count);
        for (var i = 0; i < count; i++)
        {
            _view.Item(i, out var pidl);
            if (!visit(pidl))
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    private string DisplayName(IntPtr pidl, uint flags)
    {
        var strret = Marshal.AllocCoTaskMem(ShellNative.STRRET_SIZE);
        try
        {
            Marshal.ThrowExceptionForHR(_folder.GetDisplayNameOf(pidl, flags, strret));
            Marshal.ThrowExceptionForHR(ShellNative.StrRetToBSTR(strret, pidl, out var name));
            return name;
        }
        finally
        {
            Marshal.FreeCoTaskMem(strret);
        }
    }
}
