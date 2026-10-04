using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueApex.Desktop;
using BlueApex.Drawer;
using BlueApex.Ui;

namespace BlueApex.Widgets;

/// <summary>
/// The gear-button dialog of a zone/game widget: scroll direction, re-reading the
/// library, and a picture of the user's own choice for any tile (kept in
/// %AppData%\BlueApex\covers and used by the drawer too).
/// </summary>
internal static class ZoneWidgetSettingsDialog
{
    /// <param name="items">What the widget shows, in order.</param>
    /// <param name="horizontal">Current scroll direction; the dialog returns the chosen one through <paramref name="setHorizontal"/>.</param>
    /// <param name="refresh">Re-reads the launcher library (or the desktop) so removed or added items show up.</param>
    public static void Show(DrawerManager drawer, string title, IReadOnlyList<DesktopIcon> items,
        bool horizontal, Action<bool> setHorizontal, Action refresh)
    {
        var icons = new IconLoader();
        var list = new ListBox
        {
            Width = 360,
            Height = 320,
            Background = Theme.Input,
            Foreground = Theme.Text,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, Theme.Space2),
        };
        var window = Parts.Dialog(title + " 설정", new StackPanel());

        void Fill()
        {
            list.Items.Clear();
            foreach (var icon in items)
            {
                var image = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, Theme.Space2, 0) };
                image.Source = icons.GetAsync(icon.Id, 32, false, window.Dispatcher, ready => image.Source = ready);
                var custom = drawer.CustomImageOf(icon.Id) != null;
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        image,
                        new TextBlock { Text = icon.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Text },
                    },
                };
                if (custom)
                    row.Children.Add(new TextBlock { Text = "  (내 이미지)", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextDim });
                list.Items.Add(new ListBoxItem { Content = row, Tag = icon, Padding = new Thickness(6, 4, 6, 4) });
            }
        }
        Fill();

        var pick = new Button { Content = "이미지 선택...", IsEnabled = false, Margin = new Thickness(0, 0, Theme.Space2, 0) };
        var reset = new Button { Content = "기본 이미지로", IsEnabled = false, Margin = new Thickness(0, 0, Theme.Space2, 0) };
        list.SelectionChanged += (_, _) =>
        {
            var selected = (list.SelectedItem as ListBoxItem)?.Tag as DesktopIcon;
            pick.IsEnabled = selected != null;
            reset.IsEnabled = selected != null && drawer.CustomImageOf(selected.Id) != null;
        };
        pick.Click += (_, _) =>
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is not DesktopIcon icon) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"{icon.Name}의 이미지",
                Filter = "이미지|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|모든 파일|*.*",
            };
            if (dialog.ShowDialog(window) != true) return;
            try
            {
                drawer.SetCustomImage(icon.Id, dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(window, "이미지를 복사하지 못했습니다.\n" + ex.Message, "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var index = list.SelectedIndex;
            icons = new IconLoader();
            Fill();
            list.SelectedIndex = index;
        };
        reset.Click += (_, _) =>
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is not DesktopIcon icon) return;
            drawer.SetCustomImage(icon.Id, null);
            var index = list.SelectedIndex;
            icons = new IconLoader();
            Fill();
            list.SelectedIndex = index;
        };

        var sideways = new CheckBox { Content = "가로로 스크롤 (열 단위)", IsChecked = horizontal, Margin = new Thickness(0, 0, 0, Theme.Space2) };
        sideways.Click += (_, _) => setHorizontal(sideways.IsChecked == true);
        var reload = new Button { Content = "라이브러리 다시 읽기" };
        reload.Click += (_, _) => refresh();
        var hint = new TextBlock
        {
            Text = "항목을 고르고 '이미지 선택'으로 원하는 그림을 넣습니다. 서랍 타일에도 같이 쓰입니다.\n" +
                   "'라이브러리 다시 읽기'는 지워지거나 새로 설치된 게임을 바로 반영합니다 (목록은 잠시 뒤 갱신).",
            Foreground = Theme.TextDim,
            FontSize = Theme.FontSmall,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, Theme.Space2),
        };
        var (buttons, ok, cancel) = Parts.DialogButtons();
        ok.Content = "닫기";
        cancel.Visibility = Visibility.Collapsed;
        ok.Click += (_, _) => window.Close();

        var body = new StackPanel
        {
            Margin = new Thickness(Theme.Space4),
            Children =
            {
                hint,
                sideways,
                list,
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, Theme.Space3), Children = { pick, reset, reload } },
                buttons,
            },
        };
        window.Content = body;
        // Items can change underneath (a refresh finished): rebuild the list then.
        void OnChanged() => window.Dispatcher.BeginInvoke(Fill);
        drawer.Changed += OnChanged;
        window.Closed += (_, _) => drawer.Changed -= OnChanged;
        window.ShowDialog();
    }
}
