namespace CineSync.Shared;

/// <summary>
/// A synced "screen" (TV) placed in the world. Sent host -> server -> all room members.
/// Position is in FFXIV world coordinates; each client renders its own copy at this spot.
/// </summary>
public class ScreenDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerName { get; set; } = "";

    // World anchor
    public uint TerritoryId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }          // radians about world Y (turn left/right)
    public float Pitch { get; set; }        // radians about local X (tilt up/down)
    public float Roll { get; set; }         // radians about local Z (roll)

    // Size in world units (16:9 default)
    public float Width { get; set; } = 4.0f;
    public float Height { get; set; } = 2.25f;

    // Content
    public string MediaUrl { get; set; } = "";   // e.g. your Owncast HLS / embed URL
    public bool Muted { get; set; } = false;
    public float Volume { get; set; } = 1.0f;

    public long UpdatedAtUnixMs { get; set; }
}

/// <summary>Playback control for a screen (host drives, others follow).</summary>
public class PlaybackDto
{
    public string ScreenId { get; set; } = "";
    public bool Paused { get; set; }
    public double PositionSeconds { get; set; }
    public long ServerTimestampUnixMs { get; set; }
}

/// <summary>A connected room member (for presence/UI).</summary>
public class MemberDto
{
    public string ConnectionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

/// <summary>Sent by a client when joining a room.</summary>
public class JoinDto
{
    public string RoomCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// Wire envelope for the raw-WebSocket JSON protocol. <see cref="Json"/> holds the
/// serialized payload whose concrete type is determined by <see cref="Type"/>.
/// </summary>
public class Envelope
{
    public string Type { get; set; } = "";
    public string? Json { get; set; }
}

/// <summary>Message type tags shared by server and client.</summary>
public static class MsgType
{
    // client -> server
    public const string Join      = "join";       // JoinDto
    public const string Upsert    = "upsert";     // ScreenDto
    public const string Remove    = "remove";     // string id
    public const string Playback  = "playback";   // PlaybackDto

    // keepalive (both directions)
    public const string Ping = "ping";
    public const string Pong = "pong";

    // server -> client
    public const string RoomState = "roomState";  // List<ScreenDto>
    public const string Members   = "members";    // List<MemberDto>
    // Upsert/Remove/Playback are echoed back to the room with the same tags.
}

/// <summary>Networking constants.</summary>
public static class Net
{
    public const string Path = "/ws";
}
