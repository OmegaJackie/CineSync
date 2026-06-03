using System.Runtime.InteropServices;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using LibVLCSharp.Shared;

namespace CineSync.Plugin;

/// <summary>
/// One video player for one screen. LibVLC decodes into an unmanaged BGRA buffer (vmem output);
/// each finished frame is copied to a managed buffer and, on the render thread, uploaded to a
/// Dalamud texture for drawing on the screen quad.
/// </summary>
public sealed class MediaScreen : IDisposable
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

    /// <summary>Render-thread only: refresh the texture if a new frame arrived, return it.</summary>
    public IDalamudTextureWrap? UpdateAndGet(ITextureProvider tp)
    {
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
