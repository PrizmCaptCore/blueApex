using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using BlueApex.Desktop;
using BlueApex.Ui;

namespace BlueApex.Drawer;

/// <summary>
/// "Spotify 연결 (고급)": walks the user through creating their own Spotify developer app (Spotify lets
/// only an app's owner use an unreviewed app) and stores its Client ID. "연결" then opens Spotify's own
/// consent page in the browser. With no Client ID saved, the dock shows no Spotify menu at all.
/// </summary>
internal static class SpotifySetup
{
    private const string DashboardUrl = "https://developer.spotify.com/dashboard";

    public static void Show(DrawerManager drawer, Spotify spotify)
    {
        var steps = new TextBlock { Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, Width = 460, Margin = new Thickness(0, 0, 0, Theme.Space2) };
        var link = new Hyperlink(new Run("Spotify 개발자 대시보드")) { Foreground = Theme.Accent };
        link.Click += (_, _) => Process.Start(new ProcessStartInfo(DashboardUrl) { UseShellExecute = true });
        steps.Inlines.Add(new Run("독 우클릭 메뉴에서 Spotify를 검색해 틀고, 내 재생목록·좋아요 곡을 고르려면 본인 Spotify 개발자 앱이 하나 필요합니다" +
                                  "(무료, 한 번만. Spotify Premium 계정만 됩니다). 소리는 BlueApex 내장 플레이어에서 나며 Spotify 앱은 필요 없습니다.\n\n1. "));
        steps.Inlines.Add(link);
        steps.Inlines.Add(new Run("에 로그인하고 \"Create app\"으로 앱을 만듭니다(이름·설명은 아무거나).\n" +
                                  "2. Redirect URIs에 아래 주소를 그대로 넣고 \"Add\"를 누릅니다.\n" +
                                  "3. 사용할 API로 \"Web API\"와 \"Web Playback SDK\"를 체크하고 저장합니다.\n" +
                                  "4. 앱 화면의 Client ID를 복사해 아래에 붙여 넣고 \"연결\"을 누르면 브라우저에 Spotify 승인 화면이 뜹니다.\n\n" +
                                  "Client secret은 쓰지 않습니다. 로그인 토큰은 이 Windows 계정에서만 풀리도록 암호화해서 저장합니다.\n" +
                                  "Spotify 규정상 연결은 6개월 뒤에 끝나며, 그때 이 창에서 \"연결\"을 다시 누르면 됩니다(Client ID는 그대로)."));

        // Read-only but selectable, so the address can be copied exactly.
        var redirect = new TextBox { Text = Spotify.RedirectUri, IsReadOnly = true, MinWidth = 460, Margin = new Thickness(0, 2, 0, Theme.Space2) };
        var clientId = new TextBox { Text = drawer.SpotifyClientId ?? "", MinWidth = 460, Margin = new Thickness(0, 2, 0, Theme.Space3) };
        var status = new TextBlock { Foreground = Theme.TextDim, Margin = new Thickness(0, 0, 0, Theme.Space3), TextWrapping = TextWrapping.Wrap, Width = 460 };
        // Spotify ends every sign-in 6 months after "연결", however often it was used; say when, so it is no surprise.
        void ShowStatus() => status.Text = "상태: " + (
            !spotify.Linked ? (spotify.Configured ? "Client ID만 저장됨 (\"연결\"을 눌러 주세요)" : "연결 안 됨")
            : drawer.SpotifyLinkEnds is { } ends ? $"연결됨 · {ends.ToLocalTime():yyyy-MM-dd}쯤 Spotify가 연결을 끝냅니다(규정상 6개월). 그때 \"연결\"만 다시 누르면 됩니다."
            : "연결됨");
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
                new TextBlock { Text = "Redirect URI (대시보드에 넣을 주소)", Foreground = Theme.TextDim },
                redirect,
                new TextBlock { Text = "Client ID", Foreground = Theme.TextDim },
                clientId,
                status,
                buttons,
            },
        };
        var window = Parts.Dialog("Spotify 연결 (고급)", body);

        connect.Click += async (_, _) =>
        {
            var id = clientId.Text.Trim();
            if (!Regex.IsMatch(id, "^[0-9a-fA-F]{32}$"))
            {
                status.Text = "Client ID는 숫자와 a~f로 된 32글자입니다. 대시보드 앱 화면의 Client ID를 붙여 넣어 주세요.";
                return;
            }
            if (id != drawer.SpotifyClientId)
            {
                spotify.Disconnect(); // the old sign-in belonged to the old app
                drawer.SetSpotifyApp(id);
            }
            status.Text = "상태: 브라우저에 뜬 Spotify 화면에서 \"동의\"를 눌러 주세요...";
            window.Topmost = false; // let the browser come in front of this window
            try
            {
                await spotify.ConnectAsync();
                ShowStatus();
            }
            catch (SpotifyException ex)
            {
                status.Text = "상태: " + ex.Message;
            }
        };
        unlink.Click += (_, _) =>
        {
            spotify.Disconnect();
            drawer.SetSpotifyApp(null);
            clientId.Text = "";
            ShowStatus();
        };
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => spotify.CancelConnect();
        window.ShowDialog();
    }
}
