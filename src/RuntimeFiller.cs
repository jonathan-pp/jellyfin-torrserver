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
public sealed class RuntimeFiller(ILibraryManager library, Clients clients, ProbeService probe, ILogger<RuntimeFiller> log) : IHostedService
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
        probe.Analyzed += OnAnalyzed;
        _ = Task.Run(() => Loop(_stop.Token));
        // rattrapage : au démarrage, puis de temps en temps (parcours coûteux : une requête par titre)
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
                await Task.Delay(TimeSpan.FromHours(6), _stop.Token);
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
        probe.Analyzed -= OnAnalyzed;
        _stop?.Cancel();
        return Task.CompletedTask;
    }

    private static bool Needs(BaseItem i) =>
        i is Video && (i.RunTimeTicks ?? 0) <= 0 && (i.Path ?? "").StartsWith(LibraryService.Root, StringComparison.Ordinal) && i.Path!.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    private void OnItem(object? sender, ItemChangeEventArgs e)
    {
        // durée écrasée par une actualisation de Jellyfin : corrigée tout de suite en mémoire (le lecteur la lit juste après), puis enregistrée
        if (probe.Restore(e.Item))
        {
            if (library.GetItemById(e.Item.Id) is { } cached && !ReferenceEquals(cached, e.Item)) probe.Restore(cached);
            if (_pending.TryAdd(e.Item.Id, 0)) _queue.Writer.TryWrite(e.Item.Id);
            return;
        }
        if (Needs(e.Item)) Enqueue(e.Item.Id);
    }

    /// <summary>Une version vient d'être analysée : ses sœurs sans durée peuvent reprendre la sienne.</summary>
    private void OnAnalyzed(Guid id)
    {
        try
        {
            if (library.GetItemById(id) is not Video v) return;
            var primary = v.PrimaryVersionId is { } pid && pid != Guid.Empty ? library.GetItemById(pid) as Video : v;
            foreach (var x in primary?.GetAllVersions() ?? [])
                if (library.GetItemById(x.Id) is { } sibling && Needs(sibling)) { _tried.TryRemove(sibling.Id, out _); Enqueue(sibling.Id); }
        }
        catch (Exception ex) { log.LogDebug("Durées après analyse : {M}", ex.Message); }
    }

    private readonly ConcurrentDictionary<Guid, DateTime> _tried = new();

    private void Enqueue(Guid id)
    {
        if (_tried.TryGetValue(id, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(30)) return;   // pas de boucle
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
        if (library.GetItemById(id) is not Video item) return;
        if (probe.RealTicks(id) is { } real)
        {
            item.RunTimeTicks = real;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
            return;
        }
        _tried[id] = DateTime.UtcNow;
        if (!Needs(item)) return;
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
                var map = int.TryParse(id, out var tid) ? await clients.SeasonMap(tid, ct) : null;
                if (map is not null && map.TryGetValue(s, out var mapped))
                    for (var i = 0; i < mapped.Count; i++) _tmdb[$"tv:{id}:{s}:{i + 1}"] = Clients.Int(mapped[i], "runtime") * 600000000L;
                else if ((await clients.Tmdb($"tv/{id}/season/{s}", ct)) is var j && j.TryGetProperty("episodes", out var eps))
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
