using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace CineSync.Plugin;

/// <summary>
/// Diagnostics for the depth-occluded renderer. Trigger with "/cinesync gpu"; output goes to /xllog.
/// Read-only pointer inspection — no D3D11 calls, no hooks, so it cannot crash the game.
///
/// The surface that matters is <c>RenderTargetManager.DepthStencil</c>: the unscaled scene
/// reverse-Z depth buffer that <see cref="OccludedScreenRenderer"/> compares against. Note this is
/// NOT <c>SwapChain.DepthStencil</c> (which this used to log) — that is a different surface and
/// tells you nothing about whether world occlusion will work.
/// </summary>
public sealed unsafe class WorldRenderer
{
    public void LogFoundation()
    {
        var dev = Device.Instance();
        if (dev == null) { Svc.Log.Warning("CineSync GPU: Device.Instance() returned null."); return; }

        Svc.Log.Info($"CineSync GPU: device=0x{(nint)dev:X}  d3d11Forwarder=0x{(nint)dev->D3D11Forwarder:X}  "
                   + $"context=0x{(nint)dev->D3D11DeviceContext:X}  size={dev->Width}x{dev->Height}");

        var sc = dev->SwapChain;
        if (sc != null)
            Svc.Log.Info($"CineSync GPU: swapChain=0x{(nint)sc:X}  backBuffer=0x{(nint)sc->BackBuffer:X}  "
                       + $"dxgiSwapChain=0x{(nint)sc->DXGISwapChain:X}  {sc->Width}x{sc->Height}");

        // ---- The scene depth buffer: this is what occlusion actually needs. ----
        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        if (rtm == null) { Svc.Log.Warning("CineSync GPU: RenderTargetManager.Instance() is null — occlusion cannot work."); return; }

        var depth = rtm->DepthStencil;
        if (depth == null) { Svc.Log.Warning("CineSync GPU: RenderTargetManager->DepthStencil is null — occlusion cannot work."); return; }

        Svc.Log.Info($"CineSync GPU: sceneDepth=0x{(nint)depth:X}  format={depth->TextureFormat}  "
                   + $"actual={depth->ActualWidth}x{depth->ActualHeight}  allocated={depth->AllocatedWidth}x{depth->AllocatedHeight}  "
                   + $"tex2D=0x{(nint)depth->D3D11Texture2D:X}  srv=0x{(nint)depth->D3D11ShaderResourceView:X}");

        if (depth->ActualWidth != depth->AllocatedWidth || depth->ActualHeight != depth->AllocatedHeight)
            Svc.Log.Info("CineSync GPU: 3D resolution scaling is active — check that occlusion still lines up.");

        var ok = dev->D3D11Forwarder != null && dev->D3D11DeviceContext != null && depth->D3D11Texture2D != null;
        Svc.Log.Info(ok
            ? "CineSync GPU: scene depth is present — depth occlusion has what it needs."
            : "CineSync GPU: some pointers are null — depth occlusion will fall back to the flat overlay.");
    }
}
