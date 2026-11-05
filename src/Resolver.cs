using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

public sealed class Entry
{
    public string Hash { get; set; } = "";
    public int FileId { get; set; }
    public string FileName { get; set; } = "";
    public long Length { get; set; }
    public string Release { get; set; } = "";
    public string Link { get; set; } = "";
    /// <summary>Indexeur Prowlarr d'origine (droits par utilisateur).</summary>
    public string Indexer { get; set; } = "";
    /// <summary>Source imposée par l'utilisateur : prioritaire sur le choix automatique.</summary>
    public bool Pinned { get; set; }
    public List<string> Excluded { get; set; } = [];
}

public sealed record Plan(List<string> Titles, int Year, IReadOnlyList<Scored> Ranked, int Total);

/// <summary>Trouve (et mémorise) le torrent et le fichier à lire pour un film ou un épisode.</summary>
public sealed class Resolver(Clients clients, ILogger<Resolver> log)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Entry>>> _inflight = new();
    private readonly SemaphoreSlim _fileGate = new(1);
    private Dictionary<string, Entry>? _cache;

    private static string CacheFile => Path.Combine(Plugin.Instance!.DataFolderPath, "resolved.json");

    private async Task<Dictionary<string, Entry>> Cache()
    {
        if (_cache is not null) return _cache;
        await _fileGate.WaitAsync();
        try
        {
            if (_cache is null)
            {
                try { _cache = File.Exists(CacheFile) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(await File.ReadAllTextAsync(CacheFile)) ?? [] : []; }
                catch { _cache = []; }
            }
            return _cache;
        }
        finally { _fileGate.Release(); }
    }

    private async Task Save()
    {
        await _fileGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            string json; lock (_cache!) json = JsonSerializer.Serialize(_cache);
            var tmp = CacheFile + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, CacheFile, true);
        }
        finally { _fileGate.Release(); }
    }

    private static string PackKey(MediaRef media, string scope) => $"pack:tv:{media.TmdbId}:{media.Season}" + scope;

    public async Task<Entry?> Peek(MediaRef media, string scope = "")
    {
        var c = await Cache();
        lock (c)
        {
            if (c.TryGetValue(media.Key + scope, out var e) && (e.Hash.Length > 0 || e.Pinned)) return e;
            if (media.IsTv && c.TryGetValue(PackKey(media, scope), out var p) && (p.Hash.Length > 0 || p.Pinned)) return p;
            return null;
        }
    }

    /// <summary>Oublie la source mémorisée ; avec ban = true, elle ne sera plus proposée.</summary>
    public async Task Invalidate(string key, bool ban)
    {
        var c = await Cache();
        lock (c)
        {
            if (c.TryGetValue(key, out var e))
            {
                if (ban) { e.Excluded.Add(e.Link); e.Excluded.Add(e.Release); e.Pinned = false; }
                e.Hash = "";
                if (!e.Pinned) e.Link = "";
            }
        }
        await Save();
    }

    /// <summary>Impose une source choisie par l'utilisateur (épisode précis, ou toute la saison pour un pack).</summary>
    public async Task Pin(MediaRef media, Release release, string scope = "")
    {
        var c = await Cache();
        lock (c)
        {
            var fresh = new Entry { Release = release.Title, Link = release.Link, Indexer = release.Indexer, Pinned = true };
            if (!media.IsTv || Ranking.TvFit(release.Title, media.Season, media.Episode) == 3)
            {
                if (c.TryGetValue(media.Key + scope, out var old)) fresh.Excluded = old.Excluded;
                c[media.Key + scope] = fresh;
            }
            else
            {
                c[PackKey(media, scope)] = fresh;
                foreach (var k in c.Keys.Where(k => k.StartsWith($"tv:{media.TmdbId}:{media.Season}:", StringComparison.Ordinal) && ScopeOf(k) == scope).ToList()) c.Remove(k);
            }
        }
        await Save();
    }

    private static string ScopeOf(string key) { var i = key.IndexOf('@'); return i < 0 ? "" : key[i..]; }

    public async Task Unpin(MediaRef media, string scope = "")
    {
        var c = await Cache();
        lock (c)
        {
            c.Remove(media.Key + scope);
            if (media.IsTv) c.Remove(PackKey(media, scope));
        }
        await Save();
    }

    /// <summary>Portée où lire/écrire la source d'un utilisateur : sa source choisie à la main, sinon son profil, sinon le choix commun.</summary>
    public async Task<(string Scope, SourcePrefs Prefs)> ScopeFor(MediaRef media, Guid? user)
    {
        var prefs = Access.Prefs(user);
        var own = Access.UserScope(user);
        if (own.Length > 0 && prefs.Scope.Length == 0)
        {
            var c = await Cache();
            lock (c)
                if ((c.TryGetValue(media.Key + own, out var e) && e.Pinned) || (media.IsTv && c.TryGetValue(PackKey(media, own), out var p) && p.Pinned))
                    return (own, prefs);
        }
        return (prefs.Scope, prefs);
    }

    /// <param name="user">Utilisateur qui lit (profil, trackers autorisés, source choisie) ; null = réglages communs.</param>
    public async Task<Entry> Resolve(MediaRef media, CancellationToken ct, Guid? user = null)
    {
        var (scope, prefs) = await ScopeFor(media, user);
        var cache = await Cache();
        lock (cache) { if (cache.TryGetValue(media.Key + scope, out var e) && e.Hash.Length > 0 && Access.Ok(prefs.Indexers, e.Indexer)) return e; }
        var flight = media.Key + scope;
        var lazy = _inflight.GetOrAdd(flight, _ => new Lazy<Task<Entry>>(() => ResolveCore(media, prefs, scope)));
        try { return await lazy.Value.WaitAsync(ct); }
        finally { if (lazy.Value.IsCompleted) _inflight.TryRemove(flight, out _); }
    }

    /// <summary>Titres, année et sources classées (recherches Prowlarr mises en cache 4 h).</summary>
    public async Task<Plan> BuildPlan(MediaRef media, CancellationToken ct, SourcePrefs? prefs = null)
    {
        var cache = await Cache();
        HashSet<string> excluded;
        lock (cache)
        {
            excluded = cache.TryGetValue(media.Key, out var prev) ? [.. prev.Excluded] : [];
            if (prefs is { Scope.Length: > 0 } && cache.TryGetValue(media.Key + prefs.Scope, out var mine)) excluded.UnionWith(mine.Excluded);
        }

        var titles = new List<string>(); int year = 0;
        var detail = await clients.Tmdb($"{media.Kind}/{media.TmdbId}", ct);
        var fr = Clients.Text(detail, media.IsTv ? "name" : "title");
        var orig = Clients.Text(detail, media.IsTv ? "original_name" : "original_title");
        var date = Clients.Text(detail, media.IsTv ? "first_air_date" : "release_date");
        if (date.Length >= 4) int.TryParse(date[..4], out year);
        var runtime = media.IsTv ? 0 : Clients.Int(detail, "runtime");
        if (fr.Length > 0) titles.Add(fr);
        if (orig.Length > 0 && !titles.Contains(orig, StringComparer.OrdinalIgnoreCase)) titles.Add(orig);
        try
        {
            var en = Clients.Text(await clients.Tmdb($"{media.Kind}/{media.TmdbId}", ct, "en-US"), media.IsTv ? "name" : "title");
            if (en.Length > 0 && !titles.Contains(en, StringComparer.OrdinalIgnoreCase)) titles.Add(en);
        }
        catch { /* facultatif */ }

        // recherches textuelles : toujours lancées (les trackers publics ne gèrent pas la recherche par identifiant)
        var suffixes = media.IsTv
            ? new[] { $" S{media.Season:00}E{media.Episode:00}", $" S{media.Season:00}" }
            : new[] { year > 0 ? $" {year}" : "" };
        var searches = new List<Task<List<Release>>>();
        foreach (var t in titles.Take(2)) foreach (var sfx in suffixes)
            searches.Add(clients.Search(Ranking.Normalize(t) + sfx, media.IsTv ? 5000 : 2000, ct).ContinueWith(x => x.IsCompletedSuccessfully ? x.Result : [], TaskScheduler.Default));

        // 1) recherche par identifiant TMDB : une requête précise (saison entière pour une série)
        var all = new List<Release>();
        var byId = new HashSet<string>();
        try
        {
            var r = await clients.SearchId(media.Kind, media.TmdbId, media.Season);
            if (media.IsTv && !r.Any(x => Ranking.TvFit(x.Title, media.Season, media.Episode) > 0))
                r = await clients.SearchId(media.Kind, media.TmdbId, media.Season, media.Episode);
            all.AddRange(r);
            foreach (var x in r) byId.Add(x.Link);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug("Recherche par identifiant : {M}", ex.Message); }

        // 2) fusion avec les recherches textuelles
        foreach (var l in await Task.WhenAll(searches)) foreach (var x in l) if (all.All(y => y.Link != x.Link)) all.Add(x);
        return new Plan(titles, year, Ranking.Rank(all, media, titles, year, excluded, byId, runtime, prefs), all.Count);

    }

    private async Task<Entry> ResolveCore(MediaRef media, SourcePrefs prefs, string scope)
    {
        var cfg = Clients.Config;
        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = overall.Token;
        var cache = await Cache();
        HashSet<string> excluded;
        Entry? own, pack = null;
        lock (cache)
        {
            own = cache.GetValueOrDefault(media.Key + scope);
            excluded = own is null ? [] : [.. own.Excluded];
            if (media.IsTv) pack = cache.GetValueOrDefault(PackKey(media, scope));
        }

        // 1) source imposée par l'utilisateur
        foreach (var pinned in new[] { own, pack })
        {
            if (pinned is not { Pinned: true, Link.Length: > 0 } || pinned.Hash.Length > 0 || !Access.Ok(prefs.Indexers, pinned.Indexer)) continue;
            try
            {
                var e = await TryRelease(media, new Release(pinned.Release, pinned.Link, 0, 0, pinned.Indexer), excluded, true, scope, ct);
                if (e is not null) return e;
                log.LogWarning("{Key} : la source choisie « {T} » ne contient pas le bon fichier, choix automatique", media.Key, pinned.Release);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("{Key} : source choisie inutilisable : {M}", media.Key, ex.Message); }
        }

        // 2) un pack déjà connu de TorrServer ?
        if (pack is { Hash.Length: > 0 } && Access.Ok(prefs.Indexers, pack.Indexer))
        {
            try
            {
                var st = await clients.GetTorrent(pack.Hash, ct);
                var f = await FindEpisodeFile((st.Files ?? []).Where(x => Ranking.IsVideo(x.Path)).ToList(), media, ct);
                if (f is not null) return await Store(media, st, f, new Release(pack.Release, pack.Link, 0, 0, pack.Indexer), excluded, scope);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Pack mémorisé inutilisable"); }
        }

        // 3) choix automatique
        var plan = await BuildPlan(media, ct, prefs);
        log.LogInformation("{Key} : {Total} résultats, {Kept} retenus ({Titles})", media.Key, plan.Total, plan.Ranked.Count, string.Join(" | ", plan.Titles));
        if (plan.Ranked.Count == 0) throw new KeyNotFoundException("Aucune source trouvée pour ce titre (vérifiez les indexeurs de Prowlarr).");

        Exception? last = null;
        var cands = plan.Ranked.Take(Math.Clamp(cfg.MaxCandidates, 1, 20)).ToList();
        for (var i = 0; i < cands.Count; i += 3)
        {
            ct.ThrowIfCancellationRequested();
            // 3 sources à la fois : la première qui répond avec le bon fichier gagne
            var batch = cands.Skip(i).Take(3).Select(c => Prepare(media, c.Release, ct)).ToList();
            var pending = batch.ToList();
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                if (done.IsFaulted) { last = done.Exception?.InnerException; continue; }
                if (done.IsCanceled) { last = new TimeoutException("Sources trop lentes"); continue; }
                if (done.Result is { } win)
                {
                    log.LogInformation("{Key} : « {T} » ({S} sources) → {F}", media.Key, win.Release.Title, win.Release.Seeders, win.File.Path);
                    return await Commit(media, win, excluded, false, scope);
                }
            }
            log.LogInformation("{Key} : sources {A}-{B} inutilisables, suivantes", media.Key, i + 1, Math.Min(i + 3, cands.Count));
        }
        throw new KeyNotFoundException("Aucune source utilisable (" + (last?.Message ?? "fichiers introuvables") + ").");
    }

    /// <summary>Lecture d'une source précise choisie à l'écran.</summary>
    public async Task<Entry> ResolveWith(MediaRef media, Release release, CancellationToken ct, bool pin = false, string scope = "")
    {
        var cache = await Cache();
        lock (cache)
        {
            if (cache.TryGetValue(media.Key + scope, out var e) && e.Hash.Length > 0 && e.Link == release.Link) return e;
            // même torrent déjà prêt ailleurs (autre utilisateur, choix commun) : réutilisé tel quel
            if (!pin && cache.TryGetValue(media.Key, out var g) && g.Hash.Length > 0 && g.Link == release.Link) return g;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        HashSet<string> excluded; lock (cache) excluded = cache.TryGetValue(media.Key + scope, out var prev) ? [.. prev.Excluded] : [];
        return await TryRelease(media, release, excluded, pin, scope, cts.Token)
            ?? throw new InvalidOperationException(media.IsTv ? "Cet épisode n'est pas dans ce torrent : choisissez une autre source." : "Aucun fichier vidéo dans ce torrent.");
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _absOffsets = new();

    /// <summary>Nombre d'épisodes des saisons précédentes (numérotation absolue des animes : S06E18 = 183).</summary>
    private async Task<int> AbsOffset(MediaRef media, CancellationToken ct)
    {
        if (_absOffsets.TryGetValue(media.Key.Split(':')[0] + media.TmdbId + "s" + media.Season, out var known)) return known;
        var d = await clients.Tmdb($"tv/{media.TmdbId}", ct);
        var n = 0;
        if (d.TryGetProperty("seasons", out var ss))
            foreach (var x in ss.EnumerateArray())
                if (x.TryGetProperty("season_number", out var sn) && sn.GetInt32() is > 0 && sn.GetInt32() < media.Season && x.TryGetProperty("episode_count", out var ec)) n += ec.GetInt32();
        _absOffsets[media.Key.Split(':')[0] + media.TmdbId + "s" + media.Season] = n;
        return n;
    }

    /// <summary>Fichier de l'épisode : numérotation S01E01 d'abord, puis numéro absolu (« 183 », « 181-185 »).</summary>
    private async Task<TorrentFile?> FindEpisodeFile(List<TorrentFile> videos, MediaRef media, CancellationToken ct)
    {
        var f = videos.FirstOrDefault(x => Ranking.MatchesEpisode(x.Path, media.Season, media.Episode));
        if (f is not null || media.Season < 1) return f;
        try
        {
            var abs = await AbsOffset(media, ct) + media.Episode;
            return videos.FirstOrDefault(x => Ranking.MatchesAbsolute(x.Path, abs));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private sealed record Prepared(Release Release, TorrentStatus Status, TorrentFile File, int Videos);

    /// <summary>Envoie le torrent à TorrServer et trouve le fichier voulu ; null s'il n'y est pas.</summary>
    private async Task<Prepared?> Prepare(MediaRef media, Release release, CancellationToken ct)
    {
        var cfg = Clients.Config;
        var status = await clients.AddTorrent(release, TimeSpan.FromSeconds(Math.Clamp(cfg.CandidateTimeoutSeconds, 5, 120)), ct);
        var videos = (status.Files ?? []).Where(x => Ranking.IsVideo(x.Path) && !Ranking.HasTag(x.Path, "sample")).ToList();
        TorrentFile? file;
        if (media.IsTv)
        {
            file = await FindEpisodeFile(videos, media, ct);
            if (file is null && videos.Count == 1 && Ranking.TvFit(release.Title, media.Season, media.Episode) == 3) file = videos[0];
        }
        else file = videos.OrderByDescending(x => x.Length).FirstOrDefault();
        return file is null ? null : new Prepared(release, status, file, media.IsTv ? videos.Count(x => Ranking.MatchesEpisode(x.Path, media.Season, 1) || Ranking.MatchesEpisode(x.Path, media.Season, 2)) : 0);
    }

    private async Task<Entry> Commit(MediaRef media, Prepared p, HashSet<string> excluded, bool keepPin, string scope)
    {
        if (media.IsTv && p.Videos > 1)
        {
            var cache = await Cache();
            lock (cache) cache[PackKey(media, scope)] = new Entry { Hash = p.Status.Hash, Release = p.Release.Title, Link = p.Release.Link, Indexer = p.Release.Indexer, Pinned = keepPin };
        }
        return await Store(media, p.Status, p.File, p.Release, excluded, scope, keepPin);
    }

    private async Task<Entry?> TryRelease(MediaRef media, Release release, HashSet<string> excluded, bool keepPin, string scope, CancellationToken ct) =>
        await Prepare(media, release, ct) is { } p ? await Commit(media, p, excluded, keepPin, scope) : null;

    private async Task<Entry> Store(MediaRef media, TorrentStatus st, TorrentFile f, Release release, HashSet<string> excluded, string scope, bool pinned = false)
    {
        var e = new Entry { Hash = st.Hash, FileId = f.Id, FileName = f.Path, Length = f.Length, Release = release.Title, Link = release.Link, Indexer = release.Indexer, Pinned = pinned, Excluded = [.. excluded] };
        var cache = await Cache();
        lock (cache) cache[media.Key + scope] = e;
        await Save();
        return e;
    }
}
