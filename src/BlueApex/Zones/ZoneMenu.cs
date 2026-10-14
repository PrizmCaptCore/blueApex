using System.Windows;
using System.Windows.Controls;
using BlueApex.Ui;

namespace BlueApex.Zones;

/// <summary>
/// The small text-input dialogs the drawer and widgets use (zone name, rules, new folder,
/// memo...). The name is historical: it used to be the old desktop zones' title-bar menu.
/// </summary>
internal static class ZoneMenu
{
    /// <summary>Asks for a zone title in a small dialog; null if cancelled.</summary>
    public static string? AskTitle(string current) => AskText("구역 이름", current, null, multiline: false)?.Trim();

    /// <summary>Help text shown above the pattern editor.</summary>
    public const string PatternHint =
        "한 줄에 하나씩. 파일 이름에 대한 와일드카드입니다.\n" +
        "예: *.url    *.lnk    Steam*    folder:*    file:*.pdf\n" +
        "새로 생기는 아이콘 중 규칙에 맞는 것이 이 구역으로 들어옵니다.";

    /// <summary>Shows a one-box dialog; the entered text, or null if cancelled.</summary>
    public static string? AskText(string title, string current, string? hint, bool multiline)
    {
        var box = new TextBox
        {
            Text = current,
            MinWidth = 320,
            Margin = new Thickness(0, 0, 0, Theme.Space3),
            AcceptsReturn = multiline,
            MinHeight = multiline ? 120 : 0,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        var (buttons, ok, _) = Parts.DialogButtons();
        ok.IsDefault = !multiline; // in a multiline box Enter should add a line, not close
        var body = new StackPanel { Margin = new Thickness(Theme.Space4) };
        if (hint != null)
            body.Children.Add(new TextBlock { Text = hint, Foreground = Theme.TextDim, Margin = new Thickness(0, 0, 0, Theme.Space2) });
        body.Children.Add(box);
        body.Children.Add(buttons);
        var window = Parts.Dialog(title, body);
        ok.Click += (_, _) => window.DialogResult = true;
        box.Loaded += (_, _) =>
        {
            box.Focus();
            if (!multiline) box.SelectAll();
        };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
