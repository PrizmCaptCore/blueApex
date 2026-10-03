using System.Runtime.InteropServices;

namespace BlueApex.Desktop;

// COM interfaces for reaching the desktop's shell view from another process.
// Slots that are never called are parameterless placeholders; only their order
// matters for the vtable layout.

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    void _get_Count();
    void _Item();
    void _NewEnum();
    void _Register();
    void _RegisterPending();
    void _Revoke();
    void _OnNavigate();
    void _OnActivated();
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? FindWindowSW([In] ref object? pvarLoc, [In] ref object? pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleServiceProvider
{
    void QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    void _GetWindow();
    void _ContextSensitiveHelp();
    void _InsertMenusSB();
    void _SetMenuSB();
    void _RemoveMenusSB();
    void _SetStatusTextSB();
    void _EnableModelessSB();
    void _TranslateAcceleratorSB();
    void _BrowseObject();
    void _GetViewStateStream();
    void _GetControlWindow();
    void _SendControlMsg();
    void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
}

[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    void _GetWindow();
    void _ContextSensitiveHelp();
    void _TranslateAccelerator();
    void _EnableModeless();
    void _UIActivate();
    void _Refresh();
    void _CreateViewWindow();
    void _DestroyViewWindow();
    void _GetCurrentInfo();
    void _AddPropertySheetPages();
    /// <summary>Persists the view's state (icon positions included) so explorer does not revert to an older one.</summary>
    [PreserveSig] int SaveViewState();
}

[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    void _ParseDisplayName();
    void _EnumObjects();
    void _BindToObject();
    void _BindToStorage();
    void _CompareIDs();
    void _CreateViewObject();
    void _GetAttributesOf();
    void _GetUIObjectOf();
    [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
}

/// <summary>The desktop view's item-level interface: items can be taken out of (and put back into) the view without touching files or positions.</summary>
[ComImport, Guid("37A378C0-F82D-11CE-AE65-08002B2E1262"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolderView
{
    void _Rearrange();
    void _GetArrangeParam();
    void _ArrangeGrid();
    void _AutoArrange();
    void _GetAutoArrange();
    [PreserveSig] int AddObject(IntPtr pidl, out uint puItem);
    void _GetObject();
    [PreserveSig] int RemoveObject(IntPtr pidl, out uint puItem);
    void _GetObjectCount();
    void _SetObjectCount();
    void _UpdateObject();
    void _RefreshObject();
    void _SetRedraw();
}

[ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    // IFolderView
    void GetCurrentViewMode(out uint pViewMode);
    void SetCurrentViewMode(uint viewMode);
    void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void Item(int iItemIndex, out IntPtr ppidl);
    void ItemCount(uint uFlags, out int pcItems);
    void Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void GetSelectionMarkedItem(out int piItem);
    void GetFocusedItem(out int piItem);
    void GetItemPosition(IntPtr pidl, out NativeMethods.POINT ppt);
    void GetSpacing(out NativeMethods.POINT ppt);
    void GetDefaultSpacing(out NativeMethods.POINT ppt);
    [PreserveSig] int GetAutoArrange();
    void SelectItem(int iItem, uint dwFlags);
    void SelectAndPositionItems(uint cidl,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativeMethods.POINT[]? apt,
        uint dwFlags);

    // IFolderView2
    void _SetGroupBy();
    void _GetGroupBy();
    void _SetViewProperty();
    void _GetViewProperty();
    void _SetTileViewProperties();
    void _SetExtendedTileViewProperties();
    void _SetText();
    void SetCurrentFolderFlags(uint dwMask, uint dwFlags);
    void GetCurrentFolderFlags(out uint pdwFlags);
    void _GetSortColumnCount();
    void _SetSortColumns();
    void _GetSortColumns();
    void _GetItem();
    void _GetVisibleItem();
    void _GetSelectedItem();
    void _GetSelection();
    /// <summary>Returns SVSI_* flags for the item.</summary>
    void GetSelectionState(IntPtr pidl, out uint pdwFlags);
}

internal static class ShellNative
{
    public const uint SVGIO_ALLVIEW = 2;
    public const uint SVSI_SELECTED = 0x1; // GetSelectionState result
    public const uint SVSI_SELECT = 0x1;   // SelectAndPositionItems flags
    public const uint SVSI_DESELECTOTHERS = 0x4;
    public const uint SVSI_POSITIONITEM = 0x80;
    public const uint SHGDN_NORMAL = 0;
    public const uint SHGDN_FORPARSING = 0x8000;
    public const uint FWF_AUTOARRANGE = 0x1;
    public const uint FWF_SNAPTOGRID = 0x4;
    public const int STRRET_SIZE = 272;

    [DllImport("shlwapi.dll")]
    public static extern int StrRetToBSTR(IntPtr pstr, IntPtr pidl, [MarshalAs(UnmanagedType.BStr)] out string pbstr);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    /// <summary>The last item id of an absolute pidl, i.e. the child pidl relative to its parent folder.</summary>
    [DllImport("shell32.dll")]
    public static extern IntPtr ILFindLastID(IntPtr pidl);
}
