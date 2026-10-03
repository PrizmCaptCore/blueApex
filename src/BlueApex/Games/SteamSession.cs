using System.Text.Json;

namespace BlueApex.Games;

/// <summary>
/// Steam sign-in through <see cref="BrowserSession"/>. After the user logs in on
/// Steam's own page, the session cookie names the SteamID and the store hands out a
/// short-lived Web API access token, which lets the official API list the account's
/// games without a developer key. Tokens last about a day; the saved session
/// yields a new one silently until Steam asks to sign in again.
/// </summary>
internal static class SteamSession
{
    private const string LoginUrl = "https://steamcommunity.com/login/home/?goto=";
    private const string CommunityUrl = "https://steamcommunity.com";
    private const string TokenUrl = "https://store.steampowered.com/pointssummary/ajaxgetasyncconfig";

    public sealed record Credentials(long SteamId, string Token);

    /// <summary>
    /// With <paramref name="interactive"/>, shows the login window and waits (up to ten minutes)
    /// for the user to sign in; otherwise reuses the saved session off-screen.
    /// Null when there is no signed-in session (or the user closed the window).
    /// </summary>
    public static async Task<Credentials?> GetAsync(bool interactive)
    {
        using var browser = await BrowserSession.OpenAsync("Steam 로그인", interactive);
        if (interactive)
        {
            await browser.NavigateAsync(LoginUrl);
            var signedIn = await browser.WaitUntilAsync(async () => await SteamIdAsync(browser) != null, TimeSpan.FromMinutes(10));
            if (!signedIn) return null;
        }
        var steamId = await SteamIdAsync(browser);
        if (steamId == null) return null;

        var text = await browser.FetchTextAsync(TokenUrl);
        var token = TokenFrom(text);
        if (token == null)
        {
            // The community login normally signs the store in too; if not, one visit does it.
            await browser.NavigateAsync("https://store.steampowered.com/");
            token = TokenFrom(await browser.FetchTextAsync(TokenUrl));
        }
        if (token == null) Log.Write("steam: signed in but no web API token from the store");
        return token == null ? null : new Credentials(steamId.Value, token);
    }

    // steamLoginSecure = "<steamid64>||<jwt>" (URL-encoded).
    private static async Task<long?> SteamIdAsync(BrowserSession browser)
    {
        var cookie = await browser.CookieAsync(CommunityUrl, "steamLoginSecure");
        if (cookie == null) return null;
        var id = Uri.UnescapeDataString(cookie).Split("||")[0];
        return long.TryParse(id, out var steamId) ? steamId : null;
    }

    // Signed in: {"success":1,"data":{"webapi_token":"..."}}; signed out: {"success":1,"data":[]}.
    private static string? TokenFrom(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty("webapi_token", out var token) ? token.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
