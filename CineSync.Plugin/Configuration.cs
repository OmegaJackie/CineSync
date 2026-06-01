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
}
