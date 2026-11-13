using System.Windows;
using System.Windows.Controls;
using BlueApex.Ui;

namespace BlueApex.Drawer;

/// <summary>
/// "규칙대로 지금 정리": works out what the zone rules would do, lists it (files that will really move into a
/// folder are marked), and only changes anything after the user agrees. See <see cref="DrawerManager.PlanRules"/>.
/// </summary>
internal static class RulesDialog
{
    public static void Run(DrawerManager drawer)
    {
        var plan = drawer.PlanRules();
        if (plan.Count == 0)
        {
            MessageBox.Show("규칙에 따라 옮길 아이콘이 없습니다.\n(서랍에서 구역 이름 우클릭 → 자동 분류 규칙)", "규칙대로 지금 정리",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = plan.Count(m => m.IntoFolder);
        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            Margin = new Thickness(0, 0, 0, Theme.Space3),
            Text = files > 0
                ? $"구역만 바꾸는 아이콘 {plan.Count - files}개, 폴더로 실제로 옮기는 파일 {files}개입니다.\n" +
                  "옮긴 파일은 탐색기에서 Ctrl+Z로 되돌릴 수 있고, 같은 이름이 있으면 번호를 붙여 따로 둡니다(덮어쓰지 않음)."
                : $"아이콘 {plan.Count}개의 구역을 바꿉니다. 파일은 그대로 바탕화면에 있습니다.",
        };
        var list = new StackPanel();
        foreach (var move in plan.OrderByDescending(m => m.IntoFolder).ThenBy(m => m.To.Title).ThenBy(m => m.Icon.Name))
        {
            var where = move.IntoFolder ? $"📁 {move.To.PortalPath}  (파일 이동)" : move.To.Title;
            list.Children.Add(new TextBlock
            {
                Text = $"{move.Icon.Name}   →   {where}",
                Foreground = move.IntoFolder ? Theme.Text : Theme.TextDim,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 1),
            });
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 320, Width = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var (buttons, ok, _) = Parts.DialogButtons();
        ok.Content = "정리";
        buttons.Margin = new Thickness(0, Theme.Space3, 0, 0);
        var window = Parts.Dialog("규칙대로 지금 정리", new StackPanel
        {
            Margin = new Thickness(Theme.Space4),
            Children = { summary, scroll, buttons },
        });
        ok.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true) return;

        var (zoned, moved, failed) = drawer.ApplyRules(plan);
        var result = $"구역을 바꾼 아이콘 {zoned}개" + (files > 0 ? $", 폴더로 옮긴 파일 {moved}개" : "") + ".";
        if (failed > 0) result += $"\n옮기지 못한 파일 {failed}개는 그대로 두었습니다(취소했거나 다른 프로그램이 쓰는 중).";
        MessageBox.Show(result, "규칙대로 지금 정리", MessageBoxButton.OK, failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
