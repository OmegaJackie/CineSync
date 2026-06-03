using System.Numerics;
using CineSync.Shared;
using Dalamud.Bindings.ImGui;

namespace CineSync.Plugin;

/// <summary>
/// A lightweight, self-contained move/resize gizmo drawn in world space via WorldToScreen.
/// Three colored axis handles (X/Y/Z) move the screen; two handles (cyan/yellow) resize
/// width/height. No camera matrices required, so it works without engine projection access.
/// </summary>
public sealed class Gizmo
{
    private enum Handle { None, AxisX, AxisY, AxisZ, Width, Height, RotYaw, RotPitch, RotRoll }

    private const float AxisLen = 1.0f;     // world units of each axis handle
    private const float HandleRadius = 6f;  // px
    private const float HitRadius = 12f;    // px grab tolerance

    private Handle _active = Handle.None;
    private Vector2 _lastMouse;

    /// <summary>True while the user is actively dragging a handle.</summary>
    public bool Active => _active != Handle.None;

    private static float Dist(Vector2 a, Vector2 b) => (a - b).Length();

    /// <returns>true if the screen's transform changed this frame.</returns>
    public bool Draw(ScreenDto s)
    {
        var dl = ImGui.GetForegroundDrawList();
        var mouse = ImGui.GetMousePos();
        var delta = mouse - _lastMouse;
        _lastMouse = mouse;

        var down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (!down) _active = Handle.None;

        var c = new Vector3(s.X, s.Y, s.Z);
        if (!Svc.GameGui.WorldToScreen(c, out var cs)) return false;

        var (right, up) = ScreenGeom.Basis(s);
        var defs = new (Handle h, Vector3 dir, Vector3 tip, Vector4 col, bool isSize)[]
        {
            // Move along WORLD axes (intuitive placement)
            (Handle.AxisX, new Vector3(1, 0, 0), c + new Vector3(1, 0, 0) * AxisLen, new Vector4(0.90f, 0.22f, 0.22f, 1f), false),
            (Handle.AxisY, new Vector3(0, 1, 0), c + new Vector3(0, 1, 0) * AxisLen, new Vector4(0.30f, 0.85f, 0.30f, 1f), false),
            (Handle.AxisZ, new Vector3(0, 0, 1), c + new Vector3(0, 0, 1) * AxisLen, new Vector4(0.35f, 0.55f, 1.0f, 1f), false),
            // Resize along the screen's LOCAL right/up (so it stays a rectangle when rotated)
            (Handle.Width,  right, c + right * (s.Width * 0.5f),  new Vector4(0.20f, 0.90f, 0.90f, 1f), true),
            (Handle.Height, up,    c + up * (s.Height * 0.5f),    new Vector4(0.95f, 0.85f, 0.20f, 1f), true),
        };

        dl.AddCircleFilled(cs, 4f, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.9f)));

        var changed = false;
        var capture = _active != Handle.None;   // capture mouse so clicks don't leak to the game
        foreach (var d in defs)
        {
            if (!Svc.GameGui.WorldToScreen(d.tip, out var ts)) continue;
            var col = ImGui.GetColorU32(d.col);
            var hot = _active == d.h || Dist(mouse, ts) < HitRadius;
            capture |= hot;

            dl.AddLine(cs, ts, col, hot ? 4f : 2.5f);
            dl.AddCircleFilled(ts, hot ? HandleRadius + 2f : HandleRadius, col);

            if (_active == Handle.None && clicked && Dist(mouse, ts) < HitRadius)
                _active = d.h;

            if (_active == d.h && down)
            {
                var screenDir = ts - cs;
                var len = screenDir.Length();
                if (len > 0.5f)
                {
                    var px = Vector2.Dot(delta, screenDir / len);   // mouse pixels along this handle
                    if (!d.isSize)
                    {
                        var world = px * (AxisLen / len);
                        s.X += d.dir.X * world;
                        s.Y += d.dir.Y * world;
                        s.Z += d.dir.Z * world;
                    }
                    else
                    {
                        var half = d.h == Handle.Width ? s.Width * 0.5f : s.Height * 0.5f;
                        var perPx = half <= 0.001f ? 0f : half / len;
                        var full = MathF.Max(0.3f, (half + px * perPx) * 2f);
                        if (d.h == Handle.Width) s.Width = full; else s.Height = full;
                    }
                    changed = true;
                }
            }
        }

        // ---- Rotation handles (screen-space rings near the center) ----
        // Yaw = horizontal drag, Pitch = vertical drag, Roll = horizontal drag.
        var rot = new (Handle h, Vector2 off, Vector4 col)[]
        {
            (Handle.RotYaw,   new Vector2(0f, -58f),  new Vector4(0.30f, 0.85f, 0.30f, 1f)),
            (Handle.RotPitch, new Vector2(-48f, -40f), new Vector4(0.90f, 0.22f, 0.22f, 1f)),
            (Handle.RotRoll,  new Vector2(48f, -40f),  new Vector4(0.35f, 0.55f, 1.0f, 1f)),
        };
        foreach (var r in rot)
        {
            var pos = cs + r.off;
            var col = ImGui.GetColorU32(r.col);
            var hot = _active == r.h || Dist(mouse, pos) < HitRadius;
            capture |= hot;
            dl.AddLine(cs, pos, ImGui.GetColorU32(new Vector4(r.col.X, r.col.Y, r.col.Z, 0.35f)), 1.5f);
            dl.AddCircle(pos, hot ? 10f : 8f, col, 16, hot ? 3.5f : 2.5f);

            if (_active == Handle.None && clicked && Dist(mouse, pos) < HitRadius)
                _active = r.h;

            if (_active == r.h && down)
            {
                var amt = (r.h == Handle.RotPitch ? delta.Y : delta.X) * 0.012f;
                if (r.h == Handle.RotYaw) s.Yaw += amt;
                else if (r.h == Handle.RotPitch) s.Pitch += amt;
                else s.Roll += amt;
                changed = true;
            }
        }

        // While hovering/dragging a handle, take the mouse so clicks don't reach the game
        // (which otherwise plays UI/cursor sounds and may target things behind the screen).
        if (capture) ImGui.SetNextFrameWantCaptureMouse(true);

        return changed;
    }
}
