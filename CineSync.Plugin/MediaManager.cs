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

    private LibVLC? Vlc()
    {
        if (_vlc != null || _initTried) return _vlc;
        _initTried = true;
        try
        {
            var dir = Svc.PluginInterface.AssemblyLocation.Directory!.FullName;
            Core.Initialize(Path.Combine(dir, "libvlc", "win-x64"));
            _vlc = new LibVLC("--quiet", "--no-osd", "--network-caching=1500");
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

        var ms = _players.GetOrAdd(s.Id, _ => new MediaScreen(vlc, s.MediaUrl));
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
