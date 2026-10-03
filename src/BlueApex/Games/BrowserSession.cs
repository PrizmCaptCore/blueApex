using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using BlueApex.Ui;
using Microsoft.Web.WebView2.Core;

namespace BlueApex.Games;

/// <summary>
/// An embedded Edge (WebView2) browser with its own persistent profile under
/// %AppData%\BlueApex\browser, the way Playnite links accounts: the user signs in
/// to a service's real login page inside a window, and afterwards the session's
/// cookies let the app read pages or tokens on their behalf, with no password ever
/// seen by this app. The same profile is reused invisibly later for refreshes.
///
/// Usage: <c>using var b = await BrowserSession.OpenAsync("Steam 로그인", visible: true);</c>
/// then <see cref="NavigateAsync"/>, <see cref="WaitUntilAsync"/>, <see cref="CookieAsync"/>, <see cref="FetchTextAsync"/>.
/// Everything must run on the UI thread (WebView2 is a UI component).
/// </summary>
internal sealed class BrowserSession : IDisposable
{
    private static readonly string UserDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "browser");

    private readonly Window _window;
    private readonly CoreWebView2Controller _controller;
    private bool _closedByUser;

    public CoreWebView2 Web => _controller.CoreWebView2;

    private BrowserSession(Window window, CoreWebView2Controller controller)
    {
        _window = window;
        _controller = controller;
        window.Closed += (_, _) => _closedByUser = true;
    }

    /// <summary>
    /// Opens a browser; <paramref name="visible"/> shows it in a window for the user to sign in,
    /// otherwise it lives off-screen for silent work. Throws <see cref="WebView2RuntimeNotFoundException"/>
    /// when the Edge WebView2 runtime is missing (it ships with Windows 11; older systems install it from Microsoft).
    /// </summary>
    public static async Task<BrowserSession> OpenAsync(string title, bool visible)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, UserDataDir);
        var window = new Window
        {
            Title = title,
            Width = visible ? 560 : 1,
            Height = visible ? 760 : 1,
            WindowStartupLocation = visible ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual,
            ShowInTaskbar = visible,
            ShowActivated = visible,
            Topmost = visible, // the drawer is topmost; the login must not hide behind it
        };
        if (!visible)
        {
            window.WindowStyle = WindowStyle.None;
            window.Left = -32000;
            window.Top = -32000;
            window.Opacity = 0;
        }
        Theme.ApplyWindow(window);
        window.Show(); // the controller needs a real HWND
        var hwnd = new WindowInteropHelper(window).Handle;
        var controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
        controller.IsVisible = visible;
        var session = new BrowserSession(window, controller);
        session.FitToWindow();
        window.SizeChanged += (_, _) => session.FitToWindow();
        return session;
    }

    private void FitToWindow()
    {
        var scale = PresentationSource.FromVisual(_window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0,
            (int)(_window.ActualWidth * scale), (int)(_window.ActualHeight * scale));
    }

    /// <summary>Loads a page and returns when it has finished (or false if the user closed the window).</summary>
    public async Task<bool> NavigateAsync(string url)
    {
        if (_closedByUser) return false;
        var done = new TaskCompletionSource<bool>();
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e.IsSuccess);
        void Closed(object? s, EventArgs e) => done.TrySetResult(false);
        Web.NavigationCompleted += Completed;
        _window.Closed += Closed;
        try
        {
            Web.Navigate(url);
            return await done.Task;
        }
        finally
        {
            Web.NavigationCompleted -= Completed;
            _window.Closed -= Closed;
        }
    }

    /// <summary>
    /// Polls <paramref name="condition"/> after every page load and once a second until it is true.
    /// False when the user closes the window or <paramref name="timeout"/> passes.
    /// </summary>
    public async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!_closedByUser && DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(1000);
        }
        return false;
    }

    /// <summary>The value of a cookie the profile holds for the site, or null.</summary>
    public async Task<string?> CookieAsync(string siteUrl, string name)
    {
        var cookies = await Web.CookieManager.GetCookiesAsync(siteUrl);
        return cookies.FirstOrDefault(c => c.Name == name)?.Value;
    }

    /// <summary>Loads a URL with the session's cookies and returns the page's text (for JSON endpoints).</summary>
    public async Task<string?> FetchTextAsync(string url)
    {
        if (!await NavigateAsync(url)) return null;
        var json = await Web.ExecuteScriptAsync("document.body.innerText");
        return JsonSerializer.Deserialize<string>(json);
    }

    /// <summary>Signs out of everything: wipes the profile's cookies and storage.</summary>
    public static async Task ClearAsync()
    {
        using var session = await OpenAsync("", visible: false);
        await session.Web.Profile.ClearBrowsingDataAsync();
    }

    public void Dispose()
    {
        _controller.Close();
        if (!_closedByUser) _window.Close();
    }
}
