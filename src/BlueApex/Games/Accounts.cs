using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace BlueApex.Games;

/// <summary>What an account card shows. Image fields are URLs or local files; null = not available.</summary>
/// <param name="Status">One line: "온라인", "Palworld 플레이 중", "로그인 필요"...</param>
/// <param name="Detail">A second line: playtime, owned games...</param>
/// <param name="Badge">A small tag next to the name (Steam level).</param>
/// <param name="FrameUrl">A transparent overlay drawn over the avatar (Steam avatar frames).</param>
/// <param name="BackgroundUrl">Art behind the card (Steam profile background).</param>
internal sealed record AccountProfile(
    string Name, string? AvatarUrl, string? FrameUrl, string? BackgroundUrl,
    string? Status, string? Detail, string? Badge, string? ProfileUrl, bool SignedIn);

/// <summary>
/// One launcher account as the account-card widget sees it. Signing in happens on the
/// launcher's own web login inside <see cref="BrowserSession"/>; afterwards the saved
/// session is reused silently, so the app never sees a password.
/// </summary>
internal interface IAccountSource
{
    /// <summary>"steam", "gog", "epic".</summary>
    string Id { get; }
    string Label { get; }

    /// <summary>The current profile. With no session it still returns what can be known locally (SignedIn = false).</summary>
    Task<AccountProfile?> FetchAsync();

    /// <summary>Shows the login window; true when a session exists afterwards.</summary>
    Task<bool> LoginAsync();

    /// <summary>Forgets the session for this launcher only.</summary>
    Task LogoutAsync();
}

internal static class Accounts
{
    public static readonly IReadOnlyList<IAccountSource> All = new IAccountSource[] { new SteamAccount(), new GogAccount(), new EpicAccount() };

    public static IAccountSource? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    internal static async Task<JsonDocument?> GetJsonAsync(string url)
    {
        try
        {
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"account request refused: {url.Split('?')[0]} -> {(int)response.StatusCode}");
                return null;
            }
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write($"account request failed: {url.Split('?')[0]} ({ex.Message})");
            return null;
        }
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

/// <summary>
/// Steam: name, avatar, online state, the game being played, level, two-week playtime and
/// the equipped profile decorations (background, avatar frame), all from Valve's Web API
/// with the session's access token. Without a session: the client's cached name and avatar.
/// </summary>
internal sealed class SteamAccount : IAccountSource
{
    public string Id => "steam";
    public string Label => "Steam";

    private const string Api = "https://api.steampowered.com";
    private const string CommunityImages = "https://shared.steamstatic.com/community_assets/images/";

    // A fresh token takes a hidden browser (~1-2 s); it is good for about a day, so keep it an hour.
    private static SteamSession.Credentials? _session;
    private static DateTime _sessionAt;

    private static async Task<SteamSession.Credentials?> SessionAsync(bool fresh = false)
    {
        if (!fresh && _session != null && DateTime.UtcNow - _sessionAt < TimeSpan.FromMinutes(50)) return _session;
        try
        {
            _session = await SteamSession.GetAsync(interactive: false);
            _sessionAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"steam session unavailable: {ex.Message}");
            _session = null;
        }
        return _session;
    }

    public async Task<AccountProfile?> FetchAsync()
    {
        var session = await SessionAsync();
        if (session == null) return Local();

        // The OAuth flavour of the summaries call is the one that takes a session token; the keyed one is a fallback.
        var summary = await SummariesAsync(session);
        if (summary == null)
        {
            // Tokens last about a day; a refused one means ours aged out, so take a new one from the store and retry once.
            session = await SessionAsync(fresh: true);
            if (session == null) return Local();
            summary = await SummariesAsync(session);
        }
        var token = Uri.EscapeDataString(session.Token);
        var id = session.SteamId;
        var root = summary?.RootElement;
        if (root is { } r0 && r0.TryGetProperty("response", out var wrapped)) root = wrapped;
        var player = root is { } r1 && r1.TryGetProperty("players", out var players) ? players.EnumerateArray().FirstOrDefault() : default;
        if (player is not { ValueKind: JsonValueKind.Object } p) return Local() with { Status = "Steam이 응답하지 않습니다" };

        var name = Accounts.Str(p, "personaname") ?? "Steam";
        var avatar = Accounts.Str(p, "avatarfull");
        var game = Accounts.Str(p, "gameextrainfo");
        var state = Accounts.Int(p, "personastate") ?? 0;
        var status = game != null ? $"{game} 플레이 중" : state switch
        {
            0 => "오프라인", 1 => "온라인", 2 => "다른 용무 중", 3 => "자리 비움", 4 => "수면 중", 5 => "거래 희망", 6 => "게임 희망", _ => "온라인",
        };

        string? badge = null;
        using (var level = await Accounts.GetJsonAsync($"{Api}/IPlayerService/GetSteamLevel/v1/?access_token={token}&steamid={id}"))
            if (level != null && Accounts.Int(level.RootElement.GetProperty("response"), "player_level") is { } lv) badge = $"Lv {lv}";

        string? detail = null;
        using (var recent = await Accounts.GetJsonAsync($"{Api}/IPlayerService/GetRecentlyPlayedGames/v1/?access_token={token}&steamid={id}&count=0"))
        {
            if (recent != null && recent.RootElement.GetProperty("response").TryGetProperty("games", out var games))
            {
                var minutes = games.EnumerateArray().Sum(g => Accounts.Int(g, "playtime_2weeks") ?? 0);
                var count = games.GetArrayLength();
                detail = minutes > 0 ? $"최근 2주 {minutes / 60.0:0.#}시간 · 게임 {count}개" : "최근 2주 플레이 없음";
            }
        }

        var (background, frame) = await EquippedAsync(token, id) ?? EquippedFromCache(id);
        return new AccountProfile(name, avatar, frame, background, status, detail, badge, $"https://steamcommunity.com/profiles/{id}", true);
    }

    private static async Task<JsonDocument?> SummariesAsync(SteamSession.Credentials session)
    {
        var token = Uri.EscapeDataString(session.Token);
        return await Accounts.GetJsonAsync($"{Api}/ISteamUserOAuth/GetUserSummaries/v1/?access_token={token}&steamids={session.SteamId}")
               ?? await Accounts.GetJsonAsync($"{Api}/ISteamUser/GetPlayerSummaries/v2/?access_token={token}&steamids={session.SteamId}");
    }

    // Profile background and avatar frame, as the mini profile shows them.
    private static async Task<(string? Background, string? Frame)?> EquippedAsync(string token, long id)
    {
        using var doc = await Accounts.GetJsonAsync($"{Api}/IPlayerService/GetProfileItemsEquipped/v1/?access_token={token}&steamid={id}&language=koreana");
        if (doc == null) return null;
        var r = doc.RootElement.GetProperty("response");
        return (ImageOf(r, "profile_background"), ImageOf(r, "avatar_frame"));
    }

    private static string? ImageOf(JsonElement response, string item)
    {
        if (!response.TryGetProperty(item, out var e) || e.ValueKind != JsonValueKind.Object) return null;
        var path = Accounts.Str(e, "image_large");
        return string.IsNullOrEmpty(path) ? null : CommunityImages + path;
    }

    // The client caches the same answer in localconfig.vdf ("GetEquippedProfileItemsForUser<steamid>").
    private static (string? Background, string? Frame) EquippedFromCache(long id)
    {
        try
        {
            var json = LocalConfigValue($"GetEquippedProfileItemsForUser{id}");
            if (json == null) return (null, null);
            using var doc = JsonDocument.Parse(json);
            return (ImageOf(doc.RootElement, "profile_background"), ImageOf(doc.RootElement, "avatar_frame"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    // No session: the client's cached persona name and avatar still make a card.
    private static AccountProfile Local()
    {
        var steam = SteamLibrary.InstallPath;
        var id = SteamLibrary.SteamId64;
        string? avatar = null;
        if (steam != null && id != null)
        {
            var cached = Path.Combine(steam, "config", "avatarcache", $"{id}.png");
            if (File.Exists(cached)) avatar = cached;
        }
        var (background, frame) = id != null ? EquippedFromCache(id.Value) : (null, null);
        var name = LocalConfigValue("PersonaName") ?? "Steam";
        return new AccountProfile(name, avatar, frame, background, "로그인하면 상태·레벨·플레이 시간이 보입니다", null, null,
            id != null ? $"https://steamcommunity.com/profiles/{id}" : null, false);
    }

    private static string? LocalConfigValue(string key)
    {
        var steam = SteamLibrary.InstallPath;
        if (steam == null || SteamLibrary.AccountId is not { } account) return null;
        var file = Path.Combine(steam, "userdata", account.ToString(), "config", "localconfig.vdf");
        if (!File.Exists(file)) return null;
        try
        {
            return Find(Vdf.Parse(File.ReadAllText(file)), key);
        }
        catch (IOException)
        {
            return null;
        }
        static string? Find(Vdf node, string k) => node[k] ?? node.Blocks.Select(b => Find(b, k)).FirstOrDefault(v => v != null);
    }

    public async Task<bool> LoginAsync()
    {
        _session = await SteamSession.GetAsync(interactive: true);
        _sessionAt = DateTime.UtcNow;
        return _session != null;
    }

    public async Task LogoutAsync()
    {
        _session = null;
        await BrowserSession.ClearSiteAsync("https://steamcommunity.com");
        await BrowserSession.ClearSiteAsync("https://store.steampowered.com");
        await BrowserSession.ClearSiteAsync("https://help.steampowered.com");
    }
}

/// <summary>
/// GOG: a GOG.com login in the embedded browser; afterwards userData.json (the page the
/// store itself uses) gives the user name, avatar and how many games the account owns.
/// </summary>
internal sealed class GogAccount : IAccountSource
{
    public string Id => "gog";
    public string Label => "GOG";

    private const string UserData = "https://embed.gog.com/userData.json";
    private const string LoginUrl = "https://auth.gog.com/auth?client_id=46899977096215655&redirect_uri=https%3A%2F%2Fembed.gog.com%2Fon_login_success%3Forigin%3Dclient&response_type=code&layout=galaxy";

    public async Task<AccountProfile?> FetchAsync()
    {
        string? json;
        try
        {
            using var browser = await BrowserSession.OpenAsync("GOG", visible: false);
            json = await browser.FetchTextAsync(UserData);
        }
        catch (Exception ex) when (ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"gog session unavailable: {ex.Message}");
            return null;
        }
        return Parse(json);
    }

    private static AccountProfile? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var signedIn = r.TryGetProperty("isLoggedIn", out var li) && li.ValueKind == JsonValueKind.True;
            if (!signedIn) return new AccountProfile("GOG", null, null, null, "로그인 필요", null, null, "https://www.gog.com/account", false);
            var name = Accounts.Str(r, "username") ?? "GOG";
            var avatar = Accounts.Str(r, "avatar") is { Length: > 0 } a ? (a.StartsWith("//") ? "https:" + a : a) + "_big.jpg" : null;
            string? detail = null;
            if (r.TryGetProperty("purchasedItems", out var items) && Accounts.Int(items, "games") is { } games)
                detail = $"보유 게임 {games}개";
            return new AccountProfile(name, avatar, null, null, "로그인됨", detail, null, "https://www.gog.com/account", true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> LoginAsync()
    {
        using var browser = await BrowserSession.OpenAsync("GOG 로그인", visible: true);
        await browser.NavigateAsync(LoginUrl);
        return await browser.WaitUntilAsync(async () =>
        {
            var cookie = await browser.CookieAsync("https://embed.gog.com", "gog-al");
            return cookie != null;
        }, TimeSpan.FromMinutes(10));
    }

    public async Task LogoutAsync()
    {
        await BrowserSession.ClearSiteAsync("https://embed.gog.com");
        await BrowserSession.ClearSiteAsync("https://www.gog.com");
        await BrowserSession.ClearSiteAsync("https://auth.gog.com");
    }
}

/// <summary>
/// Epic Games: an epicgames.com login; the account page's own endpoint then gives the
/// display name. Epic has no public profile pictures, so the card shows the name and the
/// library size (from the launcher's catalog cache).
/// </summary>
internal sealed class EpicAccount : IAccountSource
{
    public string Id => "epic";
    public string Label => "Epic Games";

    private const string AccountApi = "https://www.epicgames.com/id/api/account";
    private const string LoginUrl = "https://www.epicgames.com/id/login?lang=ko";

    public async Task<AccountProfile?> FetchAsync()
    {
        string? json;
        try
        {
            using var browser = await BrowserSession.OpenAsync("Epic Games", visible: false);
            json = await browser.FetchTextAsync(AccountApi);
        }
        catch (Exception ex) when (ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"epic session unavailable: {ex.Message}");
            return null;
        }
        var owned = EpicLibrary.Scan().Count();
        var detail = owned > 0 ? $"라이브러리 {owned}개" : null;
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                var name = Accounts.Str(doc.RootElement, "displayName");
                if (name != null)
                    return new AccountProfile(name, null, null, null, "로그인됨", detail, null, "https://www.epicgames.com/account/personal", true);
            }
        }
        catch (JsonException)
        {
            // the page returned an error document; treat as signed out
        }
        return new AccountProfile("Epic Games", null, null, null, "로그인 필요", detail, null, "https://www.epicgames.com/account/personal", false);
    }

    public async Task<bool> LoginAsync()
    {
        using var browser = await BrowserSession.OpenAsync("Epic Games 로그인", visible: true);
        await browser.NavigateAsync(LoginUrl);
        return await browser.WaitUntilAsync(async () =>
        {
            var cookie = await browser.CookieAsync("https://www.epicgames.com", "EPIC_SSO");
            return cookie != null;
        }, TimeSpan.FromMinutes(10));
    }

    public Task LogoutAsync() => BrowserSession.ClearSiteAsync("https://www.epicgames.com");
}
