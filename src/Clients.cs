using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TorrServer;

public sealed record Release(string Title, string Link, int Seeders, long Size, string Indexer);
public sealed record TorrentFile(int Id, string Path, long Length);
public sealed record TorrentStatus(string Hash, [property: JsonPropertyName("file_stats")] List<TorrentFile>? Files);

/// <summary>Accès HTTP à TMDB, Prowlarr et TorrServer.</summary>
public sealed class Clients(IHttpClientFactory http)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    public static PluginConfiguration Config => Plugin.Instance!.Configuration;

    // ---------- utilitaires JSON ----------
    public static string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public static int Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    public static long Long(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public static Uri Endpoint(string value, string path)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException($"Adresse invalide : « {value} » (http ou https requis).");
        return new Uri(uri, path);
    }

    private async Task<JsonElement> Send(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.CreateClient("ts-api").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{request.RequestUri?.Host}:{request.RequestUri?.Port} a répondu HTTP {(int)response.StatusCode}.");
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    // ---------- TMDB ----------
    private static string TmdbToken()
    {
        var t = Config.TmdbReadAccessToken.Trim();
        return t.Length > 0 ? t : (Environment.GetEnvironmentVariable("TMDB_API_LECTURE") ?? Environment.GetEnvironmentVariable("TMBD_API_LECTURE") ?? "").Trim();
    }

    // Réponses TMDB gardées en mémoire 1 h : page Découvrir instantanée, moins d'appels à chaque recherche de sources.
    private static readonly TimeSpan TmdbTtl = TimeSpan.FromHours(1);
    private const int TmdbMaxEntries = 4000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, JsonElement Json)> _tmdb = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<JsonElement>>> _tmdbInflight = new();

    public async Task<JsonElement> Tmdb(string path, CancellationToken ct, string? language = null)
    {
        var url = TmdbUrl(path, language);
        if (_tmdb.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < TmdbTtl) return hit.Json;
        var lazy = _tmdbInflight.GetOrAdd(url, u => new Lazy<Task<JsonElement>>(async () =>
        {
            var json = await Send(TmdbRequest(u), CancellationToken.None);   // partagée : ne dépend pas d'un seul appelant
            if (_tmdb.Count >= TmdbMaxEntries) _tmdb.Clear();
            _tmdb[u] = (DateTime.UtcNow, json);
            return json;
        }));
        try { return await lazy.Value.WaitAsync(ct); }
        finally { if (lazy.Value.IsCompleted) _tmdbInflight.TryRemove(url, out _); }
    }

    /// <summary>
    /// Animes que TMDB range en une seule saison géante (Re:Zero = 85 épisodes) : saisons du groupe d'épisodes « Seasons » de TMDB,
    /// saison → épisodes (le numéro d'épisode d'origine est gardé : c'est la numérotation absolue). Null si la série est normale.
    /// </summary>
    public async Task<Dictionary<int, List<JsonElement>>?> SeasonMap(int id, CancellationToken ct)
    {
        try
        {
            var d = await Tmdb($"tv/{id}", ct);
            if (!d.TryGetProperty("seasons", out var ss) || ss.EnumerateArray().Count(x => Int(x, "season_number") > 0 && Int(x, "episode_count") > 0) != 1) return null;
            var groups = await Tmdb($"tv/{id}/episode_groups", ct);
            if (!groups.TryGetProperty("results", out var rs)) return null;
            var g = rs.EnumerateArray().Where(x => Int(x, "type") == 6 && Int(x, "group_count") is >= 2 and <= 12)
                .OrderByDescending(x => Text(x, "name").Equals("Seasons", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (g.ValueKind != JsonValueKind.Object) return null;
            var full = await Tmdb($"tv/episode_group/{Text(g, "id")}", ct);
            var map = new Dictionary<int, List<JsonElement>>();
            foreach (var grp in full.GetProperty("groups").EnumerateArray())
            {
                var sn = Int(grp, "order");
                if (sn > 0 && Text(grp, "name").StartsWith("Season", StringComparison.OrdinalIgnoreCase)) map[sn] = grp.GetProperty("episodes").EnumerateArray().ToList();
            }
            return map.Count >= 2 ? map : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static string TmdbUrl(string path, string? language)
    {
        var token = TmdbToken();
        var key = Config.TmdbApiKey.Trim();
        if (token.Length == 0 && key.Length == 0) throw new InvalidOperationException("Aucune clé TMDB configurée.");
        var url = (Environment.GetEnvironmentVariable("TMDB_BASE_URL") ?? "https://api.themoviedb.org/3").TrimEnd('/') + "/" + path + (path.Contains('?') ? "&" : "?") + "language=" + (language ?? Config.MetadataLanguage);
        return token.Length == 0 ? url + "&api_key=" + Uri.EscapeDataString(key) : url;
    }

    private static HttpRequestMessage TmdbRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = TmdbToken();
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    // ---------- Prowlarr ----------
    // Cache des recherches (mémoire + disque, 6 h) et partage des recherches en cours :
    // un indexeur lent (10 à 45 s par requête) ne doit être interrogé qu'une fois par titre.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, List<Release> Items)> SearchCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<List<Release>>>> Inflight = new();
    private static readonly TimeSpan SearchTtl = TimeSpan.FromHours(6);
    private static bool _cacheLoaded;
    private static readonly object CacheLock = new();
    private static string CachePath => System.IO.Path.Combine(Plugin.Instance!.DataFolderPath, "search-cache.json");

    private static void LoadCache()
    {
        if (_cacheLoaded) return;
        lock (CacheLock)
        {
            if (_cacheLoaded) return;
            try
            {
                if (File.Exists(CachePath))
                    foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, CachedSearch>>(File.ReadAllText(CachePath)) ?? [])
                        if (DateTime.UtcNow - kv.Value.At < SearchTtl) SearchCache[kv.Key] = (kv.Value.At, kv.Value.Items);
            }
            catch { }
            _cacheLoaded = true;
        }
    }

    private static int _savePending;

    /// <summary>Écrit le cache au plus une fois toutes les 10 s (le fichier fait plusieurs Mo).</summary>
    private static void SaveCache()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Interlocked.Exchange(ref _savePending, 0);
            try
            {
                foreach (var old in SearchCache.Where(x => DateTime.UtcNow - x.Value.At >= SearchTtl).Select(x => x.Key).ToList()) SearchCache.TryRemove(old, out _);
                var keep = SearchCache.ToDictionary(x => x.Key, x => new CachedSearch(x.Value.At, x.Value.Items));
                var json = JsonSerializer.Serialize(keep);
                lock (CacheLock) { File.WriteAllText(CachePath + ".tmp", json); File.Move(CachePath + ".tmp", CachePath, true); }
            }
            catch { /* ce n'est qu'un cache */ }
        });
    }

    public sealed record CachedSearch(DateTime At, List<Release> Items);

    private async Task<List<Release>> Cached(string key, Func<Task<List<Release>>> fetch)
    {
        LoadCache();
        if (SearchCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < SearchTtl) return hit.Items;
        var lazy = Inflight.GetOrAdd(key, _ => new Lazy<Task<List<Release>>>(async () =>
        {
            var found = await fetch();
            if (found.Count > 0) { SearchCache[key] = (DateTime.UtcNow, found); SaveCache(); }
            return found;
        }));
        try { return await lazy.Value; }
        finally { if (lazy.Value.IsCompleted) Inflight.TryRemove(key, out _); }
    }

    public Task<List<Release>> Search(string query, int category, CancellationToken ct) =>
        Cached(category + "|" + query.ToLowerInvariant(), () => SearchUncached(query, category, CancellationToken.None));

    /// <summary>Recherche précise par identifiant TMDB (une seule requête au lieu de plusieurs recherches textuelles).</summary>
    public Task<List<Release>> SearchId(string kind, int tmdbId, int season = 0, int episode = 0)
    {
        var q = "{TmdbId:" + tmdbId + "}" + (season > 0 ? "{Season:" + season + "}" : "") + (episode > 0 ? "{Episode:" + episode + "}" : "");
        var type = kind == "movie" ? "movie" : "tvsearch";
        return Cached("id|" + type + "|" + q, () => Prowlarr("type=" + type + "&limit=100&query=" + Uri.EscapeDataString(q) + "&categories=" + (kind == "movie" ? 2000 : 5000), CancellationToken.None));
    }

    private async Task<List<Release>> Prowlarr(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Config.ProwlarrApiKey)) throw new InvalidOperationException("Clé API Prowlarr manquante.");
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(Config.ProwlarrUrl, "api/v1/search?" + query));
        request.Headers.Add("X-Api-Key", Config.ProwlarrApiKey);
        var json = await Send(request, ct);
        if (json.ValueKind != JsonValueKind.Array) return [];
        return json.EnumerateArray().Where(x => Text(x, "protocol").Equals("torrent", StringComparison.OrdinalIgnoreCase)).Select(x =>
        {
            var link = Text(x, "magnetUrl");
            if (link.Length == 0) link = Text(x, "downloadUrl");
            return new Release(Text(x, "title"), link, Int(x, "seeders"), Long(x, "size"), Text(x, "indexer"));
        }).Where(x => x.Link.Length > 0 && x.Title.Length > 0).ToList();
    }

    private async Task<List<Release>> SearchUncached(string query, int category, CancellationToken ct)
    {
        var q = "type=search&limit=100&query=" + Uri.EscapeDataString(query);
        var results = await Prowlarr(q + "&categories=" + category, ct);
        return results.Count > 0 ? results : await Prowlarr(q, ct);
    }

    // ---------- TorrServer ----------
    public HttpRequestMessage Torr(HttpMethod method, string pathOrUrl)
    {
        var uri = Uri.IsWellFormedUriString(pathOrUrl, UriKind.Absolute) ? new Uri(pathOrUrl) : Endpoint(Config.TorrServerUrl, pathOrUrl);
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(Config.TorrServerUsername))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Config.TorrServerUsername + ":" + Config.TorrServerPassword)));
        return request;
    }

    public HttpClient StreamClient => http.CreateClient("ts-stream");

    public async Task<TorrentStatus> AddTorrent(Release release, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        ct = cts.Token;
        TorrentStatus status;
        if (release.Link.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            using var request = Torr(HttpMethod.Post, "torrents");
            request.Content = JsonContent.Create(new { action = "add", link = release.Link, title = release.Title, save_to_db = true });
            status = Parse(await Send(request, ct));
        }
        else
        {
            byte[] bytes;
            try { bytes = await DownloadTorrent(release.Link, ct); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("MAGNET:", StringComparison.Ordinal))
            {
                return await AddTorrent(release with { Link = ex.Message[7..] }, timeout, ct);
            }
            using var request = Torr(HttpMethod.Post, "torrent/upload");
            var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "source.torrent" }, { new StringContent("true"), "save" }, { new StringContent(release.Title), "title" } };
            request.Content = form;
            status = Parse(await Send(request, ct));
        }
        if (string.IsNullOrEmpty(status.Hash) || status.Hash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("TorrServer n'a pas retourné de hash valide.");
        try
        {
            while (status.Files is not { Count: > 0 })
            {
                await Task.Delay(1000, ct);
                status = await GetTorrent(status.Hash, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // pas de pair ni de métadonnées : inutile de le laisser dans TorrServer
            _ = RemoveTorrent(status.Hash).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            throw;
        }
        return status;
    }

    public async Task<TorrentStatus> GetTorrent(string hash, CancellationToken ct)
    {
        using var request = Torr(HttpMethod.Post, "torrents");
        request.Content = JsonContent.Create(new { action = "get", hash });
        return Parse(await Send(request, ct));
    }

    public async Task RemoveTorrent(string hash)
    {
        using var request = Torr(HttpMethod.Post, "torrents");
        request.Content = JsonContent.Create(new { action = "rem", hash });
        using var response = await http.CreateClient("ts-api").SendAsync(request);
    }

    public string StreamUrl(string hash, int index, string path) =>
        Endpoint(Config.TorrServerUrl, "stream/" + Uri.EscapeDataString(System.IO.Path.GetFileName(path)) + "?link=" + Uri.EscapeDataString(hash) + "&index=" + index + "&play").AbsoluteUri;

    private async Task<byte[]> DownloadTorrent(string link, CancellationToken ct)
    {
        var url = new Uri(link, UriKind.Absolute);
        var prowlarr = Endpoint(Config.ProwlarrUrl, "");
        for (var hop = 0; hop < 6; hop++)
        {
            if (url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo)) throw new InvalidOperationException("Lien torrent invalide.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (url.GetLeftPart(UriPartial.Authority) == prowlarr.GetLeftPart(UriPartial.Authority)) request.Headers.Add("X-Api-Key", Config.ProwlarrApiKey);
            using var response = await http.CreateClient("ts-api").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } loc)
            {
                if (loc.Scheme == "magnet") throw new InvalidOperationException("MAGNET:" + loc.OriginalString);
                url = new Uri(url, loc); continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Téléchargement du .torrent : HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > 10 * 1024 * 1024) throw new InvalidOperationException("Fichier .torrent trop volumineux.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        throw new InvalidOperationException("Trop de redirections.");
    }

    private static TorrentStatus Parse(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) e = e.GetArrayLength() == 1 ? e[0] : throw new InvalidOperationException("TorrServer a refusé le torrent.");
        return e.Deserialize<TorrentStatus>(JsonOpts) ?? throw new InvalidOperationException("Réponse TorrServer invalide.");
    }

    // ---------- test des connexions ----------
    public async Task<Dictionary<string, string>> TestAll(CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        async Task Try(string name, Func<Task<string>> f)
        {
            try { result[name] = await f(); } catch (Exception ex) { result[name] = "ÉCHEC : " + ex.Message; }
        }
        await Try("TMDB", async () => { await Send(TmdbRequest(TmdbUrl("configuration", null)), ct); return "OK"; });
        await Try("Prowlarr", async () =>
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, Endpoint(Config.ProwlarrUrl, "api/v1/indexer"));
            r.Headers.Add("X-Api-Key", Config.ProwlarrApiKey);
            var j = await Send(r, ct);
            var n = j.EnumerateArray().Count(x => x.TryGetProperty("enable", out var en) && en.ValueKind == JsonValueKind.True);
            return n > 0 ? $"OK ({n} indexeur(s) actif(s))" : "Connecté, mais aucun indexeur actif : ajoutez-en dans Prowlarr.";
        });
        await Try("TorrServer", async () =>
        {
            using var r = Torr(HttpMethod.Get, "echo");
            using var resp = await http.CreateClient("ts-api").SendAsync(r, ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}");
            return "OK (" + (await resp.Content.ReadAsStringAsync(ct)).Trim() + ")";
        });
        return result;
    }

    /// <summary>Indexeurs configurés dans Prowlarr (nom, actif).</summary>
    public async Task<List<(string Name, bool Enabled)>> Indexers(CancellationToken ct)
    {
        using var r = new HttpRequestMessage(HttpMethod.Get, Endpoint(Config.ProwlarrUrl, "api/v1/indexer"));
        r.Headers.Add("X-Api-Key", Config.ProwlarrApiKey);
        var j = await Send(r, ct);
        return j.EnumerateArray().Select(x => (Text(x, "name"), x.TryGetProperty("enable", out var en) && en.ValueKind == JsonValueKind.True))
            .Where(x => x.Item1.Length > 0).OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
