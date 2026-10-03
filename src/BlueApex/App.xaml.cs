using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using BlueApex.Desktop;
using BlueApex.Zones;

namespace BlueApex;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private ZoneManager? _zones;
    private ZoneMouseInteraction? _mouse;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\BlueApex", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        try
        {
            var desktop = DesktopHost.Find();
            _zones = new ZoneManager(desktop);
            _mouse = new ZoneMouseInteraction(desktop, _zones);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException or Win32Exception)
        {
            MessageBox.Show(ex.Message, "BlueApex", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("구역 추가", null, (_, _) => _zones.AddZone());
        menu.Items.Add("규칙대로 지금 정리", null, (_, _) =>
        {
            var count = _zones.ApplyRulesToUnowned();
            MessageBox.Show(count > 0 ? $"아이콘 {count}개를 규칙에 따라 정리했습니다." : "규칙에 맞는 아이콘이 없습니다.\n(구역 제목 우클릭 → 자동 분류 규칙)",
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
        menu.Items.Add("종료", null, (_, _) => Shutdown());
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "BlueApex",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    // "Back up now", then one entry per saved snapshot (newest first). Rebuilt each time the submenu opens.
    private void FillBackupMenu(System.Windows.Forms.ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        parent.DropDownItems.Add("지금 백업", null, (_, _) =>
        {
            var path = _zones!.BackupNow();
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
            "구역은 남지만 멤버는 모두 비워집니다.",
            "아이콘 배치 복원", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            _zones!.Restore(backup);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _mouse?.Dispose();
        _zones?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
