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
}

public sealed record Plan(List<string> Titles, int Year, IReadOnlyList<Scored> Ranked, int Total);

/// <summary>Trouve (et mémorise) le torrent et le fichier à lire pour un film ou un épisode.</summary>
public sealed class Resolver(Clients clients, ILogger<Resolver> log)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Entry>>> _inflight = new();
    private readonly SemaphoreSlim _fileGate = new(1);
    private Dictionary<string, Entry>? _cache;
    /// <summary>Titres des sources muettes (aucune donnée reçue) : écartées quelques heures, pour passer à la suivante.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _dead = new();
    private static readonly TimeSpan DeadFor = TimeSpan.FromHours(6);

    public bool IsDead(string title) => _dead.TryGetValue(title, out var at) && DateTime.UtcNow - at < DeadFor;

    private HashSet<string> DeadTitles() => _dead.Where(x => DateTime.UtcNow - x.Value < DeadFor).Select(x => x.Key).ToHashSet();

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

    /// <summary>Oublie la source mémorisée de ce titre ; avec dead = true, elle est aussi écartée quelques heures.</summary>
    public async Task Invalidate(MediaRef media, string scope, bool dead)
    {
        var c = await Cache();
        lock (c)
        {
            if (!c.TryGetValue(media.Key + scope, out var e)) return;
            if (dead && e.Release.Length > 0) _dead[e.Release] = DateTime.UtcNow;
            if (media.IsTv && c.TryGetValue(PackKey(media, scope), out var p) && p.Release == e.Release) c.Remove(PackKey(media, scope));
            c.Remove(media.Key + scope);
        }
        await Save();
    }

    /// <summary>
    /// Portée où lire/écrire la source d'un utilisateur : son profil, sinon le choix commun.
    /// Un pack qu'il a choisi dans le menu « Version » reste utilisé pour la suite de la saison.
    /// </summary>
    public async Task<(string Scope, SourcePrefs Prefs)> ScopeFor(MediaRef media, Guid? user)
    {
        var prefs = Access.Prefs(user);
        var own = Access.UserScope(user);
        if (media.IsTv && own.Length > 0 && prefs.Scope.Length == 0)
        {
            var c = await Cache();
            lock (c)
                if (c.TryGetValue(PackKey(media, own), out var p) && p.Hash.Length > 0 && !IsDead(p.Release)) return (own, prefs);
        }
        return (prefs.Scope, prefs);
    }

    /// <param name="user">Utilisateur qui lit (profil, trackers autorisés, source choisie) ; null = réglages communs.</param>
    public async Task<Entry> Resolve(MediaRef media, CancellationToken ct, Guid? user = null)
    {
        var (scope, prefs) = await ScopeFor(media, user);
        var cache = await Cache();
        lock (cache) { if (cache.TryGetValue(media.Key + scope, out var e) && e.Hash.Length > 0 && Access.Ok(prefs.Indexers, e.Indexer) && !IsDead(e.Release)) return e; }
        var flight = media.Key + scope;
        var lazy = _inflight.GetOrAdd(flight, _ => new Lazy<Task<Entry>>(() => ResolveCore(media, prefs, scope)));
        try { return await lazy.Value.WaitAsync(ct); }
        finally { if (lazy.Value.IsCompleted) _inflight.TryRemove(flight, out _); }
    }

    /// <summary>Titres, année et sources classées (recherches Prowlarr mises en cache 4 h).</summary>
    public async Task<Plan> BuildPlan(MediaRef media, CancellationToken ct, SourcePrefs? prefs = null, Func<Plan, Task>? partial = null, TimeSpan? maxWait = null)
    {
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
        // titres alternatifs (« Law and Order SVU »…) : reconnus dans les noms de torrents, le plus court est aussi cherché
        string? alt = null;
        try
        {
            var a = await clients.Tmdb($"{media.Kind}/{media.TmdbId}/alternative_titles", ct);
            var list = (a.TryGetProperty("results", out var r1) ? r1 : a.TryGetProperty("titles", out var r2) ? r2 : default);
            if (list.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var alts = list.EnumerateArray()
                    .Where(x => Clients.Text(x, "iso_3166_1") is "US" or "GB" or "FR")
                    .Select(x => Clients.Text(x, "title")).Where(t => t.Length > 0 && t.All(c => c < 0x250))
                    .Where(t => !titles.Any(x => Ranking.Normalize(x) == Ranking.Normalize(t))).Distinct().ToList();
                // cherché seulement s'il apporte un autre nom (« law and order svu »), pas une variante du titre (« JoJo… : Golden Wind »)
                var mains = titles.Select(Ranking.Normalize).ToList();
                alt = alts.Where(t => Ranking.Normalize(t).Split(' ').Length >= 2)
                    .Where(t => !mains.Any(x => Ranking.Normalize(t).StartsWith(x, StringComparison.Ordinal) || x.StartsWith(Ranking.Normalize(t), StringComparison.Ordinal)))
                    .OrderBy(t => Ranking.Normalize(t).Length).FirstOrDefault();
                titles.AddRange(alts);
            }
        }
        catch { /* facultatif */ }

        // recherches textuelles : toujours lancées (les trackers publics ne gèrent pas la recherche par identifiant)
        var suffixes = media.IsTv
            ? new[] { $" S{media.Season:00}E{media.Episode:00}", $" S{media.Season:00}" }
            : new[] { year > 0 ? $" {year}" : "" };
        var searches = new List<Task<List<Release>>>();
        foreach (var t in titles.Take(2).Append(alt).OfType<string>().DistinctBy(Ranking.Normalize)) foreach (var sfx in suffixes)
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

        // 2) premiers résultats : si des recherches traînent, on transmet déjà ce qu'on a
        var pending = Task.WhenAll(searches);
        if (partial is not null && await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(4), ct)) != pending)
        {
            var early = new List<Release>(all);
            foreach (var s in searches.Where(x => x.IsCompletedSuccessfully)) foreach (var x in s.Result) if (early.All(y => y.Link != x.Link)) early.Add(x);
            var ranked = Ranking.Rank(early, media, titles, year, DeadTitles(), byId, runtime, prefs);
            if (ranked.Count > 0) { try { await partial(new Plan(titles, year, ranked, early.Count)); } catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug("Premiers résultats : {M}", ex.Message); } }
        }

        // 3) fusion avec les recherches textuelles (au lancement : on n'attend pas les indexeurs lents, leurs résultats restent en cache)
        if (maxWait is { } mw) await Task.WhenAny(pending, Task.Delay(mw, ct));
        else await pending;
        foreach (var l in searches.Where(x => x.IsCompletedSuccessfully).Select(x => x.Result)) foreach (var x in l) if (all.All(y => y.Link != x.Link)) all.Add(x);
        return new Plan(titles, year, Ranking.Rank(all, media, titles, year, DeadTitles(), byId, runtime, prefs), all.Count);

    }

    private async Task<Entry> ResolveCore(MediaRef media, SourcePrefs prefs, string scope)
    {
        var cfg = Clients.Config;
        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = overall.Token;
        var cache = await Cache();
        Entry? pack = null;
        if (media.IsTv) lock (cache) pack = cache.GetValueOrDefault(PackKey(media, scope));

        // un pack déjà connu de TorrServer ?
        if (pack is { Hash.Length: > 0 } && Access.Ok(prefs.Indexers, pack.Indexer) && !IsDead(pack.Release))
        {
            try
            {
                var st = await clients.GetTorrent(pack.Hash, ct);
                var f = await FindEpisodeFile((st.Files ?? []).Where(x => Ranking.IsVideo(x.Path)).ToList(), media, ct);
                if (f is not null) return await Store(media, st, f, new Release(pack.Release, pack.Link, 0, 0, pack.Indexer), scope);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Pack mémorisé inutilisable"); }
        }

        // choix automatique
        var plan = await BuildPlan(media, ct, prefs, maxWait: TimeSpan.FromSeconds(15));
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
                    var entry = await Commit(media, win, scope);
                    Discard(batch.Where(x => x != done), win.Status.Hash);
                    return entry;
                }
            }
            log.LogInformation("{Key} : sources {A}-{B} inutilisables, suivantes", media.Key, i + 1, Math.Min(i + 3, cands.Count));
        }
        throw new KeyNotFoundException("Aucune source utilisable (" + (last?.Message ?? "fichiers introuvables") + ").");
    }

    /// <summary>Lecture d'une source précise choisie à l'écran.</summary>
    public async Task<Entry> ResolveWith(MediaRef media, Release release, CancellationToken ct, string scope = "")
    {
        var cache = await Cache();
        lock (cache)
        {
            if (cache.TryGetValue(media.Key + scope, out var e) && e.Hash.Length > 0 && e.Release == release.Title) return e;
            // même torrent déjà prêt ailleurs (autre utilisateur, choix commun) : réutilisé tel quel
            if (cache.TryGetValue(media.Key, out var g) && g.Hash.Length > 0 && g.Release == release.Title) return g;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        return await TryRelease(media, release, scope, cts.Token)
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

    /// <summary>Premier numéro d'épisode de la saison chez TMDB (certains animes continuent la numérotation : saison 2 = 63 à 136).</summary>
    private async Task<int> SeasonStart(MediaRef media, CancellationToken ct)
    {
        var d = await clients.Tmdb($"tv/{media.TmdbId}/season/{media.Season}", ct);
        return d.TryGetProperty("episodes", out var eps) ? eps.EnumerateArray().Select(e => Clients.Int(e, "episode_number")).DefaultIfEmpty(1).Min() : 1;
    }

    /// <summary>
    /// Fichier de l'épisode. Les packs ne numérotent pas tous pareil : S02E103 (comme TMDB), S02E41 (depuis le début de la saison),
    /// S01E103 ou « 103 » (sur toute la série).
    /// </summary>
    private async Task<TorrentFile?> FindEpisodeFile(List<TorrentFile> videos, MediaRef media, CancellationToken ct)
    {
        var f = videos.FirstOrDefault(x => Ranking.MatchesEpisode(x.Path, media.Season, media.Episode));
        if (f is not null || media.Season < 1) return f;
        try
        {
            int abs;
            var map = await clients.SeasonMap(media.TmdbId, ct);
            if (map is not null && map.TryGetValue(media.Season, out var eps) && media.Episode >= 1 && media.Episode <= eps.Count)
                abs = Clients.Int(eps[media.Episode - 1], "episode_number");   // numéro absolu de TMDB
            else if (await SeasonStart(media, ct) is var start && start > 1)
            {
                var rel = media.Episode - start + 1;   // numérotation depuis le début de la saison
                if (rel >= 1 && videos.FirstOrDefault(x => Ranking.MatchesEpisode(x.Path, media.Season, rel)) is { } r) return r;
                abs = media.Episode;                    // TMDB donne déjà le numéro absolu
            }
            else abs = await AbsOffset(media, ct) + media.Episode;
            return videos.FirstOrDefault(x => Ranking.MatchesEpisode(x.Path, 1, abs))
                ?? videos.FirstOrDefault(x => Ranking.MatchesAbsolute(x.Path, abs));
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
        if (file is null) { await RemoveIfUnused(status.Hash); return null; }
        return new Prepared(release, status, file, media.IsTv ? videos.Count(x => Ranking.MatchesEpisode(x.Path, media.Season, 1) || Ranking.MatchesEpisode(x.Path, media.Season, 2)) : 0);
    }

    private async Task<Entry> Commit(MediaRef media, Prepared p, string scope)
    {
        if (media.IsTv && p.Videos > 1)
        {
            var cache = await Cache();
            lock (cache) cache[PackKey(media, scope)] = new Entry { Hash = p.Status.Hash, Release = p.Release.Title, Link = p.Release.Link, Indexer = p.Release.Indexer };
        }
        return await Store(media, p.Status, p.File, p.Release, scope);
    }

    private async Task<Entry?> TryRelease(MediaRef media, Release release, string scope, CancellationToken ct) =>
        await Prepare(media, release, ct) is { } p ? await Commit(media, p, scope) : null;

    /// <summary>Les sources perdantes d'une course sont retirées de TorrServer dès qu'elles répondent (sinon elles s'accumulent).</summary>
    private void Discard(IEnumerable<Task<Prepared?>> losers, string winner)
    {
        foreach (var t in losers)
            _ = t.ContinueWith(async x =>
            {
                if (x.IsCompletedSuccessfully && x.Result is { } p && !p.Status.Hash.Equals(winner, StringComparison.OrdinalIgnoreCase)) await RemoveIfUnused(p.Status.Hash);
            }, TaskScheduler.Default);
    }

    private async Task RemoveIfUnused(string hash)
    {
        try
        {
            var c = await Cache();
            lock (c) if (c.Values.Any(e => e.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))) return;
            await clients.RemoveTorrent(hash);
        }
        catch (Exception ex) { log.LogDebug("Retrait de {H} : {M}", hash, ex.Message); }
    }

    private async Task<Entry> Store(MediaRef media, TorrentStatus st, TorrentFile f, Release release, string scope)
    {
        var e = new Entry { Hash = st.Hash, FileId = f.Id, FileName = f.Path, Length = f.Length, Release = release.Title, Link = release.Link, Indexer = release.Indexer };
        var cache = await Cache();
        lock (cache) cache[media.Key + scope] = e;
        await Save();
        return e;
    }
}
