using System.Windows;
using System.Windows.Controls;
using BlueApex.Desktop;

namespace BlueApex.Zones;

/// <summary>The right-click menu of a zone title bar: rename, add, delete.</summary>
internal sealed class ZoneMenu : IDisposable
{
    private readonly ZoneManager _zones;
    private readonly System.Windows.Forms.ContextMenuStrip _menu = new();
    // A menu shown by a process with no foreground window would not close when the
    // user clicks elsewhere; bringing this invisible form to the front first fixes that.
    private readonly System.Windows.Forms.Form _owner = new()
    {
        ShowInTaskbar = false,
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
        StartPosition = System.Windows.Forms.FormStartPosition.Manual,
        Opacity = 0,
        Size = new System.Drawing.Size(1, 1),
    };
    private Zone? _zone;

    public ZoneMenu(ZoneManager zones)
    {
        _zones = zones;
        _menu.Items.Add("이름 바꾸기", null, (_, _) => Rename());
        _menu.Items.Add("자동 분류 규칙...", null, (_, _) => EditPatterns());
        _menu.Items.Add("새 구역", null, (_, _) => _zones.AddZone());
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add("구역 삭제", null, (_, _) => Delete());
    }

    /// <param name="screenX">Physical screen pixels.</param>
    public void Show(Zone zone, int screenX, int screenY)
    {
        _zone = zone;
        NativeMethods.SetForegroundWindow(_owner.Handle);
        _menu.Show(new System.Drawing.Point(screenX, screenY));
    }

    private void Rename()
    {
        if (_zone == null) return;
        var title = AskTitle(_zone.Title);
        if (!string.IsNullOrWhiteSpace(title))
            _zones.RenameZone(_zone, title);
    }

    private void Delete()
    {
        if (_zone == null) return;
        var answer = MessageBox.Show($"'{_zone.Title}' 구역을 삭제할까요?\n아이콘은 지금 자리에 그대로 남습니다.",
            "구역 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            _zones.RemoveZone(_zone);
    }

    private void EditPatterns()
    {
        if (_zone == null) return;
        var text = AskText("자동 분류 규칙: " + _zone.Title, string.Join(Environment.NewLine, _zone.Patterns),
            "한 줄에 하나씩. 파일 이름에 대한 와일드카드입니다.\n" +
            "예: *.url    *.lnk    Steam*    folder:*    file:*.pdf\n" +
            "새로 생기는 아이콘 중 규칙에 맞는 것이 이 구역으로 들어옵니다.", multiline: true);
        if (text == null) return;

        _zones.SetPatterns(_zone, text.Split('\n'));
        if (_zone.Patterns.Count == 0) return;

        var apply = MessageBox.Show("구역에 속하지 않은 아이콘에 지금 바로 적용할까요?", "자동 분류 규칙",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (apply == MessageBoxResult.Yes)
        {
            var count = _zones.ApplyRulesToUnowned();
            MessageBox.Show(count > 0 ? $"아이콘 {count}개를 정리했습니다." : "규칙에 맞는 아이콘이 없습니다.", "자동 분류 규칙",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Asks for a zone title in a small dialog; null if cancelled.</summary>
    public static string? AskTitle(string current) => AskText("구역 이름", current, null, multiline: false)?.Trim();

    private static string? AskText(string title, string current, string? hint, bool multiline)
    {
        var box = new TextBox
        {
            Text = current,
            MinWidth = 320,
            Margin = new Thickness(0, 0, 0, 12),
            AcceptsReturn = multiline,
            MinHeight = multiline ? 120 : 0,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        var ok = new Button { Content = "확인", IsDefault = !multiline, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Width = 80 };
        var body = new StackPanel { Margin = new Thickness(16) };
        if (hint != null)
            body.Children.Add(new TextBlock { Text = hint, Margin = new Thickness(0, 0, 0, 8), Opacity = 0.75 });
        body.Children.Add(box);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        });
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            Content = body,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        box.Loaded += (_, _) =>
        {
            box.Focus();
            if (!multiline) box.SelectAll();
        };
        return window.ShowDialog() == true ? box.Text : null;
    }

    public void Dispose()
    {
        _menu.Dispose();
        _owner.Dispose();
    }
}
