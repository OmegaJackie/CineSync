using Pictomancy;

namespace CineSync.Plugin;

/// <summary>
/// M5 — depth-correct rendering.
///
/// The original renderer drew each screen with <c>ImGui.GetBackgroundDrawList().AddImageQuad(...)</c>.
/// Dalamud composites ImGui from its <c>IDXGISwapChain::Present</c> detour — after the game has
/// finished the 3D scene AND its own HUD — binding a NULL depth-stencil view with depth testing
/// off, and <c>ImDrawVert</c> is (float2 pos, float2 uv, rgba8) so it carries no depth at all.
/// A screen therefore painted over everything: your character, other players, walls, props.
///
/// This draws the same quad as real world-space geometry instead, depth-tested per pixel against
/// the game's scene depth buffer, so anything actually in front of a screen occludes it.
///
/// The heavy lifting is Pictomancy's: it stands up its own renderer on the game's D3D11 device,
/// records onto a *deferred* context and replays with <c>ExecuteCommandList(restoreContextState: true)</c>,
/// so it cannot corrupt Dalamud's ImGui state. It never binds a DSV over the game depth — it wraps
/// the depth texture in an R24_UNORM_X8_TYPELESS SRV and compares in the pixel shader, which
/// sidesteps read-only-DSV flags and MSAA mismatches.
///
/// Failure is always survivable: if anything here breaks, <see cref="Ready"/> goes false and
/// <see cref="Plugin"/> falls straight back to the old flat overlay.
/// </summary>
public sealed class OccludedScreenRenderer : IDisposable
{
    public enum RState
    {
        /// <summary>Not up yet — still inside the bounded retry window.</summary>
        Initialising,
        /// <summary>Drawing with depth occlusion.</summary>
        Ready,
        /// <summary>Init never succeeded; permanently on the flat overlay for this session.</summary>
        GaveUp,
        /// <summary>Came up, then failed mid-frame; latched off so we can't crash-loop mid-movie.</summary>
        Disabled,
    }

    /// <summary>
    /// Init legitimately fails at character select and mid zone-load, so retry — but bounded, so a
    /// genuinely broken setup doesn't retry forever. Draw runs per frame, so this is ~half a second.
    /// </summary>
    private const int MaxInitAttempts = 30;

    private int _attempts;
    private bool _pctAlive;

    public RState State { get; private set; } = RState.Initialising;
    public string LastError { get; private set; } = "";
    public bool Ready => State == RState.Ready;

    /// <summary>Draw-thread: bring Pictomancy up, retrying a bounded number of frames.</summary>
    public void EnsureInitialised()
    {
        if (State != RState.Initialising) return;

        _attempts++;
        try
        {
            PctService.Initialize(Svc.PluginInterface, new PctOptions
            {
                EnableDxRenderer = true,
                // The VFX renderer is the part that hooks and sig-scans. We only need the DX path,
                // so leaving it off means zero hooks and zero signature scans => no patch-day break.
                EnableVfxRenderer = false,
                EnableKtkOutput = false,
                MaxImages = 32,
            });
            _pctAlive = true;
            State = RState.Ready;
            Svc.Log.Info($"CineSync: depth occlusion ready (attempt {_attempts}).");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            // Don't leave a half-built service behind — Initialize throws on a second call, so a
            // failed attempt must be torn down or every later retry fails as "already initialized".
            try { PctService.Dispose(); } catch { }
            _pctAlive = false;

            if (_attempts >= MaxInitAttempts)
            {
                State = RState.GaveUp;
                Svc.Log.Warning($"CineSync: depth occlusion unavailable after {_attempts} attempts " +
                                $"({ex.Message}). Using the flat overlay instead.");
            }
        }
    }

    /// <summary>
    /// Draw-thread: run <paramref name="body"/> against a depth-occluded draw list.
    /// Returns false if occlusion isn't available or the frame failed — caller draws flat instead.
    /// </summary>
    public bool TryDraw(float occlusionTolerance, Action<PctDrawList> body)
    {
        if (State != RState.Ready) return false;

        PctDrawList? dl = null;
        try
        {
            dl = PctService.Draw(null, new PctDrawHints
            {
                // Every Add* call inherits these unless it passes its own params. Left at the
                // library's own defaults otherwise (MaxAlpha 255, AlphaBlendMode.Add, UIMask.Default) —
                // Add is what lets the edit border blend over the video instead of punching
                // translucent nicks through it.
                DefaultParams = new PctDxParams
                {
                    OccludedAlpha = 0f,                     // hard cut: hidden where the scene is nearer
                    OcclusionTolerance = occlusionTolerance,
                },
            });
            if (dl is null) return false;
            body(dl);
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex, "world draw");
            return false;
        }
        finally
        {
            // Dispose is what actually executes the deferred draw, so it can fail on its own.
            try { dl?.Dispose(); }
            catch (Exception ex) { Fail(ex, "frame flush"); }
        }
    }

    /// <summary>Latch occlusion off after a mid-frame failure — never retry it every frame.</summary>
    private void Fail(Exception ex, string where)
    {
        if (State == RState.Disabled) return;
        LastError = ex.Message;
        State = RState.Disabled;
        Svc.Log.Error(ex, $"CineSync: depth occlusion failed during {where}; reverting to the flat overlay.");
    }

    public string Describe() => State switch
    {
        RState.Ready => "on (depth-occluded)",
        RState.Initialising => $"starting (attempt {_attempts}/{MaxInitAttempts})",
        RState.GaveUp => $"unavailable — flat overlay ({LastError})",
        RState.Disabled => $"disabled after a failure — flat overlay ({LastError})",
        _ => "unknown",
    };

    public void Dispose()
    {
        if (!_pctAlive) return;
        _pctAlive = false;
        // Load-bearing: Initialize throws if a previous context is still alive, so skipping this
        // would pin the plugin to the flat overlay after any reload.
        try { PctService.Dispose(); }
        catch (Exception ex) { Svc.Log.Warning($"CineSync: PctService.Dispose failed: {ex.Message}"); }
    }
}
