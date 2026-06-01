using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CineSync.Plugin;

public sealed class ConfigWindow : Window
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    public ConfigWindow(Plugin plugin) : base("CineSync###CineSyncMain")
    {
        _plugin = plugin;
        Size = new Vector2(500, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        // ---- Connection ----
        ImGui.TextUnformatted("Connection");
        var server = Cfg.ServerUrl;
        if (ImGui.InputText("Server URL", ref server, 256)) Cfg.ServerUrl = server;
        var room = Cfg.RoomCode;
        if (ImGui.InputText("Room code", ref room, 64)) Cfg.RoomCode = room;
        var name = Cfg.DisplayName;
        if (ImGui.InputText("Display name", ref name, 64)) Cfg.DisplayName = name;
        var url = Cfg.DefaultMediaUrl;
        if (ImGui.InputText("Default media URL", ref url, 512)) Cfg.DefaultMediaUrl = url;
        ImGui.TextDisabled("Pre-fills new screens. Select a screen below to set/change its URL.");
        var auto = Cfg.AutoConnect;
        if (ImGui.Checkbox("Auto-connect on load", ref auto)) Cfg.AutoConnect = auto;

        ImGui.Separator();
        if (_plugin.Connected)
        {
            if (ImGui.Button("Disconnect")) { _plugin.SaveConfig(); _ = _plugin.Disconnect(); }
        }
        else
        {
            if (ImGui.Button("Connect")) { _plugin.SaveConfig(); _ = _plugin.Connect(); }
        }
        ImGui.SameLine();
        if (ImGui.Button("Save")) _plugin.SaveConfig();
        ImGui.TextWrapped(_plugin.StatusLine);

        ImGui.Separator();

        // ---- Host controls ----
        ImGui.TextUnformatted("Host controls");
        if (ImGui.Button("Create screen here")) _plugin.CreateScreenHere();
        ImGui.SameLine();
        var edit = _plugin.EditMode;
        if (ImGui.Checkbox("Edit mode (show gizmo)", ref edit)) _plugin.EditMode = edit;
        ImGui.TextDisabled("Gizmo: drag red/green/blue = move X/Y/Z, cyan = width, yellow = height. (/cinesync edit toggles)");

        ImGui.Separator();

        // ---- Screen list ----
        ImGui.TextUnformatted($"Screens ({_plugin.Screens.Count})");
        foreach (var s in _plugin.Screens.ToList())
        {
            ImGui.PushID(s.Id);
            if (ImGui.SmallButton("Select")) _plugin.SelectedId = s.Id;
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
            {
                if (_plugin.SelectedId == s.Id) _plugin.SelectedId = null;
                _plugin.DeleteScreen(s.Id);
                ImGui.PopID();
                continue;
            }
            ImGui.SameLine();
            var tag = s.Id == _plugin.SelectedId ? "[*] " : "";
            var label = string.IsNullOrEmpty(s.MediaUrl) ? "(no media url set)" : s.MediaUrl;
            ImGui.TextUnformatted($"{tag}{s.OwnerName}  -  {label}");
            ImGui.PopID();
        }

        // ---- Selected-screen precision editor ----
        var sel = _plugin.Screens.ToList().FirstOrDefault(x => x.Id == _plugin.SelectedId);
        if (sel != null)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"Editing: {sel.OwnerName}'s screen");

            var pos = new Vector3(sel.X, sel.Y, sel.Z);
            if (ImGui.DragFloat3("Position (X/Y/Z)", ref pos, 0.05f, -100000f, 100000f, "%.2f", ImGuiSliderFlags.None))
            {
                sel.X = pos.X; sel.Y = pos.Y; sel.Z = pos.Z;
                _plugin.PushScreenUpdate(sel);
            }

            var rot = new Vector3(sel.Yaw, sel.Pitch, sel.Roll);
            if (ImGui.DragFloat3("Rotation (Yaw/Pitch/Roll)", ref rot, 0.02f, -7f, 7f, "%.2f", ImGuiSliderFlags.None))
            {
                sel.Yaw = rot.X; sel.Pitch = rot.Y; sel.Roll = rot.Z;
                _plugin.PushScreenUpdate(sel);
            }

            var size = new Vector2(sel.Width, sel.Height);
            if (ImGui.DragFloat2("Size (W x H)", ref size, 0.05f, 0.3f, 500f, "%.2f", ImGuiSliderFlags.None))
            {
                sel.Width = size.X; sel.Height = size.Y;
                _plugin.PushScreenUpdate(sel);
            }

            var murl = sel.MediaUrl;
            if (ImGui.InputText("Media URL##sel", ref murl, 512)) { sel.MediaUrl = murl; _plugin.PushScreenUpdate(sel); }
            ImGui.TextDisabled("A stream/file URL VLC can play. Owncast HLS:");
            ImGui.TextDisabled("  http://SERVER:8080/hls/stream.m3u8   (NOT the /embed page)");
            ImGui.TextDisabled("Test stream: https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8");

            if (ImGui.Button("Play")) _plugin.SetPlayback(sel.Id, false);
            ImGui.SameLine();
            if (ImGui.Button("Pause")) _plugin.SetPlayback(sel.Id, true);
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Members ({_plugin.Members.Count})");
        foreach (var m in _plugin.Members)
            ImGui.BulletText(m.DisplayName);
    }
}
