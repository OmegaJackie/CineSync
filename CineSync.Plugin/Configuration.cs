using Dalamud.Configuration;

namespace CineSync.Plugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Your self-hosted CineSync server, e.g. ws://1.2.3.4:5252/ws or wss://host/ws.</summary>
    public string ServerUrl { get; set; } = "ws://localhost:5252/ws";

    /// <summary>Shared room code (acts as the password). Everyone in the same code sees the same screens.</summary>
    public string RoomCode { get; set; } = "movienight";

    /// <summary>Name shown to others in the room.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Auto-connect when the plugin loads.</summary>
    public bool AutoConnect { get; set; } = false;

    /// <summary>Default media URL pre-filled when creating a screen (e.g. your Owncast embed URL).</summary>
    public string DefaultMediaUrl { get; set; } = "";

    /// <summary>
    /// Draw screens as real 3D geometry that depth-tests against the game's scene depth buffer,
    /// so characters and walls in front of a screen hide it. Off = the old flat overlay, which
    /// paints over everything (that is the "my body disappears behind the TV" behaviour).
    /// </summary>
    public bool DepthOcclusion { get; set; } = true;

    /// <summary>
    /// Metres of slack in the depth comparison. Too low and a screen resting against a wall
    /// z-fights/shimmers; too high and someone standing very close in front of it bleeds through.
    /// </summary>
    public float OcclusionTolerance { get; set; } = 0.05f;
}
