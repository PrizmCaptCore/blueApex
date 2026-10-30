using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using BlueApex.Desktop;
using BlueApex.Ui;

namespace BlueApex.Drawer;

/// <summary>
/// "디스코드 음성 연결 (고급)": walks the user through creating their own Discord application
/// (Discord only grants voice control to an application's owner) and stores its Client ID and
/// Client Secret. The first connection then shows Discord's own authorize prompt.
/// </summary>
internal static class DiscordSetup
{
    private const string PortalUrl = "https://discord.com/developers/applications";

    public static void Show(DrawerManager drawer, DiscordVoice? voice)
    {
        var steps = new TextBlock { Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, Width = 460, Margin = new Thickness(0, 0, 0, Theme.Space3) };
        var link = new Hyperlink(new Run("Discord 개발자 포털")) { Foreground = Theme.Accent };
        link.Click += (_, _) => Process.Start(new ProcessStartInfo(PortalUrl) { UseShellExecute = true });
        steps.Inlines.Add(new Run("독에 음성 채널·마이크·헤드셋을 띄우려면 본인 디스코드 앱이 하나 필요합니다(무료, 한 번만).\n\n1. "));
        steps.Inlines.Add(link);
        steps.Inlines.Add(new Run("에서 \"New Application\"으로 앱을 만듭니다(이름은 아무거나).\n" +
                                  "2. 왼쪽 OAuth2 메뉴에서 Client ID를 복사하고, \"Reset Secret\"으로 Client Secret을 받아 복사합니다.\n" +
                                  "3. 같은 화면 Redirects에 http://localhost 를 추가하고 저장합니다.\n" +
                                  "4. 아래에 붙여 넣고 \"연결\"을 누르면, 디스코드 앱에 승인 창이 뜹니다. \"승인\"을 누르면 끝입니다.\n\n" +
                                  "Secret과 토큰은 이 Windows 계정에서만 풀리도록 암호화해서 저장합니다."));

        var clientId = new TextBox { Text = drawer.DiscordClientId ?? "", MinWidth = 460, Margin = new Thickness(0, 2, 0, Theme.Space2) };
        var secret = new PasswordBox { MinWidth = 460, Margin = new Thickness(0, 2, 0, Theme.Space3), Background = Theme.Input, Foreground = Theme.Text, BorderBrush = Theme.Border, Padding = new Thickness(8, 5, 8, 5) };
        var status = new TextBlock { Foreground = Theme.TextDim, Margin = new Thickness(0, 0, 0, Theme.Space3), TextWrapping = TextWrapping.Wrap, Width = 460 };
        void ShowStatus() => status.Text = "상태: " + (voice == null ? "바탕화면 층을 쓸 수 없어 꺼져 있음"
            : voice.Connected ? (voice.Current != null ? $"연결됨 · {voice.Current.ChannelName}" : "연결됨 (음성 채널에 없음)")
            : voice.Problem ?? "연결 중...");
        ShowStatus();

        var connect = new Button { Content = "연결", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, Theme.Space2, 0) };
        var unlink = new Button { Content = "연결 해제", Width = 90, Margin = new Thickness(0, 0, Theme.Space2, 0) };
        var close = new Button { Content = "닫기", Width = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { connect, unlink, close } };

        var body = new StackPanel
        {
            Margin = new Thickness(Theme.Space4),
            Children =
            {
                steps,
                new TextBlock { Text = "Client ID", Foreground = Theme.TextDim },
                clientId,
                new TextBlock { Text = drawer.DiscordCredentials != null ? "Client Secret (저장돼 있음 · 바꿀 때만 입력)" : "Client Secret", Foreground = Theme.TextDim },
                secret,
                status,
                buttons,
            },
        };
        var window = Parts.Dialog("디스코드 음성 연결 (고급)", body);

        connect.Click += (_, _) =>
        {
            var id = clientId.Text.Trim();
            var newSecret = secret.Password.Trim();
            if (!ulong.TryParse(id, out _))
            {
                status.Text = "Client ID는 숫자입니다. OAuth2 화면의 Client ID를 붙여 넣어 주세요.";
                return;
            }
            var keep = drawer.DiscordCredentials;
            if (newSecret.Length == 0 && (keep == null || keep.ClientId != id))
            {
                status.Text = "Client Secret을 넣어 주세요.";
                return;
            }
            drawer.SetDiscordApp(id, newSecret.Length > 0 ? newSecret : keep!.ClientSecret);
            voice?.Reconnect();
            status.Text = "상태: 연결 중... 디스코드 앱에 승인 창이 뜨면 \"승인\"을 눌러 주세요.";
        };
        unlink.Click += (_, _) =>
        {
            drawer.SetDiscordApp(null, null);
            voice?.Reconnect();
            clientId.Text = "";
            secret.Password = "";
            ShowStatus();
        };
        close.Click += (_, _) => window.Close();
        Action onChange = () => window.Dispatcher.BeginInvoke(ShowStatus);
        if (voice != null)
        {
            voice.Changed += onChange;
            window.Closed += (_, _) => voice.Changed -= onChange;
        }
        window.ShowDialog();
    }
}
