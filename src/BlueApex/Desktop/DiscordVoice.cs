using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace BlueApex.Desktop;

/// <summary>Someone in the voice channel the user is in.</summary>
internal sealed record DiscordMember(string Id, string Name, string AvatarUrl, bool Speaking, bool Muted, bool Deafened);

/// <summary>The user's voice channel right now: who is in it, and the user's own mic/headset state.</summary>
internal sealed record DiscordVoiceState(string ChannelName, IReadOnlyList<DiscordMember> Members, bool SelfMute, bool SelfDeaf);

/// <summary>The user's own Discord application, entered once in the settings (see docs/usage.md).</summary>
internal sealed record DiscordCredentials(string ClientId, string ClientSecret, string? RefreshToken);

/// <summary>
/// Discord voice through Discord's official local RPC: a named pipe ("discord-ipc-0".."9") to the Discord
/// app running on this PC. Voice scopes are only granted to an application's owner and testers, so each
/// user brings their own (free) Discord application; the first connection shows Discord's own "authorize"
/// prompt, after that a refresh token reconnects silently.
///
/// <see cref="Changed"/> fires on the UI thread; <see cref="Current"/> is null when Discord is not running,
/// not linked, or the user is not in a voice channel. Reconnects every few seconds while Discord is closed.
/// </summary>
internal sealed class DiscordVoice : IDisposable
{
    private static readonly string[] Scopes = { "rpc", "rpc.voice.read", "rpc.voice.write" };
    private const string RedirectUri = "http://localhost";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Func<DiscordCredentials?> _credentials;
    private readonly Action<string?> _saveRefreshToken;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly object _gate = new(); // guards the channel state below: the pipe reader and commands run on different threads
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;

    // State as Discord reports it, kept on the reader thread and published as snapshots.
    private string? _channelId, _channelName;
    private readonly Dictionary<string, DiscordMember> _members = new();
    private bool _selfMute, _selfDeaf;

    public DiscordVoiceState? Current { get; private set; }
    public bool Connected { get; private set; }

    /// <summary>The last problem, for the settings dialog ("Discord가 실행 중이 아닙니다"...); null when fine.</summary>
    public string? Problem { get; private set; }

    public event Action? Changed;

    /// <param name="credentials">Read each time a connection starts, so new settings apply without a restart.</param>
    /// <param name="saveRefreshToken">Stores (protected) the refresh token Discord issues; null forgets it.</param>
    public DiscordVoice(Func<DiscordCredentials?> credentials, Action<string?> saveRefreshToken)
    {
        _credentials = credentials;
        _saveRefreshToken = saveRefreshToken;
        _ = RunAsync(_stop.Token);
    }

    // --- commands from the dock ---

    public Task SetMuteAsync(bool mute) => SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = mute });
    public Task SetDeafAsync(bool deaf) => SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = deaf });

    /// <summary>Drops the current connection so the next attempt uses fresh settings.</summary>
    public void Reconnect() => _pipe?.Dispose();

    // --- connection loop ---

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var credentials = _credentials();
            if (credentials == null)
            {
                SetProblem("연결 안 됨 (설정에서 디스코드 앱 정보를 넣어 주세요)");
                await Delay(5, stop);
                continue;
            }
            try
            {
                await SessionAsync(credentials, stop);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or InvalidOperationException
                                       or HttpRequestException or TaskCanceledException or ObjectDisposedException or UnauthorizedAccessException)
            {
                if (stop.IsCancellationRequested) return;
                Log.Write($"discord: {ex.Message}");
                if (Problem == null) SetProblem("연결이 끊겼습니다: " + ex.Message);
            }
            finally
            {
                Connected = false;
                _pipe?.Dispose();
                _pipe = null;
                FailPending();
                ClearChannel();
            }
            await Delay(5, stop);
        }
    }

    private async Task SessionAsync(DiscordCredentials credentials, CancellationToken stop)
    {
        _pipe = await OpenPipeAsync(stop);
        if (_pipe == null)
        {
            SetProblem("Discord가 실행 중이 아닙니다");
            return;
        }
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = credentials.ClientId });
        var reader = ReadLoopAsync(_pipe, stop);
        // Discord ignores commands sent before it has answered the handshake with READY.
        var first = await Task.WhenAny(_ready.Task, reader, Task.Delay(TimeSpan.FromSeconds(10), stop));
        if (first == reader) await reader; // surfaces why Discord closed the pipe (e.g. an unknown Client ID)
        if (!_ready.Task.IsCompleted) throw new TimeoutException("Discord가 연결에 응답하지 않았습니다(READY 없음)");

        var token = await AccessTokenAsync(credentials);
        await CommandAsync("AUTHENTICATE", new JsonObject { ["access_token"] = token });
        Connected = true;
        SetProblem(null);
        Log.Write("discord: connected");

        await SubscribeAsync("VOICE_CHANNEL_SELECT", null);
        await SubscribeAsync("VOICE_SETTINGS_UPDATE", null);
        var settings = await CommandAsync("GET_VOICE_SETTINGS", new JsonObject());
        lock (_gate) ApplySettings(settings["data"] as JsonObject);
        await SelectChannelAsync();
        await reader; // until the pipe closes
    }

    private static async Task<NamedPipeClientStream?> OpenPipeAsync(CancellationToken stop)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300, stop);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                pipe.Dispose();
            }
        }
        return null;
    }

    // A fresh access token: from the saved refresh token if it still works, else via Discord's authorize prompt.
    private async Task<string> AccessTokenAsync(DiscordCredentials credentials)
    {
        if (credentials.RefreshToken != null)
        {
            var refreshed = await TokenAsync(credentials, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = credentials.RefreshToken });
            if (refreshed != null) return refreshed;
            Log.Write("discord: refresh token rejected; asking to authorize again");
        }
        SetProblem("Discord 앱에서 \"승인\"을 눌러 주세요");
        var authorize = await CommandAsync("AUTHORIZE", new JsonObject
        {
            ["client_id"] = credentials.ClientId,
            ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)s!).ToArray()),
        }, TimeSpan.FromMinutes(2));
        var code = authorize["data"]?["code"]?.GetValue<string>() ?? throw new InvalidOperationException("Discord가 승인 코드를 주지 않았습니다");
        return await TokenAsync(credentials, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri })
               ?? throw new InvalidOperationException("토큰 교환 실패: Client Secret과 리디렉트 주소(http://localhost)를 확인해 주세요");
    }

    private async Task<string?> TokenAsync(DiscordCredentials credentials, Dictionary<string, string> form)
    {
        form["client_id"] = credentials.ClientId;
        form["client_secret"] = credentials.ClientSecret;
        using var response = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form));
        if (!response.IsSuccessStatusCode)
        {
            Log.Write($"discord: token request -> {(int)response.StatusCode}");
            return null;
        }
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var refresh = body["refresh_token"]?.GetValue<string>();
        if (refresh != null) await _dispatcher.InvokeAsync(() => _saveRefreshToken(refresh));
        return body["access_token"]?.GetValue<string>();
    }

    // --- voice channel tracking ---

    private async Task SelectChannelAsync()
    {
        var previous = _channelId;
        var reply = await CommandAsync("GET_SELECTED_VOICE_CHANNEL", new JsonObject());
        var channel = reply["data"] as JsonObject;
        if (previous != null)
        {
            foreach (var evt in ChannelEvents)
                await SendRawAsync("UNSUBSCRIBE", evt, new JsonObject { ["channel_id"] = previous });
        }
        lock (_gate)
        {
            _members.Clear();
            _channelId = channel?["id"]?.GetValue<string>();
            _channelName = channel?["name"]?.GetValue<string>();
            if (channel?["voice_states"] is JsonArray states)
                foreach (var state in states.OfType<JsonObject>())
                    Upsert(state);
        }
        if (_channelId != null)
            foreach (var evt in ChannelEvents)
                await SubscribeAsync(evt, new JsonObject { ["channel_id"] = _channelId });
        PublishState();
    }

    private static readonly string[] ChannelEvents = { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP" };

    // { user: {id, username, global_name, avatar}, nick, mute, voice_state: {self_mute, self_deaf, mute, deaf} }
    private void Upsert(JsonObject state)
    {
        var user = state["user"] as JsonObject;
        var id = user?["id"]?.GetValue<string>();
        if (id == null) return;
        var voice = state["voice_state"] as JsonObject;
        var name = state["nick"]?.GetValue<string>() ?? user?["global_name"]?.GetValue<string>() ?? user?["username"]?.GetValue<string>() ?? id;
        var avatar = user?["avatar"]?.GetValue<string>();
        var avatarUrl = avatar != null
            ? $"https://cdn.discordapp.com/avatars/{id}/{avatar}.png?size=64"
            : $"https://cdn.discordapp.com/embed/avatars/{(ulong.TryParse(id, out var n) ? (n >> 22) % 6 : 0)}.png";
        var muted = Bool(voice, "self_mute") || Bool(voice, "mute") || Bool(state, "mute");
        var deafened = Bool(voice, "self_deaf") || Bool(voice, "deaf");
        var speaking = _members.TryGetValue(id, out var old) && old.Speaking;
        _members[id] = new DiscordMember(id, name, avatarUrl, speaking, muted, deafened);
    }

    private static bool Bool(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private void ApplySettings(JsonObject? data)
    {
        if (data == null) return;
        _selfMute = Bool(data, "mute");
        _selfDeaf = Bool(data, "deaf");
    }

    private void OnEvent(string evt, JsonObject? data)
    {
        if (evt == "VOICE_CHANNEL_SELECT")
        {
            _ = SelectChannelAsync();
            return;
        }
        lock (_gate) Apply(evt, data);
        PublishState();
    }

    private void Apply(string evt, JsonObject? data)
    {
        switch (evt)
        {
            case "VOICE_SETTINGS_UPDATE":
                ApplySettings(data);
                break;
            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                if (data != null) Upsert(data);
                break;
            case "VOICE_STATE_DELETE":
                if (data?["user"]?["id"]?.GetValue<string>() is { } gone) _members.Remove(gone);
                break;
            case "SPEAKING_START":
            case "SPEAKING_STOP":
                if (data?["user_id"]?.GetValue<string>() is { } who && _members.TryGetValue(who, out var m))
                    _members[who] = m with { Speaking = evt == "SPEAKING_START" };
                break;
            default:
                return;
        }
    }

    private void ClearChannel()
    {
        lock (_gate)
        {
            _channelId = _channelName = null;
            _members.Clear();
        }
        PublishState();
    }

    private void PublishState()
    {
        DiscordVoiceState? snapshot;
        lock (_gate)
            snapshot = _channelId == null || !Connected
                ? null
                : new DiscordVoiceState(_channelName ?? "음성 채널", _members.Values.ToList(), _selfMute, _selfDeaf);
        _dispatcher.BeginInvoke(() =>
        {
            Current = snapshot;
            Changed?.Invoke();
        });
    }

    private void SetProblem(string? problem)
    {
        if (Problem == problem) return;
        Problem = problem;
        _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    // --- IPC framing: [opcode int32][length int32][utf-8 JSON] ---

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken stop)
    {
        var header = new byte[8];
        while (!stop.IsCancellationRequested)
        {
            await pipe.ReadExactlyAsync(header, stop);
            var op = BitConverter.ToInt32(header, 0);
            var length = BitConverter.ToInt32(header, 4);
            var body = new byte[length];
            await pipe.ReadExactlyAsync(body, stop);
            if (op == 2) throw new IOException("Discord가 연결을 닫았습니다: " + Encoding.UTF8.GetString(body));
            if (op == 3) { await WriteRawAsync(4, body); continue; } // ping -> pong
            if (op != 1) continue;
            if (JsonNode.Parse(body) is not JsonObject message) continue;
            var cmd = message["cmd"]?.GetValue<string>();
            var evtName = message["evt"]?.GetValue<string>();
            // A trace of the conversation (minus the chatty speaking events), so a stuck setup can be diagnosed from log.txt.
            if (evtName is not ("SPEAKING_START" or "SPEAKING_STOP"))
                Log.Write($"discord <- {cmd} {evtName} {Truncate(message["data"]?.ToJsonString())}");
            if (cmd == "DISPATCH" && evtName == "READY") _ready?.TrySetResult();
            var nonce = message["nonce"]?.GetValue<string>();
            if (nonce != null && _pending.TryRemove(nonce, out var waiter))
            {
                if (message["evt"]?.GetValue<string>() == "ERROR")
                    waiter.TrySetException(new InvalidOperationException(message["data"]?["message"]?.GetValue<string>() ?? "Discord 오류"));
                else
                    waiter.TrySetResult(message);
                continue;
            }
            if (message["cmd"]?.GetValue<string>() == "DISPATCH" && message["evt"]?.GetValue<string>() is { } evt)
                OnEvent(evt, message["data"] as JsonObject);
        }
    }

    private TaskCompletionSource? _ready;

    // Tokens never reach the log: AUTHORIZE/AUTHENTICATE replies are cut short, and so is everything else.
    private static string Truncate(string? text) =>
        text == null ? "" : text.Contains("access_token") || text.Contains("\"code\"") ? "(credentials omitted)" : text.Length > 160 ? text[..160] + "…" : text;

    private Task SubscribeAsync(string evt, JsonObject? args) => SendRawAsync("SUBSCRIBE", evt, args ?? new JsonObject());

    private Task SendAsync(string cmd, JsonObject args) => Connected ? CommandAsync(cmd, args) : Task.CompletedTask;

    private async Task<JsonObject> CommandAsync(string cmd, JsonObject args, TimeSpan? timeout = null)
    {
        var nonce = Guid.NewGuid().ToString();
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = waiter;
        Log.Write($"discord -> {cmd}");
        await WriteAsync(1, new JsonObject { ["cmd"] = cmd, ["args"] = args, ["nonce"] = nonce });
        try
        {
            return await waiter.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(nonce, out _);
            throw new TimeoutException(cmd == "AUTHORIZE"
                ? "디스코드에서 승인하지 않았습니다(시간 초과). 디스코드 창을 열어 두고 다시 \"연결\"을 눌러 주세요"
                : $"Discord가 {cmd}에 응답하지 않았습니다");
        }
    }

    private Task SendRawAsync(string cmd, string evt, JsonObject args) =>
        WriteAsync(1, new JsonObject { ["cmd"] = cmd, ["evt"] = evt, ["args"] = args, ["nonce"] = Guid.NewGuid().ToString() });

    private Task WriteAsync(int op, JsonObject payload) => WriteRawAsync(op, Encoding.UTF8.GetBytes(payload.ToJsonString()));

    private async Task WriteRawAsync(int op, byte[] body)
    {
        var pipe = _pipe ?? throw new IOException("not connected");
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        await _writeLock.WaitAsync();
        try
        {
            await pipe.WriteAsync(frame);
            await pipe.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailPending()
    {
        foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException("연결이 끊겼습니다"));
        _pending.Clear();
    }

    private static async Task Delay(int seconds, CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), stop); }
        catch (TaskCanceledException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _pipe?.Dispose();
    }
}
