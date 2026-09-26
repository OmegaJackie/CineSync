using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using CineSync.Shared;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using Pictomancy;

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

    /// <summary>Human-readable state of the depth-occluded renderer, for the config window.</summary>
    public string OcclusionStatus => _occlusion.Describe();
    public string StatusLine { get; private set; } = "Not connected.";

    // ---- Edit / gizmo state ----
    public bool EditMode;
    public string? SelectedId;
    private readonly Gizmo _gizmo = new();
    private readonly MediaManager _media = new();
    private readonly OccludedScreenRenderer _occlusion = new();
    private long _lastPush;
    private bool _wasGizmoActive;

    /// <summary>The local player (object-table slot 0), or null if not in-world.</summary>
    private IGameObject? LocalPlayer => Svc.ObjectTable[0];

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();
        Config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        SetVolume(Config.Volume, Config.Muted);    // also clamps a hand-edited config

        _configWindow = new ConfigWindow(this);
        _windows.AddWindow(_configWindow);

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the CineSync window. '/cinesync edit' toggles the move/resize gizmo; "
                        + "'/cinesync flat' toggles depth occlusion off/on; "
                        + "'/cinesync volume 0-100' sets your volume (+N/-N nudges it); "
                        + "'/cinesync mute' toggles mute."
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

    // ---- Local audio ---------------------------------------------------------------

    /// <summary>Your own volume (0-100) and mute, for every screen. Never broadcast. Doesn't save.</summary>
    public void SetVolume(int volume, bool muted)
    {
        Config.Volume = Math.Clamp(volume, 0, 100);
        Config.Muted = muted;
        _media.SetVolume(Config.Volume, Config.Muted);
    }

    // ---- Draw ----------------------------------------------------------------------

    private void OnDraw()
    {
        _windows.Draw();
        DrawWorldScreens();
    }

    /// <summary>
    /// Draws every synced screen in the world.
    ///
    /// Preferred path is <see cref="OccludedScreenRenderer"/>: the screen is real world-space
    /// geometry, depth-tested per pixel against the game's scene depth buffer, so a character
    /// standing in front of it occludes it properly. If occlusion isn't available — still starting,
    /// unsupported, switched off, or it failed mid-session — this falls back to the original flat
    /// ImGui overlay, which has no depth and therefore paints over everything.
    /// </summary>
    private void DrawWorldScreens()
    {
        if (!Svc.ClientState.IsLoggedIn) return;
        _media.PruneExcept(_screens.Keys);
        _media.EnforceVolume();
        var territory = Svc.ClientState.TerritoryType;

        // Materialise once: the occluded path costs a full-viewport render + composite per frame,
        // so it must not run at all when this territory has no screens.
        var here = ScreensIn(territory);
        if (here.Count == 0) { DrawGizmo(territory); return; }

        var drewOccluded = false;
        if (Config.DepthOcclusion)
        {
            _occlusion.EnsureInitialised();
            drewOccluded = _occlusion.TryDraw(Config.OcclusionTolerance, pct =>
            {
                foreach (var s in here) DrawScreenOccluded(pct, s);
            });
        }

        if (!drewOccluded)
            foreach (var s in here) DrawScreenFlat(s);

        DrawGizmo(territory);
    }

    private List<ScreenDto> ScreensIn(uint territory)
    {
        var result = new List<ScreenDto>();
        foreach (var s in _screens.Values)
            if (s.TerritoryId == territory) result.Add(s);
        return result;
    }

    /// <summary>The four world-space corners of a screen, in TL / TR / BR / BL order.</summary>
    private static (Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl) Corners(ScreenDto s, Vector3 right, Vector3 up)
    {
        var c = new Vector3(s.X, s.Y, s.Z);
        var hw = right * (s.Width * 0.5f);
        var hh = up * (s.Height * 0.5f);
        return (c - hw + hh, c + hw + hh, c + hw - hh, c - hw - hh);
    }

    /// <summary>Depth-occluded path — the screen is drawn as geometry inside the 3D scene.</summary>
    private void DrawScreenOccluded(PctDrawList pct, ScreenDto s)
    {
        var (right, up) = ScreenGeom.Basis(s);
        var centre = new Vector3(s.X, s.Y, s.Z);
        var (tl, tr, br, bl) = Corners(s, right, up);

        var tex = _media.GetTexture(s);
        if (tex != null)
        {
            // AddImage takes FULL edge vectors: a corner is centre +/- 0.5*right +/- 0.5*down.
            pct.AddImage(tex, centre, right * s.Width, -up * s.Height);
        }
        else
        {
            pct.AddQuadFilled(tl, tr, br, bl, ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.08f, 0.85f)));
            var label = string.IsNullOrWhiteSpace(s.MediaUrl) ? $"[CineSync] {s.OwnerName}" : s.MediaUrl;
            pct.AddText(centre, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), label);
        }

        // Edit visuals (gold highlight) only while editing; pure video when just watching.
        if (!EditMode) return;
        var selected = s.Id == SelectedId;
        var border = selected ? new Vector4(1f, 0.85f, 0.3f, 1f) : new Vector4(0.4f, 0.7f, 1f, 1f);
        // The border stays faintly visible through geometry so you can still find a screen you are
        // placing behind a wall; the video itself is fully occluded.
        pct.AddQuad(tl, tr, br, bl, ImGui.GetColorU32(border), selected ? 3.5f : 2f,
            new PctDxParams { OccludedAlpha = 0.35f, OcclusionTolerance = Config.OcclusionTolerance });
    }

    /// <summary>
    /// Fallback path: the original flat ImGui overlay. It has no depth buffer, so it paints over
    /// characters and walls — used only when depth occlusion is off or unavailable.
    /// </summary>
    private void DrawScreenFlat(ScreenDto s)
    {
        var dl = ImGui.GetBackgroundDrawList();
        var (right, up) = ScreenGeom.Basis(s);
        var (tl, tr, br, bl) = Corners(s, right, up);

        if (!Svc.GameGui.WorldToScreen(tl, out var p1)) return;
        if (!Svc.GameGui.WorldToScreen(tr, out var p2)) return;
        if (!Svc.GameGui.WorldToScreen(br, out var p3)) return;
        if (!Svc.GameGui.WorldToScreen(bl, out var p4)) return;

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

        // Edit visuals (gold highlight) only while editing; pure video when just watching.
        var selected = EditMode && s.Id == SelectedId;
        if (EditMode)
        {
            var border = selected ? new Vector4(1f, 0.85f, 0.3f, 1f) : new Vector4(0.4f, 0.7f, 1f, 1f);
            dl.AddQuad(p1, p2, p3, p4, ImGui.GetColorU32(border), selected ? 3.5f : 2f);
        }
    }

    /// <summary>
    /// Move/resize gizmo for the selected screen (only while Edit mode is on — easily hidden).
    /// Deliberately stays a flat overlay drawn on top of everything: you have to be able to grab a
    /// handle belonging to a screen that sits behind geometry.
    /// </summary>
    private void DrawGizmo(uint territory)
    {
        if (!EditMode || SelectedId == null) return;
        if (!_screens.TryGetValue(SelectedId, out var sel) || sel.TerritoryId != territory) return;

        if (_gizmo.Draw(sel))
        {
            sel.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var now = Environment.TickCount64;
            if (now - _lastPush >= 50) { _lastPush = now; PushScreenUpdate(sel); }
        }
        if (_wasGizmoActive && !_gizmo.Active) PushScreenUpdate(sel); // final sync on release
        _wasGizmoActive = _gizmo.Active;
    }

    // ---- Plumbing ------------------------------------------------------------------

    private readonly WorldRenderer _world = new();

    private void OnCommand(string command, string args)
    {
        var a = args.Trim().ToLowerInvariant();
        if (a == "edit") { EditMode = !EditMode; return; }
        if (a == "gpu") { _world.LogFoundation(); Svc.Log.Info($"CineSync: occlusion is {_occlusion.Describe()}."); return; }
        // Escape hatch: drop to the flat overlay instantly, mid-movie, without a reload.
        if (a == "flat")
        {
            Config.DepthOcclusion = !Config.DepthOcclusion;
            SaveConfig();
            StatusLine = Config.DepthOcclusion
                ? "Depth occlusion ON — screens are hidden behind characters and walls."
                : "Depth occlusion OFF — flat overlay (screens draw over everything).";
            Svc.Log.Info($"[CineSync] {StatusLine}");
            return;
        }
        if (a == "mute")
        {
            SetVolume(Config.Volume, !Config.Muted);
            SaveConfig();
            StatusLine = Config.Muted ? "Muted." : $"Unmuted (volume {Config.Volume}%).";
            Svc.Log.Info($"[CineSync] {StatusLine}");
            return;
        }
        // "volume 40" sets it, "volume +10" / "volume -10" nudge it (handy on a macro). Either unmutes.
        if (a.StartsWith("volume", StringComparison.Ordinal))
        {
            var arg = a["volume".Length..].Trim().TrimEnd('%');
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                StatusLine = "Usage: /cinesync volume 0-100, or +N / -N to nudge it.";
                _configWindow.IsOpen = true;    // the status line lives there
                return;
            }
            SetVolume(arg[0] is '+' or '-' ? Config.Volume + n : n, muted: false);
            SaveConfig();
            StatusLine = $"Volume {Config.Volume}%.";
            Svc.Log.Info($"[CineSync] {StatusLine}");
            return;
        }
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
        _occlusion.Dispose();
        SaveConfig();
    }
}
