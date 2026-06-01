using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CineSync.Shared;

namespace CineSync.Server;

/// <summary>A single connected client socket, with a serialized send path.</summary>
public sealed class Connection
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Room { get; set; } = "";
    public string Name { get; set; } = "Anon";
    public WebSocket Socket { get; }
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Connection(WebSocket socket) => Socket = socket;

    public async Task SendAsync(string text)
    {
        if (Socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync();
        try
        {
            if (Socket.State == WebSocketState.Open)
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* peer gone; cleanup handles removal */ }
        finally { _sendLock.Release(); }
    }
}

/// <summary>
/// Raw-WebSocket relay. Tracks connections per room and fans screen/playback state
/// out to everyone in the room. State of record lives in <see cref="RoomStore"/>.
/// </summary>
public sealed class Relay
{
    private readonly RoomStore _store;
    private readonly ILogger<Relay> _log;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Connection>> _rooms =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public Relay(RoomStore store, ILogger<Relay> log)
    {
        _store = store;
        _log = log;
    }

    public async Task HandleSocket(WebSocket socket)
    {
        var conn = new Connection(socket);
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                await Dispatch(conn, Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "socket loop ended"); }
        finally { await Cleanup(conn); }
    }

    private async Task Dispatch(Connection conn, string text)
    {
        Envelope? env;
        try { env = JsonSerializer.Deserialize<Envelope>(text, J); }
        catch { return; }
        if (env is null) return;

        switch (env.Type)
        {
            case MsgType.Ping:
                // Reply so the downstream direction also stays warm through proxies/tunnels.
                await conn.SendAsync(Pack(MsgType.Pong, new { }));
                break;

            case MsgType.Join:
            {
                var join = Deserialize<JoinDto>(env.Json);
                if (join is null || string.IsNullOrWhiteSpace(join.RoomCode)) return;
                conn.Room = join.RoomCode.Trim();
                conn.Name = string.IsNullOrWhiteSpace(join.DisplayName) ? "Anon" : join.DisplayName.Trim();
                _rooms.GetOrAdd(conn.Room, _ => new()).TryAdd(conn.Id, conn);
                _store.AddMember(conn.Room, conn.Id, conn.Name);
                _log.LogInformation("{Name} joined room {Room}", conn.Name, conn.Room);
                await conn.SendAsync(Pack(MsgType.RoomState, _store.GetScreens(conn.Room)));
                await BroadcastMembers(conn.Room);
                break;
            }
            case MsgType.Upsert:
            {
                if (conn.Room == "") return;
                var s = Deserialize<ScreenDto>(env.Json);
                if (s is null) return;
                s.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _store.UpsertScreen(conn.Room, s);
                await Broadcast(conn.Room, Pack(MsgType.Upsert, s));
                break;
            }
            case MsgType.Remove:
            {
                if (conn.Room == "") return;
                var id = Deserialize<string>(env.Json);
                if (string.IsNullOrEmpty(id)) return;
                _store.RemoveScreen(conn.Room, id);
                await Broadcast(conn.Room, Pack(MsgType.Remove, id));
                break;
            }
            case MsgType.Playback:
            {
                if (conn.Room == "") return;
                var p = Deserialize<PlaybackDto>(env.Json);
                if (p is null) return;
                p.ServerTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await Broadcast(conn.Room, Pack(MsgType.Playback, p), exclude: conn.Id);
                break;
            }
        }
    }

    private async Task Cleanup(Connection conn)
    {
        if (conn.Room != "" && _rooms.TryGetValue(conn.Room, out var set))
        {
            set.TryRemove(conn.Id, out _);
            _store.RemoveMember(conn.Room, conn.Id);
            if (set.IsEmpty) _rooms.TryRemove(conn.Room, out _);
            else await BroadcastMembers(conn.Room);
        }
        try { if (conn.Socket.State == WebSocketState.Open)
            await conn.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
        catch { }
    }

    private Task BroadcastMembers(string room) =>
        Broadcast(room, Pack(MsgType.Members, _store.GetMembers(room)));

    private async Task Broadcast(string room, string text, string? exclude = null)
    {
        if (!_rooms.TryGetValue(room, out var set)) return;
        foreach (var c in set.Values)
        {
            if (exclude is not null && c.Id == exclude) continue;
            await c.SendAsync(text);
        }
    }

    private static string Pack(string type, object payload) =>
        JsonSerializer.Serialize(new Envelope { Type = type, Json = JsonSerializer.Serialize(payload, J) }, J);

    private static T? Deserialize<T>(string? json) =>
        json is null ? default : JsonSerializer.Deserialize<T>(json, J);
}
