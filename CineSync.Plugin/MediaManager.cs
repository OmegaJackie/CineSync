using System.Collections.Concurrent;
using CineSync.Shared;
using Dalamud.Interface.Textures.TextureWraps;
using LibVLCSharp.Shared;

namespace CineSync.Plugin;

/// <summary>Owns the LibVLC instance and one <see cref="MediaScreen"/> per synced screen.</summary>
public sealed class MediaManager : IDisposable
{
    private LibVLC? _vlc;
    private bool _initTried;
    private readonly ConcurrentDictionary<string, MediaScreen> _players = new();

    // Volume is global, not per screen. LibVLC's Windows output (mmdevice) plays every player
    // through ONE audio session of its own, separate from the game's, and volume and mute belong
    // to that session. Setting them on any player moves them all.
    private int _volume = 100;
    private bool _muted;
    private long _nextVolumeCheck;

    private LibVLC? Vlc()
    {
        if (_vlc != null || _initTried) return _vlc;
        _initTried = true;
        try
        {
            var dir = Svc.PluginInterface.AssemblyLocation.Directory!.FullName;
            Core.Initialize(Path.Combine(dir, "libvlc", "win-x64"));
            _vlc = new LibVLC("--quiet", "--no-osd", "--network-caching=1500", "--avcodec-hw=none");
            Svc.Log.Info("CineSync: LibVLC initialised.");
        }
        catch (Exception ex) { Svc.Log.Error(ex, "CineSync: LibVLC init failed (video disabled)."); }
        return _vlc;
    }

    /// <summary>Render-thread: ensure a player exists for this screen's URL; return its texture.</summary>
    public IDalamudTextureWrap? GetTexture(ScreenDto s)
    {
        if (string.IsNullOrWhiteSpace(s.MediaUrl)) { Remove(s.Id); return null; }
        var vlc = Vlc();
        if (vlc == null) return null;

        var ms = _players.GetOrAdd(s.Id, _ => new MediaScreen(vlc, s.MediaUrl, _volume, _muted));
        if (ms.Url != s.MediaUrl) ms.SetUrl(s.MediaUrl);
        return ms.UpdateAndGet(Svc.TextureProvider);
    }

    public void ApplyPlayback(PlaybackDto p)
    {
        if (_players.TryGetValue(p.ScreenId, out var ms))
        {
            ms.SetPaused(p.Paused);
            if (p.PositionSeconds > 0) ms.Seek(p.PositionSeconds);
        }
    }

    /// <summary>Set the volume (0-100) and mute for every screen, including ones created later.</summary>
    public void SetVolume(int volume, bool muted)
    {
        _volume = Math.Clamp(volume, 0, 100);
        _muted = muted;
        foreach (var ms in _players.Values) ms.SetVolume(_volume, _muted);
    }

    /// <summary>
    /// Draw-thread, every frame: a few times a second, put the volume back if something moved it.
    /// Windows remembers the session's level per output device, and LibVLC re-reads it when the
    /// default device changes, so plugging in a headset mid-movie swaps in whatever level that
    /// device had last (full volume, if it never had one). Changes made in the Windows volume
    /// mixer are undone the same way, which keeps the in-game slider truthful.
    /// </summary>
    public void EnforceVolume()
    {
        var now = Environment.TickCount64;
        if (now < _nextVolumeCheck) return;
        _nextVolumeCheck = now + 250;
        foreach (var ms in _players.Values)
            if (ms.VolumeDrifted(_volume, _muted)) ms.SetVolume(_volume, _muted);
    }

    public void Remove(string id)
    {
        if (_players.TryRemove(id, out var ms)) ms.Dispose();
    }

    /// <summary>Drop players whose screens no longer exist.</summary>
    public void PruneExcept(IEnumerable<string> liveIds)
    {
        var keep = new HashSet<string>(liveIds);
        foreach (var id in _players.Keys)
            if (!keep.Contains(id)) Remove(id);
    }

    public void Dispose()
    {
        foreach (var ms in _players.Values) ms.Dispose();
        _players.Clear();
        try { _vlc?.Dispose(); } catch { }
    }
}
