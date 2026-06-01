using System.Collections.Concurrent;
using System.Numerics;
using CineSync.Shared;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;

namespace CineSync.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/cinesync";

    public Configuration Config { get; }
    private readonly WindowSystem _windows = new("CineSync");
    private readonly ConfigWindow _configWindow;

    // Synced screens by id (thread-safe: written from the network thread, read on the draw thread).
    private readonly ConcurrentDictionary<string, ScreenDto> _screens = new();
    public IReadOnlyCollection<ScreenDto> Screens => (IReadOnlyCollection<ScreenDto>)_screens.Values;
    public IReadOnlyList<MemberDto> Members { get; private set; } = new List<MemberDto>();

    private SyncClient? _client;
    public bool Connected => _client?.Connected ?? false;
    public string StatusLine { get; private set; } = "Not connected.";

    // ---- Edit / gizmo state ----
    public bool EditMode;
    public string? SelectedId;
    private readonly Gizmo _gizmo = new();
    private readonly MediaManager _media = new();
    private long _lastPush;
    private bool _wasGizmoActive;

    /// <summary>The local player (object-table slot 0), or null if not in-world.</summary>
    private IGameObject? LocalPlayer => Svc.ObjectTable[0];

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();
        Config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _configWindow = new ConfigWindow(this);
        _windows.AddWindow(_configWindow);

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the CineSync window. '/cinesync edit' toggles the move/resize gizmo."
        });

        Svc.PluginInterface.UiBuilder.Draw += OnDraw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += ToggleUi;
        Svc.PluginInterface.UiBuilder.OpenMainUi += ToggleUi;

        if (Config.AutoConnect && !string.IsNullOrWhiteSpace(Config.ServerUrl))
            _ = Connect();
    }

    // ---- Connection ----------------------------------------------------------------

    public async Task Connect()
    {
        await Disconnect();
        var name = string.IsNullOrWhiteSpace(Config.DisplayName)
            ? (LocalPlayer?.Name.TextValue ?? "Anon")
            : Config.DisplayName;

        _client = new SyncClient(Config.ServerUrl, Config.RoomCode, name);
        _client.Status += s => { StatusLine = s; Svc.Log.Info($"[CineSync] {s}"); };
        _client.RoomStateReceived += OnRoomState;
        _client.ScreenUpserted += s => _screens[s.Id] = s;
        _client.ScreenRemoved += id => _screens.TryRemove(id, out _);
        _client.MembersUpdated += m => Members = m;
        _client.PlaybackUpdated += p => _media.ApplyPlayback(p);

        try { await _client.ConnectAsync(); }
        catch (Exception ex) { StatusLine = "Connect failed: " + ex.Message; Svc.Log.Error(ex, "connect"); }
    }

    public async Task Disconnect()
    {
        if (_client is not null)
        {
            await _client.DisconnectAsync();
            _client.Dispose();
            _client = null;
        }
        _screens.Clear();
        Members = new List<MemberDto>();
    }

    private void OnRoomState(List<ScreenDto> screens)
    {
        _screens.Clear();
        foreach (var s in screens) _screens[s.Id] = s;
    }

    // ---- Host actions --------------------------------------------------------------

    /// <summary>Create a screen at the local player, facing the way they face, and broadcast it.</summary>
    public void CreateScreenHere()
    {
        var p = LocalPlayer;
        if (p is null || _client is null) { StatusLine = "Log in and connect first."; return; }

        var pos = p.Position;
        var screen = new ScreenDto
        {
            OwnerName = p.Name.TextValue,
            TerritoryId = Svc.ClientState.TerritoryType,
            X = pos.X,
            Y = pos.Y + 2.0f,                 // float it above the ground
            Z = pos.Z,
            Yaw = p.Rotation,
            Width = 4.0f,
            Height = 2.25f,
            MediaUrl = Config.DefaultMediaUrl,
        };
        _screens[screen.Id] = screen;       // optimistic local add
        SelectedId = screen.Id;             // auto-select for immediate editing
        EditMode = true;
        _ = _client.UpsertScreen(screen);
    }

    public void DeleteScreen(string id)
    {
        _screens.TryRemove(id, out _);
        _ = _client?.RemoveScreen(id);
    }

    public void PushScreenUpdate(ScreenDto s) => _ = _client?.UpsertScreen(s);

    /// <summary>Host: play/pause a screen for everyone (applies locally + broadcasts).</summary>
    public void SetPlayback(string id, bool paused)
    {
        var dto = new PlaybackDto { ScreenId = id, Paused = paused };
        _media.ApplyPlayback(dto);
        _ = _client?.UpdatePlayback(dto);
    }

    // ---- Draw ----------------------------------------------------------------------

    private void OnDraw()
    {
        _windows.Draw();
        DrawWorldScreens();
    }

    /// <summary>
    /// M1 placeholder renderer: draws each screen as a world-anchored quad (border + label).
    /// M3 replaces the fill with the live video texture.
    /// </summary>
    private void DrawWorldScreens()
    {
        if (!Svc.ClientState.IsLoggedIn) return;
        _media.PruneExcept(_screens.Keys);
        var territory = Svc.ClientState.TerritoryType;
        var dl = ImGui.GetBackgroundDrawList();

        foreach (var s in _screens.Values)
        {
            if (s.TerritoryId != territory) continue;

            var (right, up) = ScreenGeom.Basis(s);
            var c = new Vector3(s.X, s.Y, s.Z);
            var hw = s.Width * 0.5f;
            var hh = s.Height * 0.5f;

            var tl = c - right * hw + up * hh;
            var tr = c + right * hw + up * hh;
            var br = c + right * hw - up * hh;
            var bl = c - right * hw - up * hh;

            if (!Svc.GameGui.WorldToScreen(tl, out var p1)) continue;
            if (!Svc.GameGui.WorldToScreen(tr, out var p2)) continue;
            if (!Svc.GameGui.WorldToScreen(br, out var p3)) continue;
            if (!Svc.GameGui.WorldToScreen(bl, out var p4)) continue;

            var tex = _media.GetTexture(s);
            if (tex != null)
            {
                dl.AddImageQuad(tex.Handle, p1, p2, p3, p4,
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), 0xFFFFFFFFu);
            }
            else
            {
                dl.AddQuadFilled(p1, p2, p3, p4, ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.08f, 0.85f)));
                var label = string.IsNullOrWhiteSpace(s.MediaUrl) ? $"[CineSync] {s.OwnerName}" : s.MediaUrl;
                var mid = (p1 + p3) * 0.5f;
                var textSize = ImGui.CalcTextSize(label);
                dl.AddText(mid - textSize * 0.5f, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), label);
            }

            var selected = s.Id == SelectedId;
            var border = selected ? new Vector4(1f, 0.85f, 0.3f, 1f) : new Vector4(0.4f, 0.7f, 1f, 1f);
            dl.AddQuad(p1, p2, p3, p4, ImGui.GetColorU32(border), selected ? 3.5f : 2f);
        }

        // Move/resize gizmo for the selected screen (only while Edit mode is on — easily hidden).
        if (EditMode && SelectedId != null && _screens.TryGetValue(SelectedId, out var sel) && sel.TerritoryId == territory)
        {
            if (_gizmo.Draw(sel))
            {
                sel.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var now = Environment.TickCount64;
                if (now - _lastPush >= 50) { _lastPush = now; PushScreenUpdate(sel); }
            }
            if (_wasGizmoActive && !_gizmo.Active) PushScreenUpdate(sel); // final sync on release
            _wasGizmoActive = _gizmo.Active;
        }
    }

    // ---- Plumbing ------------------------------------------------------------------

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("edit", StringComparison.OrdinalIgnoreCase)) { EditMode = !EditMode; return; }
        ToggleUi();
    }
    public void ToggleUi() => _configWindow.Toggle();
    public void SaveConfig() => Svc.PluginInterface.SavePluginConfig(Config);

    public void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= OnDraw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= ToggleUi;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= ToggleUi;
        Svc.Commands.RemoveHandler(Command);
        _windows.RemoveAllWindows();
        _client?.Dispose();
        _media.Dispose();
        SaveConfig();
    }
}
