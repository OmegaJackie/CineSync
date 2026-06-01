using System.Numerics;
using CineSync.Shared;

namespace CineSync.Plugin;

/// <summary>Shared geometry so the renderer and the gizmo agree on a screen's orientation.</summary>
public static class ScreenGeom
{
    /// <summary>Local right/up axes of a screen from its yaw/pitch/roll.</summary>
    public static (Vector3 right, Vector3 up) Basis(ScreenDto s)
    {
        var q = Quaternion.CreateFromYawPitchRoll(s.Yaw, s.Pitch, s.Roll);
        return (Vector3.Transform(Vector3.UnitX, q), Vector3.Transform(Vector3.UnitY, q));
    }
}
