using System.IO;
using System.Runtime.InteropServices;

namespace BlueApex.Desktop;

/// <summary>
/// Writes .lnk shortcuts. For installed apps the link points at the app's shell
/// item (the same kind of shortcut Windows makes when you drag an app from the
/// Start menu to the desktop), which works for Store apps and classic programs alike.
/// </summary>
internal static class ShortcutWriter
{
    /// <summary>Creates "&lt;name&gt;.lnk" in the folder for the app id; returns the path. A taken name gets " (2)", " (3)"...</summary>
    public static string CreateAppShortcut(string appId, string name, string folder)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var path = Path.Combine(folder, name + ".lnk");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} ({n}).lnk");

        var pidl = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(AppCatalog.ParsingName(appId), IntPtr.Zero, out pidl, 0, out _));
            var link = (IShellLinkW)new ShellLink();
            link.SetIDList(pidl);
            ((IPersistFile)link).Save(path, true);
            Marshal.ReleaseComObject(link);
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
        return path;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void _GetPath();
        void _GetIDList();
        void SetIDList(IntPtr pidl);
        // the remaining members (description, working directory, arguments, hotkey, show command,
        // icon, relative path, resolve, path) are not needed here
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void _GetClassID();
        void _IsDirty();
        void _Load();
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void _SaveCompleted();
        void _GetCurFile();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);
}
