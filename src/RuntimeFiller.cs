using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>
/// Donne une durée aux versions jamais analysées (on évite d'analyser chacune : un torrent par version).
/// Sans durée, le lecteur affiche une barre de progression fausse (quelques secondes).
/// Source : une autre version déjà analysée du même titre, sinon la durée TMDB.
/// </summary>
public sealed class RuntimeFiller(ILibraryManager library, Clients clients, ILogger<RuntimeFiller> log) : IHostedService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private readonly ConcurrentDictionary<string, long> _tmdb = new();
    private CancellationTokenSource? _stop;
    private int _done;

    public Task StartAsync(CancellationToken ct)
    {
        _stop = new CancellationTokenSource();
        library.ItemAdded += OnItem;
        library.ItemUpdated += OnItem;
        _ = Task.Run(() => Loop(_stop.Token));
        // rattrapage des versions déjà créées
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
              while (true) {
                var n = 0;
                foreach (var v in library.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                         { IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode, Jellyfin.Data.Enums.BaseItemKind.Movie], Recursive = true }))
                {
                    if (!(v.Path ?? "").StartsWith(LibraryService.Root, StringComparison.Ordinal)) continue;
                    if (Needs(v)) { Enqueue(v.Id); n++; }
                    // les versions alternatives (01, 02…) ne sont pas listées : on passe par la principale
                    if (v is Video main)
                        foreach (var alt in main.GetAllVersions())
                            if (alt.Id != v.Id && library.GetItemById(alt.Id) is { } a2 && Needs(a2)) { Enqueue(a2.Id); n++; }
                }
                if (n > 0) log.LogInformation("Durées : {N} versions sans durée à compléter", n);
                await Task.Delay(TimeSpan.FromMinutes(3), _stop.Token);
              }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Durées (rattrapage) : {M}", ex.Message); }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        library.ItemAdded -= OnItem;
        library.ItemUpdated -= OnItem;
        _stop?.Cancel();
        return Task.CompletedTask;
    }

    private static bool Needs(BaseItem i) =>
        i is Video && (i.RunTimeTicks ?? 0) <= 0 && (i.Path ?? "").StartsWith(LibraryService.Root, StringComparison.Ordinal) && i.Path!.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    private void OnItem(object? sender, ItemChangeEventArgs e)
    {
        if (Needs(e.Item)) Enqueue(e.Item.Id);
    }

    private readonly ConcurrentDictionary<Guid, DateTime> _tried = new();

    private void Enqueue(Guid id)
    {
        if (_tried.TryGetValue(id, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(2)) return;   // pas de boucle
        if (_pending.TryAdd(id, 0)) _queue.Writer.TryWrite(id);
    }

    private async Task Loop(CancellationToken ct)
    {
        while (await _queue.Reader.WaitToReadAsync(ct))
        {
            // petit délai : laisser Jellyfin finir d'enregistrer l'élément (évite les conflits en base)
            await Task.Delay(2000, ct);
            while (_queue.Reader.TryRead(out var id))
            {
                _pending.TryRemove(id, out _);
                try { await Fill(id, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log.LogWarning("Durée de {Id} : {M}", id, ex.Message); }
            }
        }
    }

    private async Task Fill(Guid id, CancellationToken ct)
    {
        _tried[id] = DateTime.UtcNow;
        if (library.GetItemById(id) is not Video item || !Needs(item)) return;
        var ticks = Sibling(item) ?? await FromTmdb(item, ct);
        if (ticks is not > 0) { log.LogDebug("Durée inconnue pour {P}", item.Path); return; }
        item = library.GetItemById(id) as Video ?? item;
        if (!Needs(item)) return;
        item.RunTimeTicks = ticks;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
        if (Interlocked.Increment(ref _done) % 200 == 1) log.LogInformation("Durées : {N} versions complétées", _done);
    }

    /// <summary>Durée d'une autre version du même titre (version principale, ou ses versions alternatives).</summary>
    private long? Sibling(Video item)
    {
        var primary = item.PrimaryVersionId is { } pid && pid != Guid.Empty ? library.GetItemById(pid) as Video : item;
        if (primary is null) return null;
        if (primary.Id != item.Id && primary.RunTimeTicks is > 0) return primary.RunTimeTicks;
        foreach (var v in primary.GetAllVersions())
            if (v.Id != item.Id && library.GetItemById(v.Id) is Video other && other.RunTimeTicks is > 0) return other.RunTimeTicks;
        return null;
    }

    /// <summary>Durée annoncée par TMDB (minutes).</summary>
    private async Task<long?> FromTmdb(Video item, CancellationToken ct)
    {
        var tm = Regex.Match(item.Path ?? "", @"\[tmdbid-(\d+)\]");
        if (!tm.Success) return null;
        var id = tm.Groups[1].Value;
        if (item is Episode ep && ep.ParentIndexNumber is int s && ep.IndexNumber is int e)
        {
            var key = $"tv:{id}:{s}:{e}";
            if (!_tmdb.ContainsKey(key))
            {
                var j = await clients.Tmdb($"tv/{id}/season/{s}", ct);
                if (j.TryGetProperty("episodes", out var eps))
                    foreach (var x in eps.EnumerateArray())
                        _tmdb[$"tv:{id}:{s}:{Clients.Int(x, "episode_number")}"] = Clients.Int(x, "runtime") * 600000000L;
            }
            return _tmdb.TryGetValue(key, out var t) && t > 0 ? t : null;
        }
        if (item is MediaBrowser.Controller.Entities.Movies.Movie)
        {
            var key = "movie:" + id;
            if (!_tmdb.TryGetValue(key, out var t))
                _tmdb[key] = t = Clients.Int(await clients.Tmdb("movie/" + id, ct), "runtime") * 600000000L;
            return t > 0 ? t : null;
        }
        return null;
    }
}
