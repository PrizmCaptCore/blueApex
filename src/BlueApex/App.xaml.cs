using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using BlueApex.Desktop;
using BlueApex.Drawer;
using BlueApex.Widgets;
using BlueApex.Zones;

namespace BlueApex;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private readonly Updater _updater = new();
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    private async void CheckUpdates(bool manual)
    {
        if (!manual && !_drawer!.CheckUpdates) return;
        try
        {
            var release = await _updater.CheckAsync();
            if (manual && release == null)
                MessageBox.Show($"최신 버전입니다. (현재 {Updater.Current.ToString(3)})", "BlueApex", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Log.Write($"update check failed: {ex.Message}");
            if (manual) MessageBox.Show("업데이트를 확인하지 못했습니다.\n" + ex.Message, "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void InstallUpdate()
    {
        if (_updater.Available is not { } release) return;
        if (MessageBox.Show($"BlueApex {release.Tag}을(를) 설치할까요?\n앱이 종료되고(숨긴 아이콘은 복원됨) 설치 후 새 버전이 다시 켜집니다.",
                "업데이트", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _updater.InstallAsync(release);
            Shutdown(); // the installer is waiting for us to let go of the files
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException or Win32Exception)
        {
            Log.Write($"update install failed: {ex}");
            MessageBox.Show("업데이트 파일을 받지 못했습니다.\n" + ex.Message + $"\n\n직접 받기: {release.PageUrl}", "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private DrawerManager? _drawer;
    private DrawerWindow? _window;
    private Hotkey? _hotkey;
    private DesktopButton? _button;
    private DesktopLayerInput? _layerInput;
    private WidgetHost? _widgets;

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

        // The desktop layer (button, widgets) needs the desktop window layout; without it the hotkey and tray still work.
        try
        {
            var desktop = DesktopHost.Find();
            _layerInput = new DesktopLayerInput(desktop, _drawer.PollNow);
            _button = new DesktopButton(desktop, _drawer, ToggleDrawer);
            _layerInput.Items.Add(_button);
            _widgets = new WidgetHost(desktop, _layerInput, _drawer);
        }
        catch (Exception ex) when (ex is NotSupportedException or Win32Exception)
        {
            Log.Write($"desktop layer unavailable: {ex.Message}");
        }

        // Log-off / shutdown: run the normal exit path so hidden icons come back.
        SessionEnding += (_, _) => Shutdown();

        // A bug must not take the desktop icons down with it: log, keep running, and if the
        // process is going down anyway, unhide everything first.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"unhandled: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write($"fatal: {args.ExceptionObject}");
            try { _drawer?.UnhideAll(); } catch { /* best effort on the way down */ }
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("서랍 열기", null, (_, _) => ToggleDrawer());
        menu.Items.Add("규칙대로 지금 정리", null, (_, _) =>
        {
            var count = _drawer.ApplyRulesToUnassigned();
            MessageBox.Show(count > 0 ? $"아이콘 {count}개를 규칙에 따라 정리했습니다." : "규칙에 맞는 아이콘이 없습니다.\n(서랍에서 구역 제목 우클릭 → 자동 분류 규칙)",
                "BlueApex", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        menu.Items.Add("레이아웃 파일 열기", null, (_, _) => Process.Start("notepad.exe", LayoutStore.FilePath));
        if (_widgets != null)
        {
            var add = new System.Windows.Forms.ToolStripMenuItem("위젯 추가");
            foreach (var kind in _widgets.Kinds)
                add.DropDownItems.Add(kind.DisplayName, null, (_, _) => _widgets.Add(kind));
            menu.Items.Add(add);
        }
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var backups = new System.Windows.Forms.ToolStripMenuItem("아이콘 배치 백업/복원");
        backups.DropDownOpening += (_, _) => FillBackupMenu(backups);
        FillBackupMenu(backups);
        menu.Items.Add(backups);

        var showApps = new System.Windows.Forms.ToolStripMenuItem("서랍에 설치된 앱 전부 표시") { CheckOnClick = true, Checked = _drawer.ShowApps };
        showApps.CheckedChanged += (_, _) => _drawer.ShowApps = showApps.Checked;
        menu.Opening += (_, _) => showApps.Checked = _drawer.ShowApps;
        menu.Items.Add(showApps);
        var showGames = new System.Windows.Forms.ToolStripMenuItem("서랍에 게임 라이브러리 표시 (Steam / Epic / GOG)") { CheckOnClick = true, Checked = _drawer.ShowGames };
        showGames.CheckedChanged += (_, _) => _drawer.ShowGames = showGames.Checked;
        menu.Opening += (_, _) => showGames.Checked = _drawer.ShowGames;
        menu.Items.Add(showGames);

        var autostart = new System.Windows.Forms.ToolStripMenuItem("시작 시 자동 실행") { CheckOnClick = true, Checked = Autostart.IsEnabled };
        autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);
        menu.Items.Add(autostart);

        // Updates: a daily check (switchable) plus a manual one; "설치" appears once a newer release is known.
        var install = new System.Windows.Forms.ToolStripMenuItem("업데이트 설치") { Visible = false };
        install.Click += (_, _) => InstallUpdate();
        var check = new System.Windows.Forms.ToolStripMenuItem($"업데이트 확인 (현재 {Updater.Current.ToString(3)})");
        check.Click += (_, _) => CheckUpdates(manual: true);
        var autoCheck = new System.Windows.Forms.ToolStripMenuItem("업데이트 자동 확인 (하루 한 번)") { CheckOnClick = true, Checked = _drawer.CheckUpdates };
        autoCheck.CheckedChanged += (_, _) => _drawer.CheckUpdates = autoCheck.Checked;
        menu.Opening += (_, _) =>
        {
            autoCheck.Checked = _drawer.CheckUpdates;
            install.Visible = _updater.Available != null;
            if (_updater.Available is { } r) install.Text = $"업데이트 설치 ({r.Tag})";
        };
        menu.Items.Add(install);
        menu.Items.Add(check);
        menu.Items.Add(autoCheck);

        menu.Items.Add("공용 바탕화면 권한 설정 (관리자 권한)", null, (_, _) => GrantPublicDesktopAccess());
        menu.Items.Add("숨긴 아이콘 모두 보이기", null, (_, _) =>
        {
            _drawer.UnhideAll();
            MessageBox.Show("이 앱이 숨긴 아이콘을 모두 다시 보이게 했습니다.\n(앱이 켜져 있는 동안 고정되지 않은 아이콘은 곧 다시 서랍으로 들어갑니다.)",
                "BlueApex", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("종료 (숨긴 아이콘을 다시 보임)", null, (_, _) => Shutdown());

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
        _updater.Found += r => _trayIcon.ShowBalloonTip(6000, $"BlueApex {r.Tag} 업데이트",
            "트레이 메뉴의 '업데이트 설치'를 누르면 받아서 설치합니다. (앱이 잠시 꺼졌다가 새 버전으로 켜집니다)", System.Windows.Forms.ToolTipIcon.Info);
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _updateTimer.Tick += (_, _) => CheckUpdates(manual: false);
        _updateTimer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Dispatcher.BeginInvoke(() => CheckUpdates(manual: false)));

        if (LayoutStore.LastLoadError != null)
            MessageBox.Show(LayoutStore.LastLoadError, "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (DesktopCatalog.ExplorerShowsHiddenFiles())
            _trayIcon.ShowBalloonTip(6000, "숨김 파일 표시가 켜져 있습니다",
                "탐색기 옵션에서 숨김 파일을 표시하는 동안에는 서랍에 넣은 아이콘이 바탕화면에 흐리게 남습니다.", System.Windows.Forms.ToolTipIcon.Warning);
        if (_drawer.CannotHide.Count > 0)
            Dispatcher.BeginInvoke(GrantPublicDesktopAccess);

        if (e.Args.Contains("--drawer")) // handy while developing: start with the drawer open
            _window.Open();
    }

    // Public-desktop shortcuts (Steam, Chrome... made by installers) need one elevated
    // permission change before they can be hidden. Asked once per run while any remain.
    private void GrantPublicDesktopAccess()
    {
        var pending = _drawer!.CannotHide.Count;
        var answer = MessageBox.Show(
            (pending > 0 ? $"공용 바탕화면의 아이콘 {pending}개는 권한이 없어 서랍에 넣을 수 없습니다.\n" : "") +
            "관리자 권한으로 공용 바탕화면(C:\\Users\\Public\\Desktop)에 이 계정의 속성 변경 권한을 한 번 부여할까요?\n" +
            "앞으로 설치되는 바로가기에도 적용됩니다.",
            "BlueApex", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        if (!_drawer.GrantPublicDesktopAccess())
            MessageBox.Show("권한을 부여하지 못했습니다(취소했거나 실패).", "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var path = _drawer!.BackupNow();
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
        _layerInput?.Dispose();
        _widgets?.Dispose();
        _button?.Dispose();
        _window?.Close();
        _drawer?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
