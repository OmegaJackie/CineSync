using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CineSync.Shared;

namespace CineSync.Plugin;

/// <summary>
/// Raw-WebSocket client with a supervisor loop: connects, joins the room, runs a heartbeat,
/// and on any drop waits (exponential backoff) and reconnects + re-joins. No external deps.
/// </summary>
public sealed class SyncClient : IDisposable
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(25);

    private readonly string _url;
    private readonly JoinDto _join;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _stopped;

    public bool Connected => _ws?.State == WebSocketState.Open;

    public event Action<List<ScreenDto>>? RoomStateReceived;
    public event Action<ScreenDto>? ScreenUpserted;
    public event Action<string>? ScreenRemoved;
    public event Action<PlaybackDto>? PlaybackUpdated;
    public event Action<List<MemberDto>>? MembersUpdated;
    public event Action<string>? Status;

    public SyncClient(string url, string room, string name)
    {
        _url = url;
        _join = new JoinDto { RoomCode = room, DisplayName = name };
    }

    /// <summary>Starts the background connect/reconnect supervisor and returns immediately.</summary>
    public Task ConnectAsync()
    {
        _stopped = false;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => SuperviseAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        _stopped = true;
        try { _cts?.Cancel(); } catch { }
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
        }
        Status?.Invoke("Disconnected.");
    }

    public Task UpsertScreen(ScreenDto s) => SendAsync(MsgType.Upsert, s);
    public Task RemoveScreen(string id) => SendAsync(MsgType.Remove, id);
    public Task UpdatePlayback(PlaybackDto p) => SendAsync(MsgType.Playback, p);

    // ---- supervisor ----

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var backoffMs = 1000;
        while (!ct.IsCancellationRequested && !_stopped)
        {
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20); // WS-level ping frames
                await _ws.ConnectAsync(new Uri(_url), conn.Token);
                Status?.Invoke($"Connected to {_url} (room '{_join.RoomCode}').");
                backoffMs = 1000;

                await SendAsync(MsgType.Join, _join);                    // (re)join the room
                var heartbeat = Task.Run(() => HeartbeatAsync(conn.Token));
                await ReceiveLoopAsync(conn.Token);                       // returns when the socket ends
                conn.Cancel();
                try { await heartbeat; } catch { }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex) { Status?.Invoke("Connection lost: " + ex.Message); }
            finally { try { _ws?.Dispose(); } catch { } _ws = null; }

            if (ct.IsCancellationRequested || _stopped) break;
            Status?.Invoke($"Reconnecting in {backoffMs / 1000}s...");
            try { await Task.Delay(backoffMs, ct); } catch { break; }
            backoffMs = Math.Min(backoffMs * 2, 15000);
        }
        Status?.Invoke("Stopped.");
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatEvery, ct); } catch { return; }
            try { await SendAsync(MsgType.Ping, null); } catch { return; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        while (_ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            Handle(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
        }
    }

    private void Handle(string text)
    {
        Envelope? env;
        try { env = JsonSerializer.Deserialize<Envelope>(text, J); }
        catch { return; }
        if (env is null) return;

        switch (env.Type)
        {
            case MsgType.RoomState: RoomStateReceived?.Invoke(De<List<ScreenDto>>(env.Json) ?? new()); break;
            case MsgType.Upsert:    if (De<ScreenDto>(env.Json) is { } s) ScreenUpserted?.Invoke(s); break;
            case MsgType.Remove:    if (De<string>(env.Json) is { } id) ScreenRemoved?.Invoke(id); break;
            case MsgType.Playback:  if (De<PlaybackDto>(env.Json) is { } p) PlaybackUpdated?.Invoke(p); break;
            case MsgType.Members:   MembersUpdated?.Invoke(De<List<MemberDto>>(env.Json) ?? new()); break;
            case MsgType.Pong:      break; // keepalive ack
        }
    }

    private async Task SendAsync(string type, object? payload)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        var env = new Envelope { Type = type, Json = payload is null ? null : JsonSerializer.Serialize(payload, J) };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(env, J));
        await _sendLock.WaitAsync();
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* drop; supervisor will reconnect */ }
        finally { _sendLock.Release(); }
    }

    private static T? De<T>(string? json) => json is null ? default : JsonSerializer.Deserialize<T>(json, J);

    public void Dispose()
    {
        _stopped = true;
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
