using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>
/// Analyse (ffprobe) d'UNE version précise, faite par le plugin lui-même.
/// Si on laissait Jellyfin analyser un film, il analyserait aussi toutes ses versions (un torrent chacune).
/// </summary>
public sealed class ProbeService(ILibraryManager library, IMediaEncoder encoder, IMediaStreamRepository streams, LibraryService lib, ILogger<ProbeService> log)
{
    private readonly ConcurrentDictionary<string, (DateTime At, Task Task)> _recent = new();
    private readonly SemaphoreSlim _gate = new(2);

    /// <summary>Une version vient d'être analysée (sa durée réelle est connue).</summary>
    public event Action<Guid>? Analyzed;

    // Durées mesurées sur le fichier réel. Jellyfin actualise chaque .strm à la lecture et remplace la durée par celle de TMDB,
    // souvent différente du fichier : le lecteur attendrait alors une fin qui n'arrive jamais. On les garde donc à part.
    private readonly ConcurrentDictionary<Guid, long> _real = new();
    private bool _realLoaded;
    private int _savePending;
    private static string RealFile => Path.Combine(Plugin.Instance!.DataFolderPath, "durations.json");

    /// <summary>Durée mesurée d'une version, si elle a été analysée.</summary>
    public long? RealTicks(Guid id)
    {
        LoadReal();
        return _real.TryGetValue(id, out var t) ? t : null;
    }

    /// <summary>Rétablit la durée mesurée si elle a été écrasée ; vrai si l'élément a été corrigé (à enregistrer).</summary>
    public bool Restore(BaseItem item)
    {
        if (RealTicks(item.Id) is not { } real || item.RunTimeTicks == real) return false;
        item.RunTimeTicks = real;
        return true;
    }

    /// <summary>La source de cette version vient de changer : sa durée sera mesurée à nouveau.</summary>
    public void Forget(string path)
    {
        _recent.TryRemove(path, out _);
        if (lib.FindByStrm(path) is { } item && _real.TryRemove(item.Id, out _)) SaveReal();
    }

    private void LoadReal()
    {
        if (_realLoaded) return;
        lock (_real)
        {
            if (_realLoaded) return;
            try
            {
                if (File.Exists(RealFile))
                    foreach (var kv in System.Text.Json.JsonSerializer.Deserialize<Dictionary<Guid, long>>(File.ReadAllText(RealFile)) ?? []) _real[kv.Key] = kv.Value;
            }
            catch { /* elles seront mesurées à nouveau */ }
            _realLoaded = true;
        }
    }

    private void SaveReal()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref _savePending, 0);
            try
            {
                File.WriteAllText(RealFile + ".tmp", System.Text.Json.JsonSerializer.Serialize(_real.ToDictionary(x => x.Key, x => x.Value)));
                File.Move(RealFile + ".tmp", RealFile, true);
            }
            catch { /* best effort */ }
        });
    }

    /// <summary>Analyse en arrière-plan l'élément de ce fichier .strm s'il ne l'a jamais été.</summary>
    public Task ProbePath(string path)
    {
        var now = DateTime.UtcNow;
        foreach (var old in _recent.Where(x => now - x.Value.At > TimeSpan.FromMinutes(10) && x.Value.Task.IsCompleted).Select(x => x.Key).ToList()) _recent.TryRemove(old, out _);
        if (_recent.TryGetValue(path, out var cur)) return cur.Task;
        var t = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { await Probe(path); }
            catch (Exception ex) { _recent.TryRemove(path, out _); log.LogWarning("Analyse de {P} : {M}", Path.GetFileName(path), ex.Message); }
            finally { _gate.Release(); }
        });
        _recent[path] = (now, t);
        return t;
    }

    private async Task Probe(string path)
    {
        BaseItem? item = null;
        for (var i = 0; i < 20 && item is null; i++) { item = lib.FindByStrm(path); if (item is null) await Task.Delay(1500); }
        if (item is null || !File.Exists(path)) return;
        if (RealTicks(item.Id) is not null && streams.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }).Count > 0) return;
        var url = (await File.ReadAllTextAsync(path)).Trim();
        url += (url.Contains('?') ? "&" : "?") + "probe=1";     // analyse voulue : laissée passer
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var info = await encoder.GetMediaInfo(new MediaInfoRequest
        {
            MediaSource = new MediaSourceInfo { Id = item.Id.ToString("N"), Path = url, Protocol = MediaProtocol.Http, IsRemote = true },
            MediaType = DlnaProfileType.Video,
            ExtractChapters = false,
        }, cts.Token);
        if (info.MediaStreams is not { Count: > 0 }) return;
        // on relit l'élément : ses infos TMDB ont pu être mises à jour pendant l'analyse (ne pas les écraser)
        item = library.GetItemById(item.Id) ?? item;
        streams.SaveMediaStreams(item.Id, info.MediaStreams, cts.Token);
        if (info.RunTimeTicks is > 0)   // jamais remplacer une durée par « inconnue »
        {
            item.RunTimeTicks = info.RunTimeTicks;
            _real[item.Id] = info.RunTimeTicks.Value;
            SaveReal();
        }
        item.Size = info.Size;
        item.TotalBitrate = info.Bitrate;
        if (item is Video v) v.Container = info.Container;
        var video = info.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (video is not null) { item.Width = video.Width ?? 0; item.Height = video.Height ?? 0; }
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cts.Token);
        log.LogInformation("Analyse : {N} ({D} min, {S} pistes)", Path.GetFileNameWithoutExtension(path), (info.RunTimeTicks ?? 0) / 600000000, info.MediaStreams.Count);
        Analyzed?.Invoke(item.Id);
    }
}
