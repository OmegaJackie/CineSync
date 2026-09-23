using System.Runtime.InteropServices;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using LibVLCSharp.Shared;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using TerraFX.Interop.DirectX;

namespace CineSync.Plugin;

/// <summary>
/// One video player for one screen. LibVLC decodes into an unmanaged BGRA buffer (vmem output);
/// each finished frame is copied to a managed buffer and, on the render thread, uploaded to a
/// Dalamud texture for drawing on the screen quad.
/// </summary>
public sealed unsafe class MediaScreen : IDisposable
{
    public const int W = 1280;
    public const int H = 720;

    private readonly LibVLC _vlc;
    private readonly MediaPlayer _mp;
    private readonly IntPtr _native;
    private readonly byte[] _pixels = new byte[W * H * 4];
    private readonly object _sync = new();
    private bool _dirty;
    private IDalamudTextureWrap? _tex;
    /// <summary>Set if the in-place upload path failed; we then fall back to a texture per frame.</summary>
    private bool _noDynamicTexture;
    /// <summary>A CreateEmpty texture holds undefined pixels until written — don't show it before then.</summary>
    private bool _uploaded;

    // Keep the delegates rooted so the GC can't collect them while native code holds them.
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    public string Url { get; private set; } = "";

    public MediaScreen(LibVLC vlc, string url)
    {
        _vlc = vlc;
        _native = Marshal.AllocHGlobal(W * H * 4);
        // Hardware decoding + vmem callbacks => GPU frames never reach our CPU buffer (green screen
        // on many GPUs). Force software decoding so the vmem path always gets real pixels.
        _mp = new MediaPlayer(_vlc) { EnableHardwareDecoding = false };

        _lockCb = Lock;
        _unlockCb = Unlock;
        _displayCb = Display;
        _mp.SetVideoFormat("RV32", W, H, W * 4);          // RV32 == BGRA
        _mp.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);

        SetUrl(url);
    }

    public void SetUrl(string url)
    {
        Url = url;
        if (string.IsNullOrWhiteSpace(url)) { try { _mp.Stop(); } catch { } return; }
        try
        {
            using var media = new Media(_vlc, new Uri(url));
            _mp.Play(media);
        }
        catch (Exception ex) { Svc.Log.Error(ex, "CineSync: failed to play " + url); }
    }

    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _native);
        return _native;
    }

    private void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        lock (_sync)
        {
            Marshal.Copy(_native, _pixels, 0, _pixels.Length);
            _dirty = true;
        }
    }

    private void Display(IntPtr opaque, IntPtr picture) { }

    /// <summary>
    /// Render-thread only: refresh the texture if a new frame arrived, return it.
    ///
    /// One long-lived D3D11_USAGE_DYNAMIC texture is written in place per frame. Creating a fresh
    /// texture per frame instead would be actively harmful now that the screen is drawn through the
    /// depth-occluded path: Pictomancy AddRefs each distinct shader-resource-view pointer it is
    /// handed and only evicts it after 120 frames, so a new texture per video frame would pin
    /// roughly two seconds of decoded 1280x720 frames (hundreds of MB) in VRAM per screen.
    /// A stable pointer collapses that cache to a single entry.
    /// </summary>
    public IDalamudTextureWrap? UpdateAndGet(ITextureProvider tp)
    {
        if (!_noDynamicTexture)
        {
            try
            {
                _tex ??= tp.CreateEmpty(RawImageSpecification.Bgra32(W, H), false, true, "CineSync.Screen");
                if (_dirty)
                {
                    lock (_sync)
                    {
                        Upload(_tex);
                        _dirty = false;
                        _uploaded = true;
                    }
                }
                // Until the first frame lands the texture holds undefined pixels, so keep showing
                // the caller's placeholder rather than a rectangle of garbage.
                return _uploaded ? _tex : null;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "CineSync: in-place video upload failed; falling back to a texture per frame.");
                _noDynamicTexture = true;
                _uploaded = false;
                _tex?.Dispose();
                _tex = null;
            }
        }

        // Fallback: a fresh immutable texture per frame. Correct, but allocates ~3.5 MB per frame
        // and pins VRAM while the depth-occluded renderer is in use.
        if (_dirty)
        {
            lock (_sync)
            {
                var old = _tex;
                _tex = tp.CreateFromRaw(RawImageSpecification.Bgra32(W, H), _pixels, "CineSync.Screen");
                old?.Dispose();
                _dirty = false;
            }
        }
        return _tex;
    }

    /// <summary>
    /// Copy the latest decoded frame into the dynamic texture. Render thread only, and the caller
    /// must hold <see cref="_sync"/> so LibVLC can't rewrite <see cref="_pixels"/> mid-copy.
    /// </summary>
    private void Upload(IDalamudTextureWrap tex)
    {
        var dev = Device.Instance();
        if (dev == null || dev->D3D11DeviceContext == null)
            throw new InvalidOperationException("no D3D11 device context");

        var ctx = (ID3D11DeviceContext*)dev->D3D11DeviceContext;
        var srv = (ID3D11ShaderResourceView*)(nint)tex.Handle.Handle;
        if (srv == null) throw new InvalidOperationException("texture has no shader resource view");

        ID3D11Resource* res = null;
        srv->GetResource(&res);
        if (res == null) throw new InvalidOperationException("shader resource view has no resource");

        try
        {
            D3D11_MAPPED_SUBRESOURCE mapped;
            var hr = ctx->Map(res, 0u, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0u, &mapped);
            if (hr.FAILED) throw new InvalidOperationException($"Map failed (0x{(uint)hr.Value:X8})");

            try
            {
                fixed (byte* src = _pixels)
                {
                    var dst = (byte*)mapped.pData;
                    const int srcPitch = W * 4;
                    // The driver picks the destination pitch; it is often wider than our rows.
                    if (mapped.RowPitch == srcPitch)
                    {
                        Buffer.MemoryCopy(src, dst, (long)srcPitch * H, (long)srcPitch * H);
                    }
                    else
                    {
                        for (var y = 0; y < H; y++)
                            Buffer.MemoryCopy(src + (y * srcPitch), dst + ((long)y * mapped.RowPitch),
                                              mapped.RowPitch, srcPitch);
                    }
                }
            }
            finally { ctx->Unmap(res, 0u); }
        }
        finally { res->Release(); }
    }

    public void SetPaused(bool paused)
    {
        try
        {
            if (paused) { if (_mp.IsPlaying) _mp.SetPause(true); }
            else { if (!_mp.IsPlaying) _mp.Play(); }
        }
        catch { }
    }

    public void Seek(double seconds)
    {
        try { _mp.Time = (long)(seconds * 1000); } catch { }
    }

    public void Dispose()
    {
        try { _mp.Stop(); } catch { }
        try { _mp.Dispose(); } catch { }
        _tex?.Dispose();
        if (_native != IntPtr.Zero) Marshal.FreeHGlobal(_native);
    }
}
