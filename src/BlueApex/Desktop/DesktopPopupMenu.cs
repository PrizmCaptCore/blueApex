namespace BlueApex.Desktop;

/// <summary>
/// Shows a context menu at a screen point from a process that has no foreground
/// window. Without the invisible owner form being brought to the front first,
/// the menu would not close when the user clicks elsewhere.
/// </summary>
internal static class DesktopPopupMenu
{
    private static readonly System.Windows.Forms.Form Owner = new()
    {
        ShowInTaskbar = false,
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
        StartPosition = System.Windows.Forms.FormStartPosition.Manual,
        Opacity = 0,
        Size = new System.Drawing.Size(1, 1),
    };

    // One menu, rebuilt per call. It is never disposed while shown: WinForms' menu
    // message filter still touches it after Closed fires, and disposing it there crashes.
    private static readonly System.Windows.Forms.ContextMenuStrip Menu = new();

    /// <param name="items">(label, action); a null label is a separator.</param>
    public static void Show(int screenX, int screenY, params (string? Label, Action? Action)[] items)
    {
        Menu.Items.Clear();
        foreach (var (label, action) in items)
        {
            if (label == null) Menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            else Menu.Items.Add(label, null, (_, _) => action?.Invoke());
        }
        NativeMethods.SetForegroundWindow(Owner.Handle);
        Menu.Show(new System.Drawing.Point(screenX, screenY));
    }
}
