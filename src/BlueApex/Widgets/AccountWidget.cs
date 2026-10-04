using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueApex.Games;
using BlueApex.Sdk;
using BlueApex.Ui;

namespace BlueApex.Widgets;

/// <summary>One launcher account as a card on the home screen: avatar (with the launcher's frame and background art where it has them), name, status, a detail line.</summary>
internal sealed class AccountWidgetProvider : IWidgetProvider
{
    private readonly IAccountSource _source;

    public AccountWidgetProvider(IAccountSource source) => _source = source;

    public string Id => "account-" + _source.Id;
    public string DisplayName => $"계정 카드: {_source.Label}";
    public (int Width, int Height) DefaultSizeDip => (AccountWidget.WidthDip, AccountWidget.HeightDip);
    public IWidget Create(IWidgetContext context) => new AccountWidget(_source, context);
}

internal sealed class AccountWidget : IInternalWidget
{
    public const int WidthDip = 320, HeightDip = 104;
    private const double AvatarSize = 60, FrameScale = 1.22;

    private readonly IAccountSource _source;
    private readonly IWidgetContext _context;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly Image _avatar = new() { Width = AvatarSize, Height = AvatarSize, Stretch = Stretch.UniformToFill };
    private readonly Image _frame = new() { Width = AvatarSize * FrameScale, Height = AvatarSize * FrameScale, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly ImageBrush _background = new() { Stretch = Stretch.UniformToFill, Opacity = 0.35 };
    private readonly Border _card;
    private readonly TextBlock _name = new() { Foreground = Theme.Text, FontSize = Theme.FontTitle, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _badge;
    private readonly TextBlock _badgeText = new() { Foreground = Theme.Text, FontSize = Theme.FontSmall, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { Foreground = Theme.Text, FontSize = Theme.FontBody, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { Foreground = Theme.TextDim, FontSize = Theme.FontSmall, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _launcher;
    private AccountProfile? _profile;
    private bool _busy;

    public AccountWidget(IAccountSource source, IWidgetContext context)
    {
        _source = source;
        _context = context;
        _launcher = new TextBlock { Text = source.Label, Foreground = Theme.TextFaint, FontSize = Theme.FontSmall, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _badge = new Border
        {
            Background = Theme.Accent, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(Theme.Space2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Child = _badgeText,
        };
        var avatarClip = new Border
        {
            Width = AvatarSize, Height = AvatarSize, CornerRadius = new CornerRadius(AvatarSize / 2), ClipToBounds = true,
            Background = Theme.GroupBar, Child = _avatar,
        };
        // The frame is larger than the avatar and drawn over it, as Steam's own profile does.
        var avatarStack = new Grid { Width = AvatarSize * FrameScale, Height = AvatarSize * FrameScale, Children = { avatarClip, _frame }, VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _name, _badge } };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.Space3, 0, 0, 0), Children = { nameRow, _status, _detail } };
        var row = new DockPanel { Children = { avatarStack, text } };
        var content = new Grid { Children = { row, _launcher } };
        _card = Parts.Panel(content, padding: new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2));
        View = _card;
        DesiredSizeDip = (WidthDip, HeightDip);

        Show(new AccountProfile(source.Label, null, null, null, "불러오는 중...", null, null, null, false));
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public FrameworkElement View { get; }
    public (double Width, double Height) DesiredSizeDip { get; }
    public event Action? Resized { add { } remove { } }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var profile = await _source.FetchAsync();
            if (profile != null) Show(profile);
            else Show(new AccountProfile(_source.Label, null, null, null, "가져오지 못했습니다", null, null, null, false));
            if (profile != null) await LoadImagesAsync(profile);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Show(AccountProfile profile)
    {
        _profile = profile;
        _name.Text = profile.Name;
        _status.Text = profile.Status ?? "";
        _detail.Text = profile.Detail ?? "";
        _detail.Visibility = profile.Detail == null ? Visibility.Collapsed : Visibility.Visible;
        _badgeText.Text = profile.Badge ?? "";
        _badge.Visibility = profile.Badge == null ? Visibility.Collapsed : Visibility.Visible;
        _name.Opacity = profile.SignedIn ? 1 : 0.7;
        if (profile.AvatarUrl == null) _avatar.Source = null;
        if (profile.FrameUrl == null) _frame.Source = null;
        if (profile.BackgroundUrl == null) _card.Background = Theme.Panel;
    }

    // Pictures are fetched (cached on disk) and decoded off the UI thread, then swapped in.
    private async Task LoadImagesAsync(AccountProfile profile)
    {
        var (avatar, frame, background) = await Task.Run(() =>
            (Decode(profile.AvatarUrl, 128), Decode(profile.FrameUrl, 160), Decode(profile.BackgroundUrl, 400)));
        if (!ReferenceEquals(_profile, profile)) return; // a newer profile arrived meanwhile
        if (avatar != null) _avatar.Source = avatar;
        if (frame != null) _frame.Source = frame;
        if (background != null)
        {
            _background.ImageSource = background;
            _card.Background = _background;
        }
    }

    private static BitmapSource? Decode(string? source, int heightPx)
    {
        if (source == null) return null;
        var file = File.Exists(source) ? source : ImageCache.Fetch(source);
        if (file == null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.DecodePixelHeight = heightPx;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    public void OnClick() { }

    public void OnClickAt(double x, double y)
    {
        if (_profile?.ProfileUrl is { } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public bool ScrollsAt(double x, double y) => false;
    public void ScrollBy(double deltaX, double deltaY) { }
    public void Wheel(int notches) { }

    public IEnumerable<WidgetMenuItem> MenuItems
    {
        get
        {
            yield return new WidgetMenuItem($"{_source.Label} 로그인...", () => _ = LoginAsync());
            yield return new WidgetMenuItem("새로 고침", () => _ = RefreshAsync());
            yield return new WidgetMenuItem($"{_source.Label} 로그아웃", () => _ = LogoutAsync());
        }
    }

    private async Task LoginAsync()
    {
        try
        {
            if (await _source.LoginAsync()) await RefreshAsync();
        }
        catch (Exception ex) when (ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            _context.Log($"login failed: {ex.Message}");
            MessageBox.Show("로그인 창을 열지 못했습니다.\n" + ex.Message, "BlueApex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LogoutAsync()
    {
        await _source.LogoutAsync();
        await RefreshAsync();
    }

    public void Dispose() => _timer.Stop();
}
