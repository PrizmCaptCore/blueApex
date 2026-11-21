using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueApex.Desktop;

/// <summary>Something Spotify can play: a track, an album or a playlist ("spotify:track:...", "spotify:album:...").</summary>
/// <param name="Image">Address of a small cover picture (album art, playlist cover), or null.</param>
internal sealed record SpotifyItem(string Title, string Subtitle, string Uri, string? Image = null)
{
    /// <summary>Albums and playlists play as a whole ("context"); a track plays on its own.</summary>
    public bool IsTrack => Uri.StartsWith("spotify:track:", StringComparison.Ordinal);
}

/// <summary>A problem worth telling the user about, already worded for them (Korean).</summary>
internal sealed class SpotifyException : Exception
{
    /// <summary>The HTTP status Spotify answered with, or 0 when the problem was something else.</summary>
    public int Status { get; }

    public SpotifyException(string message, int status = 0) : base(message) => Status = status;
}

/// <summary>
/// Spotify's Web API: search, the user's playlists and liked songs, and "play this". It makes no sound itself;
/// the sound always comes from BlueApex's own hidden player, turned into a Spotify device
/// (<see cref="BackgroundPlayer.StartSpotifyDeviceAsync"/>). The Spotify app is never started or used: an
/// earlier version fell back to it, and the two players ended up fighting over the same playback.
///
/// Spotify only lets an app's owner (and up to 5 listed accounts) use an unreviewed app, so each user brings
/// their own Client ID, like <see cref="DiscordVoice"/>. Signing in is "authorization code with PKCE": the
/// user's browser opens Spotify's own consent page, Spotify sends the browser back to a tiny web server this
/// class runs on 127.0.0.1 for that moment, and the code it carries is traded for tokens. No client secret
/// and no password ever reach this app. Playback control needs a Premium account (Spotify's rule).
/// </summary>
internal sealed class Spotify
{
    /// <summary>Must be registered, exactly like this, as the app's Redirect URI in Spotify's dashboard.</summary>
    public const string RedirectUri = "http://127.0.0.1:48721/callback";
    private const string ListenPrefix = "http://127.0.0.1:48721/";

    private const string AuthorizeUrl = "https://accounts.spotify.com/authorize";
    private const string TokenUrl = "https://accounts.spotify.com/api/token";
    private const string Api = "https://api.spotify.com/v1";
    // The last three are what Spotify's Web Playback SDK (the built-in player) asks for.
    // The read-playback pair is for "what plays next" (the dock's current-list button).
    private const string Scopes = "user-modify-playback-state playlist-read-private playlist-read-collaborative user-library-read"
                                  + " streaming user-read-email user-read-private"
                                  + " user-read-playback-state user-read-currently-playing";
    private const string NotLinked = "Spotify에 연결돼 있지 않습니다. 트레이 메뉴 \"Spotify 연결 (고급)...\"에서 연결해 주세요.";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Func<string?> _clientId;
    private readonly Func<string?> _refreshToken;
    private readonly Action<string?, bool> _saveRefreshToken;
    private readonly SemaphoreSlim _tokenLock = new(1, 1); // one refresh at a time: each refresh replaces the refresh token
    private CancellationTokenSource? _connecting;
    private string? _access;
    private DateTime _accessUntil; // wall-clock time, so the hours a laptop spent asleep count
    private int _signIn;           // goes up at every "연결 해제": an answer that started before it is thrown away

    /// <param name="clientId">Read each time, so new settings apply without a restart.</param>
    /// <param name="refreshToken">The stored sign-in, or null.</param>
    /// <param name="saveRefreshToken">
    /// Stores (protected) the refresh token Spotify issues; null forgets it. The flag says whether it comes
    /// from a new sign-in ("연결") rather than a renewal: Spotify's 6 months count from the sign-in.
    /// </param>
    public Spotify(Func<string?> clientId, Func<string?> refreshToken, Action<string?, bool> saveRefreshToken)
    {
        _clientId = clientId;
        _refreshToken = refreshToken;
        _saveRefreshToken = saveRefreshToken;
    }

    /// <summary>
    /// The "소리만 듣기" player, which becomes the Spotify device everything plays on.
    /// Null when the desktop layer (and so the dock and that player) is unavailable.
    /// </summary>
    public BackgroundPlayer? Player { get; set; }

    /// <summary>A Client ID is saved (the dock shows its Spotify menu).</summary>
    public bool Configured => _clientId() != null;

    /// <summary>The user has signed in (a refresh token is stored).</summary>
    public bool Linked => Configured && _refreshToken() != null;

    // --- signing in ---

    /// <summary>
    /// Opens Spotify's consent page in the user's browser and waits (up to 5 minutes) for the answer.
    /// Throws <see cref="SpotifyException"/> when it was refused, cancelled or timed out.
    /// </summary>
    public async Task ConnectAsync()
    {
        var clientId = _clientId() ?? throw new SpotifyException("Client ID를 먼저 넣어 주세요.");
        _connecting?.Cancel(); // a second "연결" replaces the first
        var cancel = _connecting = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // PKCE: we keep a random secret (verifier) and send only its hash (challenge) with the browser.
        // Trading the code for tokens needs the verifier, so a code someone else intercepts is useless.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16)); // proves the answer belongs to this request

        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenPrefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new SpotifyException("이 PC의 48721번 포트를 열지 못했습니다(다른 프로그램이 쓰고 있을 수 있습니다): " + ex.Message);
        }

        var url = AuthorizeUrl + "?response_type=code&code_challenge_method=S256"
                  + "&client_id=" + Uri.EscapeDataString(clientId)
                  + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                  + "&code_challenge=" + challenge
                  + "&state=" + state
                  + "&scope=" + Uri.EscapeDataString(Scopes);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            throw new SpotifyException("브라우저를 열지 못했습니다: " + ex.Message);
        }

        string? code = null;
        using (cancel.Token.Register(listener.Stop)) // stopping the listener is what ends the wait below
        {
            try
            {
                while (code == null)
                {
                    var context = await listener.GetContextAsync();
                    var query = context.Request.QueryString;
                    if (context.Request.Url?.AbsolutePath != "/callback" || query["state"] != state)
                    {
                        Reply(context, 404, "BlueApex: 알 수 없는 요청입니다.");
                        continue;
                    }
                    if (query["error"] is { } error)
                    {
                        Reply(context, 200, "Spotify 연결이 취소됐습니다. 이 탭은 닫아도 됩니다.");
                        throw new SpotifyException(error == "access_denied" ? "Spotify 승인 화면에서 취소했습니다." : "Spotify가 승인을 거부했습니다: " + error);
                    }
                    code = query["code"];
                    Reply(context, code != null ? 200 : 400,
                        code != null ? "BlueApex가 Spotify에 연결됐습니다. 이 탭은 닫아도 됩니다." : "BlueApex: 승인 코드가 없습니다.");
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                throw new SpotifyException("승인이 끝나지 않았습니다(취소했거나 5분이 지났습니다). 다시 \"연결\"을 눌러 주세요.");
            }
        }

        await _tokenLock.WaitAsync();
        try
        {
            await TokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            });
        }
        finally
        {
            _tokenLock.Release();
        }
        Log.Write("spotify: connected");
    }

    /// <summary>Gives up a pending <see cref="ConnectAsync"/> (the settings window closed).</summary>
    public void CancelConnect() => _connecting?.Cancel();

    /// <summary>Forgets the sign-in; the Client ID stays.</summary>
    public void Disconnect()
    {
        CancelConnect();
        _signIn++; // a renewal still on its way must not bring the sign-in back when it lands
        _access = null;
        Keep(null, newSignIn: false);
    }

    // Hands the refresh token to be stored. Failing to write the file (disk full, file locked) must not fail
    // the play that caused the renewal: the token is already held in memory and goes to disk with the next save.
    private void Keep(string? refreshToken, bool newSignIn)
    {
        try
        {
            _saveRefreshToken(refreshToken, newSignIn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"spotify: could not save the sign-in now (kept in memory): {ex.Message}");
        }
    }

    private static void Reply(HttpListenerContext context, int status, string message)
    {
        try
        {
            var page = "<!doctype html><html><head><meta charset=\"utf-8\"><title>BlueApex</title></head>" +
                       "<body style=\"font-family:'Segoe UI','Malgun Gothic',sans-serif;background:#1e1e20;color:#fff;padding:48px\">" +
                       $"<h2>{WebUtility.HtmlEncode(message)}</h2></body></html>";
            var bytes = Encoding.UTF8.GetBytes(page);
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // the browser went away; nothing to tell it
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // --- tokens ---

    // Asks Spotify for tokens (first sign-in or refresh) and keeps them. Call with _tokenLock held.
    // The access token lasts an hour and lives only in memory; the refresh token is stored, protected.
    //
    // What can go wrong around sleep / a dropped network, and what happens then:
    //  - no network (lid just opened, Wi-Fi not back yet): the request fails, nothing changed at Spotify,
    //    the stored token is untouched and the next try works.
    //  - Spotify answers with something that is not its JSON (a hotel Wi-Fi login page, a 502): same, untouched.
    //  - the request reached Spotify but the answer was lost on the way back: Spotify may have replaced the
    //    refresh token with one we never received. Nothing on our side can prevent that; the next renewal
    //    then gets "invalid_grant" and the user presses "연결" once (the Client ID stays).
    //  - "invalid_grant" is the one answer that means the sign-in is really over (also: 6 months passed, or
    //    access was removed on spotify.com). Spotify says not to retry it, so the token is dropped.
    private async Task TokenAsync(Dictionary<string, string> form)
    {
        var signIn = _signIn;
        var isRenewal = form["grant_type"] == "refresh_token";
        string text;
        bool ok;
        try
        {
            using var response = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(form));
            text = await response.Content.ReadAsStringAsync();
            ok = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new SpotifyException("Spotify에 연결하지 못했습니다: " + ex.Message);
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            // "연결 해제" (or a new Client ID) happened while we waited: this answer belongs to the old sign-in.
            if (isRenewal && signIn != _signIn) throw new SpotifyException(NotLinked);
            if (!ok)
            {
                var error = Str(root, "error");
                Log.Write($"spotify: token refused: {error} {Str(root, "error_description")}");
                if (error == "invalid_grant" && isRenewal)
                {
                    _access = null;
                    Keep(null, newSignIn: false);
                    throw new SpotifyException("Spotify 연결이 끝났습니다(Spotify 규정상 6개월마다 다시 연결해야 합니다). " +
                                               "트레이 메뉴 \"Spotify 연결 (고급)...\"에서 \"연결\"을 눌러 주세요.");
                }
                throw new SpotifyException(error == "invalid_client"
                    ? "Spotify가 이 Client ID를 모릅니다. 대시보드의 Client ID를 다시 확인해 주세요."
                    : "Spotify 로그인에 실패했습니다: " + (Str(root, "error_description") ?? error ?? "알 수 없는 오류"));
            }
            _access = Str(root, "access_token") ?? throw new SpotifyException("Spotify가 토큰을 주지 않았습니다.");
            var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            _accessUntil = DateTime.UtcNow.AddSeconds(seconds - 60);
            // A renewal may or may not come with a new refresh token; without one the stored token stays in use.
            if (Str(root, "refresh_token") is { } refresh) Keep(refresh, newSignIn: !isRenewal);
        }
        catch (JsonException)
        {
            throw new SpotifyException("Spotify 로그인 응답을 이해하지 못했습니다.");
        }
    }

    private async Task<string> AccessTokenAsync(bool fresh)
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (!fresh && _access != null && DateTime.UtcNow < _accessUntil) return _access;
            var clientId = _clientId() ?? throw new SpotifyException(NotLinked);
            var refresh = _refreshToken() ?? throw new SpotifyException(NotLinked);
            await TokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = clientId,
            });
            return _access!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // --- the Web API ---

    // One call. Returns the JSON answer, or null when Spotify answers with no content (most commands).
    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await AccessTokenAsync(fresh: attempt > 0);
            HttpStatusCode status;
            string text;
            try
            {
                using var request = new HttpRequestMessage(method, Api + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var response = await Http.SendAsync(request);
                status = response.StatusCode;
                text = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new SpotifyException("Spotify에 연결하지 못했습니다: " + ex.Message);
            }
            if (status == HttpStatusCode.Unauthorized && attempt == 0) continue; // the token aged out early: take a new one, once

            JsonDocument? doc = null;
            try
            {
                if (text.TrimStart().StartsWith('{')) doc = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                // some commands answer with a bare word; only the status matters then
            }
            if ((int)status is >= 200 and < 300) return doc;
            using (doc)
            {
                var message = Problem((int)status, doc?.RootElement);
                Log.Write($"spotify: {method} {path.Split('?')[0]} -> {(int)status} {message}");
                throw new SpotifyException(message, (int)status);
            }
        }
    }

    private static string Problem(int status, JsonElement? root)
    {
        string? message = null, reason = null;
        if (root is { } r && r.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            message = Str(error, "message");
            reason = Str(error, "reason");
        }
        var premium = reason == "PREMIUM_REQUIRED" || (message?.Contains("premium", StringComparison.OrdinalIgnoreCase) ?? false);
        return status switch
        {
            403 when premium => "Spotify Premium 계정만 재생을 조작할 수 있습니다.",
            // A feature added after the user signed in needs a permission that sign-in did not ask for.
            403 when message?.Contains("scope", StringComparison.OrdinalIgnoreCase) == true =>
                "이 기능은 새 권한이 필요합니다. 트레이 메뉴 \"Spotify 연결 (고급)...\"에서 \"연결\"을 한 번 다시 눌러 주세요.",
            403 => "Spotify가 거부했습니다. 개발자 대시보드 앱의 User Management에 이 Spotify 계정이 등록돼 있는지 확인해 주세요."
                   + (message != null ? $" ({message})" : ""),
            404 when reason == "NO_ACTIVE_DEVICE" => "재생할 Spotify 기기가 없습니다. Spotify 앱을 켜 주세요.",
            429 => "Spotify에 요청이 너무 많았습니다. 잠시 뒤에 다시 해 주세요.",
            _ => $"Spotify 오류 {status}" + (message != null ? $": {message}" : ""),
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // The objects in root.<name>.items (or root.items when name is null); Spotify sometimes puts null in these lists.
    private static IEnumerable<JsonElement> Items(JsonElement root, string? name)
    {
        var holder = root;
        if (name != null && (!root.TryGetProperty(name, out holder) || holder.ValueKind != JsonValueKind.Object)) yield break;
        if (!holder.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in items.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object) yield return item;
    }

    private static string Artists(JsonElement e) =>
        e.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? string.Join(", ", artists.EnumerateArray().Select(a => Str(a, "name")).Where(n => !string.IsNullOrEmpty(n)))
            : "";

    // The smallest picture that is still at least 100 pixels wide (lists show covers small); else the first one.
    private static string? Image(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        string? best = null, first = null;
        var bestWidth = int.MaxValue;
        foreach (var image in images.EnumerateArray())
        {
            if (Str(image, "url") is not { } url) continue;
            first ??= url;
            var width = image.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : int.MaxValue - 1;
            if (width >= 100 && width < bestWidth) (best, bestWidth) = (url, width);
        }
        return best ?? first;
    }

    private static SpotifyItem? Track(JsonElement e) =>
        Str(e, "uri") is { } uri && Str(e, "name") is { } name
            ? new SpotifyItem(name, Artists(e), uri, e.TryGetProperty("album", out var album) ? Image(album) : null)
            : null;

    private static SpotifyItem? Playlist(JsonElement e)
    {
        if (Str(e, "uri") is not { } uri || Str(e, "name") is not { } name) return null;
        var owner = e.TryGetProperty("owner", out var o) ? Str(o, "display_name") : null;
        return new SpotifyItem(name, owner != null ? "재생목록 · " + owner : "재생목록", uri, Image(e));
    }

    // --- what the dock's menu offers ---

    /// <summary>Tracks, then albums, then playlists matching the words (Spotify gives at most 10 of each).</summary>
    public async Task<List<SpotifyItem>> SearchAsync(string words)
    {
        var result = new List<SpotifyItem>();
        if (string.IsNullOrWhiteSpace(words)) return result;
        using var doc = await SendAsync(HttpMethod.Get, "/search?type=track,album,playlist&limit=10&q=" + Uri.EscapeDataString(words.Trim()));
        if (doc == null) return result;
        var root = doc.RootElement;
        result.AddRange(Items(root, "tracks").Select(Track).OfType<SpotifyItem>());
        foreach (var album in Items(root, "albums"))
            if (Str(album, "uri") is { } uri && Str(album, "name") is { } name)
                result.Add(new SpotifyItem(name, "앨범 · " + Artists(album), uri, Image(album)));
        result.AddRange(Items(root, "playlists").Select(Playlist).OfType<SpotifyItem>());
        return result;
    }

    /// <summary>The playlists in the user's library (their own and the ones they follow), up to 50.</summary>
    public async Task<List<SpotifyItem>> PlaylistsAsync()
    {
        using var doc = await SendAsync(HttpMethod.Get, "/me/playlists?limit=50");
        return doc == null ? new List<SpotifyItem>() : Items(doc.RootElement, null).Select(Playlist).OfType<SpotifyItem>().ToList();
    }

    /// <summary>The most recent 50 liked songs, newest first.</summary>
    public async Task<List<SpotifyItem>> LikedAsync()
    {
        using var doc = await SendAsync(HttpMethod.Get, "/me/tracks?limit=50");
        var result = new List<SpotifyItem>();
        if (doc == null) return result;
        foreach (var saved in Items(doc.RootElement, null))
            if (saved.TryGetProperty("track", out var track) && Track(track) is { } item) result.Add(item);
        return result;
    }

    /// <summary>Plays a track on its own, or an album / playlist from its start.</summary>
    public Task PlayAsync(SpotifyItem item) =>
        item.IsTrack ? StartListAsync(new[] { item.Uri }, 0) : StartContextAsync(item.Uri, null);

    /// <summary>Plays a list of tracks, starting at one of them (liked songs: the rest follow).</summary>
    public Task PlayAsync(IReadOnlyList<SpotifyItem> tracks, int startAt) =>
        StartListAsync(tracks.Select(t => t.Uri).ToArray(), startAt);

    /// <summary>Plays a playlist (or album) starting at one of its tracks; the rest of it follows.</summary>
    public Task PlayAsync(SpotifyItem context, SpotifyItem startAt) => StartContextAsync(context.Uri, startAt.Uri);

    // What was last started from here, remembered so "jump to that track in the current list" can start the
    // same thing again at another track (Spotify has no "go to the 5th track of the queue" command).
    private string? _playingContext; // a playlist or album...
    private string[]? _playingUris;  // ...or a plain list of tracks (liked songs, one searched track)

    private Task StartContextAsync(string context, string? startAtTrack)
    {
        (_playingContext, _playingUris) = (context, null);
        return StartAsync(startAtTrack == null ? (object)new { context_uri = context } : new { context_uri = context, offset = new { uri = startAtTrack } });
    }

    private Task StartListAsync(string[] uris, int startAt)
    {
        (_playingContext, _playingUris) = (null, uris);
        return StartAsync(new { uris, offset = new { position = startAt } });
    }

    /// <summary>The track playing now (null when none) and the ones Spotify will play after it, in order.</summary>
    public async Task<(SpotifyItem? Current, List<SpotifyItem> Next)> QueueAsync()
    {
        using var doc = await SendAsync(HttpMethod.Get, "/me/player/queue");
        var next = new List<SpotifyItem>();
        if (doc == null) return (null, next);
        var root = doc.RootElement;
        var current = root.TryGetProperty("currently_playing", out var playing) && playing.ValueKind == JsonValueKind.Object ? Track(playing) : null;
        if (root.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Array)
            foreach (var entry in queue.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && Track(entry) is { IsTrack: true } item) next.Add(item);
        return (current, next);
    }

    /// <summary>
    /// Goes to a track of the current list: the playlist (or list of tracks) last started from here is started
    /// again at that track, so what follows it stays the same. A track that is not part of it just plays alone.
    /// </summary>
    public Task JumpToAsync(SpotifyItem track)
    {
        if (_playingContext != null) return StartContextAsync(_playingContext, track.Uri);
        var at = _playingUris == null ? -1 : Array.IndexOf(_playingUris, track.Uri);
        return at >= 0 ? StartListAsync(_playingUris!, at) : StartListAsync(new[] { track.Uri }, 0);
    }

    /// <summary>
    /// The first 50 tracks of a playlist. Since Spotify's February 2026 change this only works for playlists
    /// the user made or co-edits; for the others it comes back empty (or refused), and the caller just plays
    /// the playlist as a whole.
    /// </summary>
    public async Task<List<SpotifyItem>> PlaylistTracksAsync(SpotifyItem playlist)
    {
        var id = playlist.Uri[(playlist.Uri.LastIndexOf(':') + 1)..]; // "spotify:playlist:<id>"
        using var doc = await SendAsync(HttpMethod.Get, $"/playlists/{Uri.EscapeDataString(id)}/items?limit=50");
        var result = new List<SpotifyItem>();
        if (doc == null) return result;
        foreach (var entry in Items(doc.RootElement, null))
        {
            // The entry's track used to be under "track"; the 2026 format may call it "item". Podcasts episodes are skipped.
            var track = entry.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object ? t
                : entry.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
            if (track.ValueKind == JsonValueKind.Object && Track(track) is { IsTrack: true } item) result.Add(item);
        }
        return result;
    }

    // There is exactly one place the sound comes from: the built-in player. It is started if needed, and
    // Spotify is told to play on it by its device id (which also pulls playback away from a phone or the
    // Spotify app if one was playing). Nothing else is ever started or picked, so two players cannot fight.
    private async Task StartAsync(object what)
    {
        var player = Player ?? throw new SpotifyException("바탕화면 층을 쓸 수 없어 내장 플레이어가 꺼져 있습니다.");
        // "Device not found" (404) has two causes, told apart by whether the device is new:
        //  - a device made a moment ago is not known to Spotify's servers yet: wait a little and try again;
        //  - a device we remembered from earlier is gone (Spotify forgets it when the network was away for a
        //    while): waiting cannot help, so make a new device and send the play there.
        var remembered = player.HasSpotifyDevice;
        for (var round = 0; ; round++)
        {
            var isNew = round > 0 || !remembered;
            var device = await player.StartSpotifyDeviceAsync(() => AccessTokenAsync(fresh: false), fresh: round > 0);
            var path = "/me/player/play?device_id=" + Uri.EscapeDataString(device);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var _ = await SendAsync(HttpMethod.Put, path, what);
                    return;
                }
                catch (SpotifyException ex) when (ex.Status == 404 && isNew && attempt < 3)
                {
                    await Task.Delay(700);
                }
                catch (SpotifyException ex) when (ex.Status == 404 && !isNew)
                {
                    Log.Write("spotify: the remembered device is gone, starting a new one");
                    break; // out of the retries, into the next round with a fresh device
                }
            }
        }
    }
}
