using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Crée les titres à la demande et remplit le menu « Version » de Jellyfin avec les sources torrent.</summary>
public sealed class VersionService(LibraryService lib, Resolver resolver, SourceStore store, ProbeService probe, ILibraryManager library, IFileSystem files, ILogger<VersionService> log)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(4);
    private readonly ConcurrentDictionary<string, DateTime> _fresh = new();
    private readonly ConcurrentDictionary<string, Task> _running = new();
    /// <summary>Une relecture à la fois par série : deux en parallèle provoquent des erreurs en base côté Jellyfin.</summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _seriesGates = new();

    private static readonly string[] LangTags = ["MULTI", "VFF", "VFQ", "VF2", "VFI", "TRUEFRENCH", "FRENCH", "VF", "VOSTFR", "SUBFRENCH"];

    /// <summary>
    /// Étiquette d'une version. Pas de « 1080p » : Jellyfin classerait ces versions avant « Meilleure source »
    /// (il met en tête les noms contenant une résolution), qui ne serait plus la version principale.
    /// </summary>
    public static string Label(Release r, bool pack)
    {
        var res = Ranking.Resolution(r.Title);
        var parts = new List<string> { res == 0 ? "?" : res == 2160 ? "4K" : res.ToString() };
        var langs = LangTags.Where(t => Ranking.HasTag(r.Title, t)).Take(2).ToList();
        parts.Add(langs.Count > 0 ? string.Join(" ", langs) : "VO");
        if (Ranking.HasTag(r.Title, "x265") || Ranking.HasTag(r.Title, "hevc")) parts.Add("HEVC");
        if (Ranking.HasTag(r.Title, "AV1")) parts.Add("AV1");
        if (pack) parts.Add("pack");
        parts.Add($"{r.Seeders} sources");
        parts.Add($"{r.Size / 1073741824.0:0.0} Go".Replace('.', ','));
        if (r.Indexer.Length > 0) parts.Add(r.Indexer);
        return Regex.Replace(string.Join(" · ", parts), @"(\d{3,})([ip])", "$1 $2");
    }

    /// <summary>Lance (ou rejoint) la préparation des sources d'un titre ; une seule à la fois par clé.</summary>
    private Task Ensure(string key, Func<Task> work)
    {
        if (_fresh.TryGetValue(key, out var at) && DateTime.UtcNow - at < Ttl) return Task.CompletedTask;
        return _running.GetOrAdd(key, k => Task.Run(async () =>
        {
            try { await work(); _fresh[key] = DateTime.UtcNow; }
            catch (Exception ex) { log.LogWarning("Sources {K} : {M}", key, ex.Message); }
            finally { _running.TryRemove(key, out _); }
        }));
    }

    private void Background(string key, Func<Task> work) => _ = Ensure(key, work);

    /// <summary>Attend une préparation, au plus <paramref name="seconds"/> s (elle continue en arrière-plan au-delà).</summary>
    private static async Task Wait(Task t, int seconds, CancellationToken ct)
    {
        try { await t.WaitAsync(TimeSpan.FromSeconds(seconds), ct); }
        catch (TimeoutException) { }
    }

    /// <summary>Avancement (%) des titres en cours d'ouverture.</summary>
    public static readonly ConcurrentDictionary<string, int> Progress = new();

    /// <summary>Garantit que le film / la série existe dans Jellyfin ; renvoie l'élément à ouvrir.</summary>
    public async Task<BaseItem> Open(string type, int id, CancellationToken ct)
    {
        var pk = type + id;
        Progress[pk] = 5;
        try { return await OpenCore(type, id, pk, ct); }
        finally { Progress.TryRemove(pk, out _); }
    }

    private async Task<BaseItem> OpenCore(string type, int id, string pk, CancellationToken ct)
    {
        var dirs = new DirectoryService(files);
        if (!lib.LoadCatalog().Any(x => x.Type == type && x.TmdbId == id)) await lib.Add(type, id, null, false, ct);
        Progress[pk] = 20;
        if (type == "movie")
        {
            var path = lib.StrmPath(new MediaRef("movie", id))!;
            if (!File.Exists(path)) await lib.Add(type, id, null, false, ct);   // ancien nommage → réécriture
            var item = await lib.WaitItem(path, false, false, dirs, ct);
            Progress[pk] = 40;
            probe.ProbePath(path);   // « Meilleure source » prête à lire en direct
            var movie = lib.FindMovie(id) ?? item;
            lib.RefreshMetadata(movie);
            // la fiche ne s'ouvre qu'avec ses sources (30 s max) : plus besoin de la rafraîchir à la main
            Progress[pk] = 50;
            await Wait(Ensure("movie" + id, () => MovieVersions(id, CancellationToken.None)), 30, ct);
            Progress[pk] = 85;
            await SyncMovie(id, ct);   // titre ouvert avant : Jellyfin n'avait pas pris en compte les versions
            return lib.FindMovie(id) ?? movie;
        }
        var folder = lib.ShowFolder(id) ?? throw new InvalidOperationException("Série introuvable.");
        Progress[pk] = 30;
        var show = await lib.WaitItem(folder, true, true, dirs, ct);
        Progress[pk] = 80;
        lib.RefreshMetadata(show);
        // la page de la série n'a pas besoin des sources : celles des 2 premières saisons se préparent en arrière-plan,
        // celles d'un épisode au moment où on ouvre sa page
        foreach (var sn in Seasons(id).Take(2)) Background($"tv{id}s{sn}", () => SeasonVersions(id, sn, CancellationToken.None));
        return show;
    }

    private int JellyfinVersions(int id) => (lib.FindMovie(id) as Video)?.GetAllVersions().Count() ?? 0;

    /// <summary>Attend (20 s max) que Jellyfin ait pris en compte toutes les versions écrites sur le disque.</summary>
    private async Task SyncMovie(int id, CancellationToken ct)
    {
        var disk = VersionsOnDisk(id);
        if (disk <= 1 || JellyfinVersions(id) >= disk) return;
        await lib.ApplyMovieVersions(id, ct);
        for (var i = 0; i < 60 && JellyfinVersions(id) < disk; i++) await Task.Delay(500, ct);
    }

    private int VersionsOnDisk(int id) => lib.VersionsOnDisk(new MediaRef("movie", id));

    private IEnumerable<int> Seasons(int id)
    {
        var show = lib.ShowFolder(id);
        if (show is null || !Directory.Exists(show)) return [];
        return Directory.GetDirectories(show, "Season *").Select(d => int.TryParse(Path.GetFileName(d)[7..], out var n) ? n : 0).Where(n => n > 0).OrderBy(n => n).ToList();
    }

    private static int Max => Math.Clamp(Clients.Config.MaxResults, 3, 15);

    private async Task MovieVersions(int id, CancellationToken ct)
    {
        var m = new MediaRef("movie", id);
        var plan = await resolver.BuildPlan(m, ct);
        var versions = plan.Ranked.Take(Max).Select(x => (Sid: store.Put(x.Release), Label: Label(x.Release, false))).ToList();
        store.Save();
        if (lib.WriteVersions(m, versions)) await lib.ApplyMovieVersions(id, ct);   // relit toute la bibliothèque : seulement si besoin
        log.LogInformation("{K} : {N} versions", m.Key, versions.Count);
    }

    /// <summary>Packs de la saison : proposés comme versions de chacun de ses épisodes.</summary>
    private async Task SeasonVersions(int id, int season, CancellationToken ct)
    {
        var show = lib.ShowFolder(id);
        var dir = show is null ? null : Path.Combine(show, $"Season {season:00}");
        if (dir is null || !Directory.Exists(dir)) return;
        var plan = await resolver.BuildPlan(new MediaRef("tv", id, season, 1), ct);
        var packs = plan.Ranked.Where(x => Ranking.TvFit(x.Release.Title, season, 1) == 2).Take(Max)
            .Select(x => (Sid: store.Put(x.Release), Label: Label(x.Release, true))).ToList();
        store.Save();
        var n = 0;
        foreach (var e in Episodes(dir))
        {
            var m = new MediaRef("tv", id, season, e);
            if (lib.WriteVersions(m, packs, additive: true, cap: Max)) n++;
        }
        if (n > 0) await RescanSeason(id, season, ct);
        log.LogInformation("tv:{Id} saison {S} : {P} packs, {N} épisodes mis à jour", id, season, packs.Count, n);
    }

    /// <summary>Sources propres à un épisode (épisode exact + packs), quand on ouvre sa page. Renvoie vrai si le menu « Version » a changé.</summary>
    private async Task<bool> EpisodeVersions(MediaRef m, CancellationToken ct)
    {
        var plan = await resolver.BuildPlan(m, ct);
        var versions = plan.Ranked.Take(Max)
            .Select(x => (Sid: store.Put(x.Release), Label: Label(x.Release, Ranking.TvFit(x.Release.Title, m.Season, m.Episode) == 2))).ToList();
        store.Save();
        var changed = lib.WriteVersions(m, versions) || OutOfSync(m);
        if (versions.Count > 0) _fresh[m.Key] = DateTime.UtcNow;   // 0 source : on réessaiera à la prochaine ouverture
        if (changed) await RescanSeason(m.TmdbId, m.Season, ct);
        log.LogInformation("{K} : {N} versions{C}", m.Key, versions.Count, changed ? " (menu mis à jour)" : "");
        return changed;
    }

    /// <summary>Jellyfin n'a pas (encore) pris en compte tous les fichiers de cet épisode.</summary>
    private bool OutOfSync(MediaRef m) =>
        lib.StrmPath(m) is { } path && library.FindByPath(path, false) is Video video && video.GetAllVersions().Count() != lib.VersionsOnDisk(m);

    private async Task RescanSeason(int id, int season, CancellationToken ct)
    {
        var show = lib.ShowFolder(id);
        if (show is null) return;
        var gate = _seriesGates.GetOrAdd(id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            var dirs = new DirectoryService(files);
            if (library.FindByPath(Path.Combine(show, $"Season {season:00}"), true) is Folder seasonItem)
                await seasonItem.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(dirs), false, false, ct);
            else if (library.FindByPath(show, true) is Folder series)
                await series.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(dirs), true, false, ct);
        }
        finally { gate.Release(); }
    }

    private static IEnumerable<int> Episodes(string dir) =>
        Directory.GetFiles(dir, "*" + LibraryService.AutoLabel + ".strm")
            .Select(f => Regex.Match(Path.GetFileName(f), @"S\d+E(\d+) - ")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Distinct();

    /// <summary>
    /// Appelé quand on ouvre la fiche d'un élément TorrServer dans Jellyfin.
    /// Renvoie vrai si des versions viennent d'être ajoutées (la page doit être rechargée).
    /// </summary>
    public async Task<bool> Details(BaseItem item, CancellationToken ct)
    {
        var path = item.Path ?? "";
        if (!path.StartsWith(LibraryService.Root, StringComparison.Ordinal)) return false;
        var tm = Regex.Match(path, @"\[tmdbid-(\d+)\]");
        if (!tm.Success) return false;
        var id = int.Parse(tm.Groups[1].Value);
        if (item is Episode ep && ep.ParentIndexNumber is int s && ep.IndexNumber is int e)
        {
            var m = new MediaRef("tv", id, s, e);
            if (lib.StrmPath(m) is { } strm) _ = probe.ProbePath(strm);   // lancement immédiat : torrent chargé et durée connue d'avance
            if (_fresh.TryGetValue(m.Key, out var at) && DateTime.UtcNow - at < Ttl) return false;
            return await EpisodeVersions(m, ct);
        }
        if (item is Season season && season.IndexNumber is int sn)
        {
            Background($"tv{id}s{sn}", () => SeasonVersions(id, sn, CancellationToken.None));
            return false;
        }
        if (item is Series)
        {
            foreach (var x in Seasons(id).Take(2)) Background($"tv{id}s{x}", () => SeasonVersions(id, x, CancellationToken.None));
            return false;
        }
        if (path.StartsWith(LibraryService.MoviesDir, StringComparison.Ordinal))
        {
            var had = (lib.FindMovie(id) as Video)?.GetAllVersions().Count() ?? 1;
            await Wait(Ensure("movie" + id, () => MovieVersions(id, CancellationToken.None)), 30, ct);
            await SyncMovie(id, ct);
            return ((lib.FindMovie(id) as Video)?.GetAllVersions().Count() ?? 1) > had;
        }
        return false;
    }

    /// <summary>La source automatique de ce titre a changé : la durée de « Meilleure source » n'est plus valable.</summary>
    public void SourceChanged(MediaRef m)
    {
        if (lib.StrmPath(m) is { } path) probe.Forget(path);
    }

    /// <summary>
    /// Lecture en cours : les packs de la saison et l'épisode suivant sont préparés à l'avance,
    /// pour que l'enchaînement soit immédiat.
    /// </summary>
    public void Played(MediaRef m, Guid? user)
    {
        if (!m.IsTv) return;
        Background($"tv{m.TmdbId}s{m.Season}", () => SeasonVersions(m.TmdbId, m.Season, CancellationToken.None));
        Background($"next:{m.Key}", async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2));   // pas pendant le démarrage : même torrent lu deux fois = lancement plus lent
            var next = new[] { new MediaRef("tv", m.TmdbId, m.Season, m.Episode + 1), new MediaRef("tv", m.TmdbId, m.Season + 1, 1) }
                .FirstOrDefault(x => lib.StrmPath(x) is { } p && File.Exists(p));
            if (next is null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await resolver.Resolve(next, cts.Token, user);
            await probe.ProbePath(lib.StrmPath(next)!);
            log.LogInformation("{K} : épisode suivant prêt", next.Key);
        });
    }
}
