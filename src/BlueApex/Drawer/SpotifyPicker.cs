using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueApex.Desktop;
using BlueApex.Ui;

namespace BlueApex.Drawer;

/// <summary>
/// The small list window behind the dock's Spotify menu items: search results, the user's playlists, or
/// liked songs. Clicking a row plays it at once (on the built-in player) and the window stays open so
/// another can be picked; Esc closes it. Only one is open at a time. What plays shows up in the dock's
/// media slot by itself.
/// </summary>
internal sealed class SpotifyPicker
{
    private const double ListWidth = 460, ListHeight = 420;

    /// <summary>One row: what it says, what clicking does, and what to report when that worked.</summary>
    private sealed record Entry(string Title, string Subtitle, Func<Task> Pick, string Done);

    private static SpotifyPicker? _current;

    private readonly Window _window;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status;
    private bool _busy; // a pick is on its way to Spotify; further clicks wait for it

    private SpotifyPicker(string title, UIElement? top)
    {
        _status = new TextBlock { Foreground = Theme.TextDim, TextWrapping = TextWrapping.Wrap, Width = ListWidth, Margin = new Thickness(0, 0, 0, Theme.Space2) };
        var scroll = new ScrollViewer { Content = _rows, Width = ListWidth, Height = ListHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new StackPanel { Margin = new Thickness(Theme.Space4) };
        if (top != null) body.Children.Add(top);
        body.Children.Add(_status);
        body.Children.Add(scroll);
        _window = Parts.Dialog(title, body);
        _window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _window.Close();
        };
        _window.Closed += (_, _) =>
        {
            if (_current == this) _current = null;
        };
    }

    // --- the three menu items ---

    public static void Search(Spotify spotify)
    {
        var box = new TextBox { MinWidth = ListWidth - 90 - Theme.Space2 };
        var go = new Button { Content = "검색", Width = 90, IsDefault = true, Margin = new Thickness(Theme.Space2, 0, 0, 0) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, Theme.Space3) };
        DockPanel.SetDock(go, Dock.Right);
        top.Children.Add(go);
        top.Children.Add(box);

        var picker = new SpotifyPicker("Spotify: 검색해서 틀기", top);
        picker._status.Text = "곡·앨범·재생목록 이름을 넣고 Enter를 누르세요. 결과를 누르면 바로 재생합니다.";
        go.Click += (_, _) => picker.Load(async () => (await spotify.SearchAsync(box.Text)).Select(i => Playable(spotify, i)).ToList(),
            empty: "찾은 것이 없습니다.", hint: "누르면 바로 재생합니다.");
        picker.Open();
        box.Focus();
    }

    public static void Playlists(Spotify spotify)
    {
        var picker = new SpotifyPicker("Spotify: 내 재생목록", null);
        picker.Load(async () => (await spotify.PlaylistsAsync()).Select(i => Playable(spotify, i)).ToList(),
            empty: "라이브러리에 재생목록이 없습니다.", hint: "누르면 그 재생목록을 처음부터 재생합니다.");
        picker.Open();
    }

    public static void Liked(Spotify spotify)
    {
        var picker = new SpotifyPicker("Spotify: 좋아요 표시한 곡", null);
        picker.Load(async () =>
        {
            var tracks = await spotify.LikedAsync();
            return tracks.Select((t, index) => new Entry(t.Title, t.Subtitle, () => spotify.PlayAsync(tracks, index), "재생: " + t.Title)).ToList();
        }, empty: "좋아요 표시한 곡이 없습니다.", hint: "최근 50곡입니다. 누른 곡부터 아래로 이어서 재생합니다.");
        picker.Open();
    }

    private static Entry Playable(Spotify spotify, SpotifyItem item) =>
        new(item.Title, item.Subtitle, () => spotify.PlayAsync(item), "재생: " + item.Title);

    // --- the window ---

    private void Open()
    {
        _current?._window.Close();
        _current = this;
        _window.Show();
        _window.Activate();
    }

    // "async void" is right for something started by a click: nobody waits for it, so every failure
    // must be caught here and shown in the status line instead of escaping.
    private async void Load(Func<Task<List<Entry>>> source, string empty, string hint)
    {
        _status.Text = "불러오는 중...";
        try
        {
            var entries = await source();
            _rows.Children.Clear();
            foreach (var entry in entries) _rows.Children.Add(Row(entry));
            _status.Text = entries.Count == 0 ? empty : hint;
        }
        catch (SpotifyException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async void Pick(Entry entry)
    {
        // One at a time: a double click used to send the same "play" twice, and two starts of the player raced.
        if (_busy) return;
        _busy = true;
        _status.Text = "Spotify에 보내는 중...";
        try
        {
            await entry.Pick();
            _status.Text = entry.Done;
        }
        catch (SpotifyException ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private Border Row(Entry entry)
    {
        var text = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = entry.Title, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = entry.Subtitle, Foreground = Theme.TextDim, FontSize = Theme.FontSmall, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        var row = new Border
        {
            Child = text,
            Background = Brushes.Transparent, // transparent, not null: a null background does not take clicks
            CornerRadius = new CornerRadius(Theme.RadiusControl),
            Padding = new Thickness(Theme.Space2 + 2, Theme.Space1 + 2, Theme.Space2 + 2, Theme.Space1 + 2),
            Cursor = Cursors.Hand,
        };
        row.MouseEnter += (_, _) => row.Background = Theme.Hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => Pick(entry);
        return row;
    }
}
