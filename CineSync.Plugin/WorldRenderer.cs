using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace CineSync.Plugin;

/// <summary>
/// M5 (depth-correct 3D rendering) — built in safe checkpoints.
///
/// Checkpoint 1 (this): read and log the GPU foundation pointers from FFXIVClientStructs at
/// runtime — D3D11 device/context, the swap chain, its back buffer, and (critically) the scene
/// DEPTH buffer we'll depth-test against. No D3D11 calls, no render hook => zero crash risk.
/// Trigger with "/cinesync gpu".
///
/// Next checkpoints: acquire view/projection from Render.Camera, hook Present, draw a solid quad,
/// place it in 3D, add the video texture, then depth-test against the game depth buffer.
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
        if (sc == null) { Svc.Log.Warning("CineSync GPU: SwapChain is null."); return; }

        Svc.Log.Info($"CineSync GPU: swapChain=0x{(nint)sc:X}  backBuffer=0x{(nint)sc->BackBuffer:X}  "
                   + $"depthStencil=0x{(nint)sc->DepthStencil:X}  dxgiSwapChain=0x{(nint)sc->DXGISwapChain:X}  {sc->Width}x{sc->Height}");

        var ok = dev->D3D11Forwarder != null && dev->D3D11DeviceContext != null
                 && sc->BackBuffer != null && sc->DepthStencil != null && sc->DXGISwapChain != null;
        Svc.Log.Info(ok
            ? "CineSync GPU: ALL foundation pointers present — safe to proceed to the render hook."
            : "CineSync GPU: some pointers null — will need a different acquisition path.");
    }
}
