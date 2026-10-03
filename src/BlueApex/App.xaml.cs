using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using BlueApex.Desktop;
using BlueApex.Drawer;
using BlueApex.Zones;

namespace BlueApex;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private DrawerManager? _drawer;
    private DrawerWindow? _window;
    private Hotkey? _hotkey;
    private DesktopButton? _button;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "BlueApex.exe --exit" asks the running instance to quit cleanly (icons come back),
        // which a script cannot do by killing the process.
        var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BlueApex.Exit");
        if (e.Args.Contains("--exit"))
        {
            exitSignal.Set();
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, @"Local\BlueApex", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, -1, true);

        try
        {
            _drawer = new DrawerManager();
            _window = new DrawerWindow(_drawer);
            _hotkey = new Hotkey(_window, _drawer.Hotkey, ToggleDrawer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or Win32Exception or FormatException)
        {
            MessageBox.Show(ex.Message, "BlueApex", MessageBoxButton.OK, MessageBoxImage.Error);
            _drawer?.Dispose();
            Shutdown(1);
            return;
        }

        // The on-desktop button needs the desktop window layout; without it the hotkey and tray still work.
        try
        {
            _button = new DesktopButton(DesktopHost.Find(), _drawer, ToggleDrawer);
        }
        catch (Exception ex) when (ex is NotSupportedException or Win32Exception)
        {
            Log.Write($"desktop button unavailable: {ex.Message}");
        }

        // Log-off / shutdown: run the normal exit path so parked icons come back.
        SessionEnding += (_, _) => Shutdown();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("서랍 열기", null, (_, _) => ToggleDrawer());
        menu.Items.Add("규칙대로 지금 정리", null, (_, _) =>
        {
            var count = _drawer.ApplyRulesToUnassigned();
            MessageBox.Show(count > 0 ? $"아이콘 {count}개를 규칙에 따라 정리했습니다." : "규칙에 맞는 아이콘이 없습니다.\n(서랍에서 구역 제목 우클릭 → 자동 분류 규칙)",
                "BlueApex", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        menu.Items.Add("레이아웃 파일 열기", null, (_, _) => Process.Start("notepad.exe", LayoutStore.FilePath));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var backups = new System.Windows.Forms.ToolStripMenuItem("아이콘 배치 백업/복원");
        backups.DropDownOpening += (_, _) => FillBackupMenu(backups);
        FillBackupMenu(backups);
        menu.Items.Add(backups);

        var autostart = new System.Windows.Forms.ToolStripMenuItem("시작 시 자동 실행") { CheckOnClick = true, Checked = Autostart.IsEnabled };
        autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);
        menu.Items.Add(autostart);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("종료 (아이콘을 바탕화면으로 되돌림)", null, (_, _) => Shutdown());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = $"BlueApex ({_drawer.Hotkey}: 서랍)",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left) ToggleDrawer();
        };
        _drawer.Notice += (title, message) => _trayIcon.ShowBalloonTip(3000, title, message, System.Windows.Forms.ToolTipIcon.Info);

        if (e.Args.Contains("--drawer")) // handy while developing: start with the drawer open
            _window.Open();
    }

    private void ToggleDrawer()
    {
        if (_window == null) return;
        if (!_window.IsVisible && FullscreenAppInFront()) return; // don't pop over a game
        _window.Toggle();
    }

    // A foreground window covering the whole primary screen that is not the desktop itself.
    private static bool FullscreenAppInFront()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || !NativeMethods.GetWindowRect(foreground, out var rect)) return false;
        var className = new StringBuilder(64);
        NativeMethods.GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW") return false;
        return rect.Left <= 0 && rect.Top <= 0
               && rect.Right >= NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN)
               && rect.Bottom >= NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
    }

    // "Back up now", then one entry per saved snapshot (newest first). Rebuilt each time the submenu opens.
    private void FillBackupMenu(System.Windows.Forms.ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        parent.DropDownItems.Add("지금 백업", null, (_, _) =>
        {
            var path = BackupStore.Save(_drawer!.Icons.ToList());
            MessageBox.Show($"백업했습니다.\n{path}", "아이콘 배치 백업", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        parent.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());

        var list = BackupStore.List();
        if (list.Count == 0)
            parent.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("(백업 없음)") { Enabled = false });
        foreach (var path in list)
            parent.DropDownItems.Add($"{BackupStore.Describe(path)} 배치로 복원", null, (_, _) => RestoreBackup(path));
    }

    private void RestoreBackup(string path)
    {
        var backup = BackupStore.Load(path);
        var answer = MessageBox.Show(
            $"{backup.Time:yyyy-MM-dd HH:mm:ss} 시점의 배치로 아이콘 {backup.Icons.Count}개를 되돌릴까요?\n" +
            "되돌린 아이콘은 전부 바탕화면에 꺼내 둔 상태가 됩니다.",
            "아이콘 배치 복원", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            _drawer!.Restore(backup);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _hotkey?.Dispose();
        _button?.Dispose();
        _window?.Close();
        _drawer?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
