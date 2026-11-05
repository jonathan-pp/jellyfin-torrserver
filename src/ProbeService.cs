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
        if ((item.RunTimeTicks ?? 0) > 0 && streams.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }).Count > 0) return;
        var url = (await File.ReadAllTextAsync(path)).Trim();
        url += (url.Contains('?') ? "&" : "?") + "probe=1";     // analyse voulue : laissée passer
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
        item.RunTimeTicks = info.RunTimeTicks;
        item.Size = info.Size;
        item.TotalBitrate = info.Bitrate;
        if (item is Video v) v.Container = info.Container;
        var video = info.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (video is not null) { item.Width = video.Width ?? 0; item.Height = video.Height ?? 0; }
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cts.Token);
        log.LogInformation("Analyse : {N} ({D} min, {S} pistes)", Path.GetFileNameWithoutExtension(path), (info.RunTimeTicks ?? 0) / 600000000, info.MediaStreams.Count);
    }
}
