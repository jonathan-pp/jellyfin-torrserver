using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

public sealed class TooLongException(string title) : Exception($"« {title} » est trop longue pour le catalogue automatique.");

public sealed class CatalogItem
{
    public string Type { get; set; } = "";
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public int Year { get; set; }
    public string Poster { get; set; } = "";
    /// <summary>Saisons ajoutées (séries) ; vide = toutes.</summary>
    public List<int> Seasons { get; set; } = [];
    public int Episodes { get; set; }
    /// <summary>Ajouté par le catalogue automatique (et non à la main).</summary>
    public bool Auto { get; set; }
}

/// <summary>Écrit les fichiers .strm de la bibliothèque virtuelle et tient à jour son catalogue.</summary>
public sealed class LibraryService(Clients clients, ILibraryManager library, IProviderManager providers, MediaBrowser.Model.IO.IFileSystem files, ILogger<LibraryService> log)
{
    private readonly SemaphoreSlim _gate = new(1);
    public const string AutoLabel = "00 · ⚡ Meilleure source";
    public const string MoviesName = "Films (TorrServer)";
    public const string ShowsName = "Séries (TorrServer)";

    public static string Root
    {
        get
        {
            var c = Clients.Config.LibraryRoot.Trim();
            return c.Length > 0 ? c : Path.Combine(Plugin.Instance!.DataFolderPath, "library");
        }
    }
    public static string MoviesDir => Path.Combine(Root, "Films");
    public static string ShowsDir => Path.Combine(Root, "Series");
    private static string CatalogFile => Path.Combine(Plugin.Instance!.DataFolderPath, "catalog.json");

    public static string EnsureKey()
    {
        var cfg = Clients.Config;
        if (cfg.StreamKey.Length < 16)
        {
            cfg.StreamKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Plugin.Instance!.SaveConfiguration();
        }
        return cfg.StreamKey;
    }

    private static string StreamUrl(string path) => Clients.Endpoint(Clients.Config.JellyfinInternalUrl, $"TorrServerPlugin/stream/{EnsureKey()}/{path}").AbsoluteUri;

    public static string Safe(string name)
    {
        var s = Regex.Replace(name, @"[<>:""/\\|?*\x00-\x1f]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        return s.Length == 0 ? "Sans titre" : s;
    }

    /// <summary>Chemin du fichier .strm correspondant à un film ou un épisode, s'il est dans le catalogue.</summary>
    public string? StrmPath(MediaRef m)
    {
        var loc = Location(m);
        return loc is null ? null : Path.Combine(loc.Value.Dir, $"{loc.Value.Base} - {AutoLabel}.strm");
    }

    private List<CatalogItem>? _catalog;
    private static string HiddenFile => Path.Combine(Plugin.Instance!.DataFolderPath, "hidden.json");

    public List<CatalogItem> LoadCatalog()
    {
        lock (this)
        {
            if (_catalog is null)
            {
                try { _catalog = File.Exists(CatalogFile) ? JsonSerializer.Deserialize<List<CatalogItem>>(File.ReadAllText(CatalogFile)) ?? [] : []; }
                catch { _catalog = []; }
            }
            return [.. _catalog];
        }
    }

    private void SaveCatalog(List<CatalogItem> items)
    {
        lock (this)
        {
            _catalog = [.. items];
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogFile)!);
            File.WriteAllText(CatalogFile, JsonSerializer.Serialize(items));
        }
    }

    private static HashSet<string> LoadHidden()
    {
        try { return File.Exists(HiddenFile) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(HiddenFile)) ?? [] : []; }
        catch { return []; }
    }

    private static void Hide(string key)
    {
        var h = LoadHidden(); h.Add(key);
        Directory.CreateDirectory(Path.GetDirectoryName(HiddenFile)!);
        File.WriteAllText(HiddenFile, JsonSerializer.Serialize(h));
    }

    private static void DeleteIfExists(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    /// <summary>Dossier et préfixe des fichiers d'un film / d'un épisode.</summary>
    public (string Dir, string Base)? Location(MediaRef m)
    {
        var item = LoadCatalog().FirstOrDefault(x => x.Type == m.Kind && x.TmdbId == m.TmdbId);
        if (item is null) return null;
        var label = item.Year > 0 ? $"{Safe(item.Title)} ({item.Year})" : Safe(item.Title);
        var folder = $"{label} [tmdbid-{item.TmdbId}]";
        return m.IsTv
            ? (Path.Combine(ShowsDir, folder, $"Season {m.Season:00}"), $"{Safe(item.Title)} S{m.Season:00}E{m.Episode:00}")
            : (Path.Combine(MoviesDir, folder), folder);
    }

    public string? ShowFolder(int tmdbId)
    {
        var item = LoadCatalog().FirstOrDefault(x => x.Type == "tv" && x.TmdbId == tmdbId);
        if (item is null) return null;
        var label = item.Year > 0 ? $"{Safe(item.Title)} ({item.Year})" : Safe(item.Title);
        return Path.Combine(ShowsDir, $"{label} [tmdbid-{item.TmdbId}]");
    }

    /// <summary>Écrit les versions (une par source) à côté de « Meilleure source » et supprime les anciennes.</summary>
    public int WriteVersions(MediaRef m, IReadOnlyList<(string Sid, string Label)> versions)
    {
        var loc = Location(m);
        if (loc is null || !Directory.Exists(loc.Value.Dir)) return 0;
        var (dir, b) = loc.Value;
        var auto = Path.Combine(dir, $"{b} - {AutoLabel}.strm");
        if (!File.Exists(auto)) return 0;
        var path = m.IsTv ? $"tv/{m.TmdbId}/{m.Season}/{m.Episode}" : $"movie/{m.TmdbId}";
        var keep = new HashSet<string>(StringComparer.Ordinal) { auto };
        var n = 1;
        foreach (var v in versions)
        {
            var file = Path.Combine(dir, $"{b} - {n++:00} · {SafeLabel(v.Label)}.strm");
            WriteIfChanged(file, StreamUrl(path) + "?sid=" + v.Sid);
            keep.Add(file);
        }
        foreach (var f in Directory.GetFiles(dir, "*.strm"))
            if (Path.GetFileName(f).StartsWith(b + " - ", StringComparison.Ordinal) && !keep.Contains(f)) DeleteIfExists(f);
        return versions.Count;
    }

    public static string SafeLabelPublic(string s) => SafeLabel(s);

    private static string SafeLabel(string s)
    {
        s = Safe(s).Replace(" - ", " – ");
        return s.Length > 90 ? s[..90].TrimEnd() : s;
    }

    /// <summary>Fait prendre en compte les fichiers d'une bibliothèque (sans scanner les autres).</summary>
    public async Task<Folder> Rescan(bool tv, IDirectoryService dirs, CancellationToken ct, bool queue = true, bool recursive = true)
    {
        var root = tv ? ShowsDir : MoviesDir;
        var vf = library.GetVirtualFolders().FirstOrDefault(v => v.Locations.Any(l => string.Equals(Path.GetFullPath(l).TrimEnd('/'), Path.GetFullPath(root).TrimEnd('/'), StringComparison.Ordinal)))
            ?? throw new InvalidOperationException("Bibliothèque TorrServer introuvable : cliquez sur « Créer les bibliothèques » dans la configuration.");
        if (library.GetItemById(Guid.Parse(vf.ItemId)) is not Folder folder) throw new InvalidOperationException("Bibliothèque TorrServer introuvable.");
        await folder.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(dirs), recursive, false, ct);
        if (queue) providers.QueueRefresh(folder.Id, new MetadataRefreshOptions(new DirectoryService(files)) { MetadataRefreshMode = MetadataRefreshMode.Default, ImageRefreshMode = MetadataRefreshMode.Default }, RefreshPriority.High);
        return folder;
    }

    /// <summary>Film de la bibliothèque TorrServer par identifiant TMDB (avec plusieurs versions, son chemin est celui d'une des versions).</summary>
    public BaseItem? FindMovie(int tmdbId) =>
        library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie],
            HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = tmdbId.ToString() },
            Recursive = true,
        }).FirstOrDefault(i => i.Path is not null && i.Path.StartsWith(MoviesDir, StringComparison.Ordinal));

    /// <summary>Élément Jellyfin d'un fichier .strm, y compris une version secondaire (film ou épisode).</summary>
    public BaseItem? FindByStrm(string path)
    {
        if (library.FindByPath(path, false) is { } direct) return direct;
        var name = Path.GetFileNameWithoutExtension(path);
        var i = name.LastIndexOf(" - ", StringComparison.Ordinal);
        Video? primary = null;
        if (i > 0) primary = library.FindByPath(Path.Combine(Path.GetDirectoryName(path)!, name[..i] + " - " + AutoLabel + ".strm"), false) as Video;
        if (primary is null)
        {
            var m = Regex.Match(path, @"\[tmdbid-(\d+)\]");
            if (m.Success && path.StartsWith(MoviesDir, StringComparison.Ordinal)) primary = FindMovie(int.Parse(m.Groups[1].Value)) as Video;
        }
        if (primary is null) return null;
        return primary.Path == path ? primary : primary.GetAllVersions().FirstOrDefault(x => x?.Path == path);
    }

    /// <summary>Attend qu'un fichier / dossier apparaisse comme élément Jellyfin.</summary>
    public async Task<BaseItem> WaitItem(string path, bool isFolder, bool tv, IDirectoryService dirs, CancellationToken ct)
    {
        BaseItem? Find() => isFolder ? library.FindByPath(path, true) : FindByStrm(path);
        var item = Find();
        if (item is null)
        {
            await Rescan(tv, dirs, ct, queue: false, recursive: false);   // crée le film / la série, sans tout rescanner
            for (var i = 0; i < 40 && (item = Find()) is null; i++)
            {
                if (i == 3) await Rescan(tv, dirs, ct, queue: true, recursive: false);
                await Task.Delay(500, ct);
            }
        }
        if (item is null) throw new InvalidOperationException("Jellyfin n'a pas encore pris en compte ce titre, réessayez dans un instant.");
        RefreshMetadata(item);
        if (item is Folder series)
        {
            // saisons + épisodes de cette série uniquement, puis métadonnées ; on attend au plus 8 s
            var task = Task.Run(async () =>
            {
                await series.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(new DirectoryService(files)), true, false, CancellationToken.None);
                providers.QueueRefresh(series.Id, new MetadataRefreshOptions(new DirectoryService(files)) { MetadataRefreshMode = MetadataRefreshMode.Default, ImageRefreshMode = MetadataRefreshMode.Default }, RefreshPriority.High);
            }, CancellationToken.None);
            await Task.WhenAny(task, Task.Delay(8000, ct));
        }
        return item;
    }

    /// <summary>Infos TMDB (titre, affiche, résumé…) : si elles manquent ou si le nom est celui du dossier, on les (re)demande.</summary>
    public void RefreshMetadata(BaseItem item)
    {
        var broken = item.Name.Contains("[tmdbid-", StringComparison.Ordinal) || item.Name.Contains(AutoLabel, StringComparison.Ordinal);
        if (!broken && !string.IsNullOrEmpty(item.Overview) && item.HasImage(MediaBrowser.Model.Entities.ImageType.Primary)) return;
        providers.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(files))
        {
            MetadataRefreshMode = broken ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.Default,
            ReplaceAllMetadata = broken,
        }, RefreshPriority.High);
    }

    /// <summary>
    /// Après l'écriture de nouvelles versions d'un film : Jellyfin relit le dossier et met à jour le menu « Version »
    /// (actualisation de l'élément seul, sans analyse vidéo : pas d'ouverture de torrents).
    /// </summary>
    public Task ApplyMovieVersions(int tmdbId, CancellationToken ct)
    {
        // même appel que « Actualiser la bibliothèque » de Jellyfin : file d'attente prioritaire sur la bibliothèque Films
        var folder = LibraryFolder(false);
        providers.QueueRefresh(folder.Id, new MetadataRefreshOptions(new DirectoryService(files))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.None,
            IsAutomated = false,
        }, RefreshPriority.High);
        return Task.CompletedTask;
    }

    private Folder LibraryFolder(bool tv)
    {
        var root = tv ? ShowsDir : MoviesDir;
        var vf = library.GetVirtualFolders().FirstOrDefault(v => v.Locations.Any(l => string.Equals(Path.GetFullPath(l).TrimEnd('/'), Path.GetFullPath(root).TrimEnd('/'), StringComparison.Ordinal)))
            ?? throw new InvalidOperationException("Bibliothèque TorrServer introuvable : cliquez sur « Créer les bibliothèques » dans la configuration.");
        return library.GetItemById(Guid.Parse(vf.ItemId)) as Folder ?? throw new InvalidOperationException("Bibliothèque TorrServer introuvable.");
    }

    /// <summary>Fait prendre en compte les nouvelles versions d'un dossier (saison ou film).</summary>
    public async Task RescanFolderOf(BaseItem item, CancellationToken ct)
    {
        var dirs = new DirectoryService(files);
        if (item.GetParent() is Folder parent) await parent.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(dirs), false, false, ct);
    }

    private static void WriteIfChanged(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public async Task<CatalogItem> Add(string type, int tmdbId, IReadOnlyCollection<int>? seasons, CancellationToken ct) => await Add(type, tmdbId, seasons, true, ct);

    public async Task<CatalogItem> Add(string type, int tmdbId, IReadOnlyCollection<int>? seasons, bool scan, CancellationToken ct)
    {
        if (type is not ("movie" or "tv")) throw new ArgumentException("Type invalide.");
        await _gate.WaitAsync(ct);
        try
        {
            var item = await Write(type, tmdbId, seasons, ct);
            var catalog = LoadCatalog();
            catalog.RemoveAll(x => x.Type == type && x.TmdbId == tmdbId);
            catalog.Add(item);
            SaveCatalog(catalog);
            await EnsureLibraries();
            if (scan) library.QueueLibraryScan();
            return item;
        }
        finally { _gate.Release(); }
    }

    private async Task<CatalogItem> Write(string type, int id, IReadOnlyCollection<int>? seasons, CancellationToken ct, int maxEpisodes = 0)
    {
        var d = await clients.Tmdb($"{type}/{id}", ct);
        var title = Clients.Text(d, type == "movie" ? "title" : "name");
        var date = Clients.Text(d, type == "movie" ? "release_date" : "first_air_date");
        int.TryParse(date.Length >= 4 ? date[..4] : "0", out var year);
        var label = year > 0 ? $"{Safe(title)} ({year})" : Safe(title);
        var poster = Clients.Text(d, "poster_path");
        var item = new CatalogItem { Type = type, TmdbId = id, Title = title, Year = year, Poster = poster, Seasons = seasons?.OrderBy(x => x).ToList() ?? [] };
        if (type == "movie")
        {
            var folder = $"{label} [tmdbid-{id}]";
            var dir = Path.Combine(MoviesDir, folder);
            WriteIfChanged(Path.Combine(dir, $"{folder} - {AutoLabel}.strm"), StreamUrl($"movie/{id}"));
            DeleteIfExists(Path.Combine(dir, label + ".strm"));   // ancien nommage
            item.Episodes = 1;
            return item;
        }
        var show = Path.Combine(ShowsDir, $"{label} [tmdbid-{id}]");
        var today = DateTime.UtcNow.Date;
        var count = 0;
        // plafond (catalogue automatique) : saisons entières depuis la 1re, au moins une
        if (maxEpisodes > 0 && item.Seasons.Count == 0)
        {
            var chosen = new List<int>(); var total = 0;
            foreach (var s in d.GetProperty("seasons").EnumerateArray().OrderBy(x => Clients.Int(x, "season_number")))
            {
                var sn = Clients.Int(s, "season_number"); if (sn <= 0) continue;
                total += Clients.Int(s, "episode_count");
                if (chosen.Count == 0 && total > maxEpisodes) throw new TooLongException(title);
                if (chosen.Count > 0 && total > maxEpisodes) { item.Seasons = chosen; break; }
                chosen.Add(sn);
            }
        }
        var wanted = d.GetProperty("seasons").EnumerateArray().Select(x => Clients.Int(x, "season_number"))
            .Where(sn => sn > 0 && (item.Seasons.Count == 0 || item.Seasons.Contains(sn))).ToList();
        using var gate = new SemaphoreSlim(6);
        var seasonsJson = await Task.WhenAll(wanted.Select(async sn =>
        {
            await gate.WaitAsync(ct);
            try { return (sn, json: (System.Text.Json.JsonElement?)await clients.Tmdb($"tv/{id}/season/{sn}", ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (sn, json: (System.Text.Json.JsonElement?)null); }
            finally { gate.Release(); }
        }));
        foreach (var (sn, json) in seasonsJson)
        {
            if (json is not { } season || !season.TryGetProperty("episodes", out var eps)) continue;
            foreach (var ep in eps.EnumerateArray())
            {
                var en = Clients.Int(ep, "episode_number");
                if (en <= 0) continue;
                var air = Clients.Text(ep, "air_date");
                if (DateTime.TryParse(air, out var when) && when.Date > today) continue;   // pas encore diffusé
                var name = $"{Safe(title)} S{sn:00}E{en:00}";
                WriteIfChanged(Path.Combine(show, $"Season {sn:00}", $"{name} - {AutoLabel}.strm"), StreamUrl($"tv/{id}/{sn}/{en}"));
                DeleteIfExists(Path.Combine(show, $"Season {sn:00}", name + ".strm"));   // ancien nommage
                count++;
            }
        }
        item.Episodes = count;
        return item;
    }

    public async Task Remove(string type, int tmdbId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var catalog = LoadCatalog();
            if (catalog.Any(x => x.Type == type && x.TmdbId == tmdbId && x.Auto)) Hide(type + tmdbId);
            catalog.RemoveAll(x => x.Type == type && x.TmdbId == tmdbId);
            SaveCatalog(catalog);
            var baseDir = type == "movie" ? MoviesDir : ShowsDir;
            if (Directory.Exists(baseDir))
                foreach (var dir in Directory.GetDirectories(baseDir, $"*[[]tmdbid-{tmdbId}]"))
                    Directory.Delete(dir, true);
            library.QueueLibraryScan();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Réécrit les fichiers du catalogue : nouveaux épisodes des séries et nouvelle adresse de lecture.</summary>
    public async Task<int> RefreshShows(IProgress<double>? progress, CancellationToken ct, ISet<string>? skip = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var catalog = LoadCatalog();
            var added = 0; var i = 0;
            foreach (var s in catalog)
            {
                if (skip is not null && skip.Contains(s.Type + s.TmdbId)) { i++; continue; }
                try
                {
                    var updated = await Write(s.Type, s.TmdbId, s.Seasons, ct);
                    added += Math.Max(0, updated.Episodes - s.Episodes);
                    s.Episodes = updated.Episodes;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Actualisation de « {T} » : {M}", s.Title, ex.Message); }
                progress?.Report(100.0 * ++i / Math.Max(1, catalog.Count));
            }
            SaveCatalog(catalog);
            if (added > 0) library.QueueLibraryScan();
            return added;
        }
        finally { _gate.Release(); }
    }

    // ---------- lecture depuis l'application ----------
    private readonly ConcurrentDictionary<Guid, string> _probed = new();

    /// <summary>Garantit que le film / l'épisode existe dans Jellyfin (fichier .strm + élément), sans scanner les autres bibliothèques.</summary>
    public async Task<BaseItem> EnsureItem(MediaRef m, IDirectoryService dirs, CancellationToken ct)
    {
        var path = StrmPath(m);
        if (path is null || !File.Exists(path))
        {
            await Add(m.Kind, m.TmdbId, null, false, ct);
            path = StrmPath(m) ?? throw new InvalidOperationException("Titre introuvable.");
            if (!File.Exists(path)) throw new InvalidOperationException("Cet épisode n'est pas encore diffusé.");
        }
        if (library.FindByPath(path, false) is { } existing) return existing;
        var root = m.IsTv ? ShowsDir : MoviesDir;
        var vf = library.GetVirtualFolders().FirstOrDefault(v => v.Locations.Any(l => string.Equals(Path.GetFullPath(l).TrimEnd('/'), Path.GetFullPath(root).TrimEnd('/'), StringComparison.Ordinal)))
            ?? throw new InvalidOperationException("Bibliothèque TorrServer introuvable : cliquez sur « Créer les bibliothèques » dans la configuration.");
        if (library.GetItemById(Guid.Parse(vf.ItemId)) is not Folder folder) throw new InvalidOperationException("Bibliothèque TorrServer introuvable.");
        await folder.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(dirs), true, false, ct);
        var queued = false;
        for (var i = 0; i < 60; i++)
        {
            if (library.FindByPath(path, false) is { } item) return item;
            if (!queued && i >= 2)
            {
                providers.QueueRefresh(folder.Id, new MetadataRefreshOptions(new DirectoryService(files)) { MetadataRefreshMode = MetadataRefreshMode.Default, ImageRefreshMode = MetadataRefreshMode.Default }, RefreshPriority.High);
                queued = true;
            }
            await Task.Delay(1000, ct);
        }
        throw new InvalidOperationException("Jellyfin n'a pas encore pris en compte ce titre, réessayez dans un instant.");
    }

    /// <summary>Analyse (ffprobe) l'élément si la source a changé depuis la dernière analyse.</summary>
    public async Task ProbeIfChanged(BaseItem item, Entry source, IDirectoryService dirs, CancellationToken ct)
    {
        var sig = source.Hash + ":" + source.FileId;
        if ((item.RunTimeTicks ?? 0) > 0 && _probed.TryGetValue(item.Id, out var last) && last == sig) return;
        await item.RefreshMetadata(new MetadataRefreshOptions(dirs) { MetadataRefreshMode = MetadataRefreshMode.Default, ImageRefreshMode = MetadataRefreshMode.Default, EnableRemoteContentProbe = true }, ct);
        _probed[item.Id] = sig;
    }

    // ---------- catalogue automatique ----------
    private int _syncing;
    public bool Syncing => _syncing == 1;
    public int SyncDone, SyncTotal;
    public string SyncMessage = "";

    private static readonly string[] AutoLists =
    [
        "movie|movie/popular?page={0}", "movie|movie/top_rated?page={0}",
        "tv|tv/popular?page={0}", "tv|tv/top_rated?page={0}",
        "tv|discover/tv?with_genres=16&with_original_language=ja&sort_by=popularity.desc&page={0}",
        "tv|discover/tv?with_genres=16&with_original_language=ja&sort_by=vote_average.desc&vote_count.gte=300&page={0}",
    ];

    /// <summary>Ajoute à la bibliothèque les titres des listes TMDB, puis met à jour les séries.</summary>
    public async Task SyncAll(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _syncing, 1) == 1) return;
        try
        {
            var addedKeys = new HashSet<string>();
            if (Clients.Config.AutoCatalog) addedKeys = await SyncNew(ct);
            SyncMessage = "Mise à jour des séries…";
            await RefreshShows(null, ct, addedKeys);
            SyncMessage = $"Terminé : {addedKeys.Count} titre(s) ajouté(s).";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { SyncMessage = "Échec : " + ex.Message; log.LogWarning("Catalogue automatique : {M}", ex.Message); }
        finally { Interlocked.Exchange(ref _syncing, 0); }
    }

    private async Task<HashSet<string>> SyncNew(CancellationToken ct)
    {
        SyncMessage = "Lecture des listes TMDB…"; SyncDone = 0; SyncTotal = 0;
        var pages = Math.Clamp(Clients.Config.AutoPages, 1, 500);
        var known = LoadCatalog().Select(x => x.Type + x.TmdbId).ToHashSet();
        var hidden = LoadHidden();
        var wanted = new List<(string Type, int Id)>();
        var seen = new HashSet<string>();
        for (var page = 1; page <= pages; page++)
            foreach (var spec in AutoLists)
            {
                var parts = spec.Split('|');
                try
                {
                    var json = await clients.Tmdb(string.Format(parts[1], page), ct);
                    foreach (var x in json.GetProperty("results").EnumerateArray())
                    {
                        var id = Clients.Int(x, "id"); var key = parts[0] + id;
                        if (id == 0 || Clients.Text(x, "poster_path").Length == 0 || known.Contains(key) || hidden.Contains(key) || !seen.Add(key)) continue;
                        wanted.Add((parts[0], id));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug("Liste {S} page {P} : {M}", parts[1], page, ex.Message); }
            }
        SyncTotal = wanted.Count;
        log.LogInformation("Catalogue automatique : {N} nouveaux titres à ajouter", wanted.Count);
        var added = new ConcurrentBag<CatalogItem>();
        var skipped = new ConcurrentBag<string>();
        SyncMessage = "Ajout des titres…";
        await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (w, t) =>
        {
            try { var item = await Write(w.Type, w.Id, null, t, w.Type == "tv" ? Clients.Config.AutoMaxEpisodes : 0); item.Auto = true; added.Add(item); }
            catch (TooLongException) { skipped.Add(w.Type + w.Id); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug("Ajout {T}{I} : {M}", w.Type, w.Id, ex.Message); }
            Interlocked.Increment(ref SyncDone);
        });
        if (!skipped.IsEmpty) { var h = LoadHidden(); foreach (var k in skipped) h.Add(k); Directory.CreateDirectory(Path.GetDirectoryName(HiddenFile)!); File.WriteAllText(HiddenFile, JsonSerializer.Serialize(h)); }
        await _gate.WaitAsync(ct);
        try
        {
            var catalog = LoadCatalog();
            foreach (var item in added) { catalog.RemoveAll(x => x.Type == item.Type && x.TmdbId == item.TmdbId); catalog.Add(item); }
            SaveCatalog(catalog);
        }
        finally { _gate.Release(); }
        if (!added.IsEmpty) { await EnsureLibraries(); library.QueueLibraryScan(); }
        return added.Select(x => x.Type + x.TmdbId).ToHashSet();
    }

    /// <summary>Crée les deux bibliothèques Jellyfin si elles n'existent pas.</summary>
    public async Task<string> EnsureLibraries()
    {
        EnsureKey();
        Directory.CreateDirectory(MoviesDir); Directory.CreateDirectory(ShowsDir);
        var existing = library.GetVirtualFolders().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<string>();
        async Task Make(string name, CollectionTypeOptions type, string path)
        {
            if (existing.Contains(name)) return;
            var options = new LibraryOptions
            {
                EnableRealtimeMonitor = false,
                EnableChapterImageExtraction = false,
                ExtractChapterImagesDuringLibraryScan = false,
                EnableTrickplayImageExtraction = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                SaveLocalMetadata = false,
                PreferredMetadataLanguage = Clients.Config.MetadataLanguage[..2],
                MetadataCountryCode = Clients.Config.MetadataLanguage.Length >= 5 ? Clients.Config.MetadataLanguage[3..5] : "FR",
                PathInfos = [new MediaPathInfo(path)],
            };
            await library.AddVirtualFolder(name, type, options, true);
            created.Add(name);
        }
        await Make(MoviesName, CollectionTypeOptions.movies, MoviesDir);
        await Make(ShowsName, CollectionTypeOptions.tvshows, ShowsDir);
        return created.Count == 0 ? "Bibliothèques déjà présentes." : "Bibliothèques créées : " + string.Join(", ", created);
    }
}
