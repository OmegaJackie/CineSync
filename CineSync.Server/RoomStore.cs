using System.Collections.Concurrent;
using CineSync.Shared;

namespace CineSync.Server;

/// <summary>In-memory store of rooms, their screens, and members. Thread-safe.</summary>
public class RoomStore
{
    private class Room
    {
        public readonly ConcurrentDictionary<string, ScreenDto> Screens = new();
        public readonly ConcurrentDictionary<string, string> Members = new(); // connId -> name
    }

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);

    private Room Get(string code) => _rooms.GetOrAdd(code, _ => new Room());

    public void AddMember(string code, string connId, string name) => Get(code).Members[connId] = name;

    public void RemoveMember(string code, string connId)
    {
        if (_rooms.TryGetValue(code, out var r))
        {
            r.Members.TryRemove(connId, out _);
            // Optionally drop empty rooms to free memory
            if (r.Members.IsEmpty && r.Screens.IsEmpty)
                _rooms.TryRemove(code, out _);
        }
    }

    public List<ScreenDto> GetScreens(string code) => Get(code).Screens.Values.ToList();

    public List<MemberDto> GetMembers(string code) =>
        Get(code).Members.Select(kv => new MemberDto { ConnectionId = kv.Key, DisplayName = kv.Value }).ToList();

    public void UpsertScreen(string code, ScreenDto screen) => Get(code).Screens[screen.Id] = screen;

    public void RemoveScreen(string code, string id)
    {
        if (_rooms.TryGetValue(code, out var r))
            r.Screens.TryRemove(id, out _);
    }
}
