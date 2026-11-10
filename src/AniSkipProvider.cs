using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>
/// « Passer l'intro / le générique » des animes : débuts et fins viennent d'AniSkip (base communautaire, par identifiant MyAnimeList).
/// TMDB → MyAnimeList passe par la liste communautaire Fribb/anime-lists ; le nombre d'épisodes de chaque partie vient d'AniList.
/// </summary>
public sealed class AniSkipProvider(ILibraryManager library, Clients clients, IHttpClientFactory http, ILogger<AniSkipProvider> log) : IMediaSegmentProvider
{
    public string Name => "AniSkip (TorrServer)";

    private sealed record Entry(int Mal, int Season);
    private static string ListFile => Path.Combine(Plugin.Instance!.DataFolderPath, "anime-list.json");
    private static string CacheFile => Path.Combine(Plugin.Instance!.DataFolderPath, "aniskip.json");
    private Dictionary<int, List<Entry>>? _list;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly SemaphoreSlim _net = new(2);   // AniList : requêtes limitées
    private readonly ConcurrentDictionary<int, int> _counts = new();
    private readonly ConcurrentDictionary<string, List<double[]>?> _found = new();   // "mal:ep" → [type(0 op,1 ed,2 recap), début, fin]
    private bool _cacheLoaded;

    public ValueTask<bool> Supports(BaseItem item) =>
        ValueTask.FromResult(item is Episode && (item.Path ?? "").StartsWith(LibraryService.Root, StringComparison.Ordinal));

    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<Dictionary<int, List<Entry>>> List(CancellationToken ct)
    {
        if (_list is not null) return _list;
        await _gate.WaitAsync(ct);
        try
        {
            if (_list is not null) return _list;
            var f = new FileInfo(ListFile);
            if (!f.Exists || DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromDays(7))
            {
                try
                {
                    var bytes = await http.CreateClient().GetByteArrayAsync("https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-full.json", ct);
                    await File.WriteAllBytesAsync(ListFile + ".tmp", bytes, ct);
                    File.Move(ListFile + ".tmp", ListFile, true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Liste des animes indisponible : {M}", ex.Message); }
            }
            var map = new Dictionary<int, List<Entry>>();
            if (File.Exists(ListFile))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(ListFile, ct));
                foreach (var x in doc.RootElement.EnumerateArray())
                {
                    var type = Clients.Text(x, "type");
                    var mal = Clients.Int(x, "mal_id");
                    if (type is not ("TV" or "ONA") || mal <= 0) continue;
                    if (!x.TryGetProperty("themoviedb_id", out var t)) continue;
                    var tv = t.ValueKind == JsonValueKind.Object ? Clients.Int(t, "tv") : t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                    if (tv <= 0) continue;
                    var season = 1;
                    if (x.TryGetProperty("season", out var s) && s.ValueKind == JsonValueKind.Object && Clients.Int(s, "tmdb") > 0) season = Clients.Int(s, "tmdb");
                    if (!map.TryGetValue(tv, out var l)) map[tv] = l = [];
                    l.Add(new Entry(mal, season));
                }
                foreach (var l in map.Values) l.Sort((a, b) => a.Mal.CompareTo(b.Mal));
            }
            return _list = map;
        }
        finally { _gate.Release(); }
    }

    private void LoadCache()
    {
        if (_cacheLoaded) return;
        _cacheLoaded = true;
        try
        {
            if (!File.Exists(CacheFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            foreach (var c in doc.RootElement.GetProperty("counts").EnumerateObject()) _counts[int.Parse(c.Name)] = c.Value.GetInt32();
            foreach (var c in doc.RootElement.GetProperty("found").EnumerateObject())
                _found[c.Name] = c.Value.ValueKind == JsonValueKind.Null ? null : c.Value.Deserialize<List<double[]>>();
        }
        catch (Exception) { }
    }

    private void SaveCache()
    {
        try { File.WriteAllText(CacheFile, JsonSerializer.Serialize(new { counts = _counts, found = _found })); } catch (Exception) { }
    }

    private async Task<int> Count(int mal, CancellationToken ct)
    {
        if (_counts.TryGetValue(mal, out var n)) return n;
        await _net.WaitAsync(ct);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
            {
                Content = new StringContent("{\"query\":\"{ Media(idMal: " + mal + ", type: ANIME) { episodes } }\"}", System.Text.Encoding.UTF8, "application/json"),
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var r = await http.CreateClient().SendAsync(req, cts.Token);
            if (!r.IsSuccessStatusCode) return 0;
            using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
            n = j.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("Media", out var m) && m.ValueKind == JsonValueKind.Object ? Clients.Int(m, "episodes") : 0;
            if (n > 0) _counts[mal] = n;
            return n;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return 0; }
        finally { _net.Release(); }
    }

    /// <summary>
    /// Heure exacte (UTC) de diffusion du prochain épisode d'un anime, d'après AniList. TMDB ne donne que le jour : la date d'AniList
    /// doit tomber à moins de 36 h de celle de TMDB, sinon c'est une autre partie de la série et on l'ignore.
    /// </summary>
    public async Task<DateTime?> AiringAt(int tmdbId, string date, CancellationToken ct)
    {
        if (!DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var day)) return null;
        if (!(await List(ct)).TryGetValue(tmdbId, out var all)) return null;
        foreach (var e in all.OrderByDescending(x => x.Mal).Take(3))
        {
            await _net.WaitAsync(ct);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
                {
                    Content = new StringContent("{\"query\":\"{ Media(idMal: " + e.Mal + ", type: ANIME) { nextAiringEpisode { airingAt } } }\"}", System.Text.Encoding.UTF8, "application/json"),
                };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var r = await http.CreateClient().SendAsync(req, cts.Token);
                if (!r.IsSuccessStatusCode) continue;
                using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
                if (j.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("Media", out var m) && m.ValueKind == JsonValueKind.Object
                    && m.TryGetProperty("nextAiringEpisode", out var n) && n.ValueKind == JsonValueKind.Object && n.TryGetProperty("airingAt", out var a) && a.TryGetInt64(out var ts))
                {
                    var at = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
                    if (Math.Abs((at - day).TotalHours) <= 36) return at;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
            finally { _net.Release(); }
        }
        return null;
    }

    private async Task<List<double[]>?> Skip(int mal, int ep, CancellationToken ct)
    {
        var key = mal + ":" + ep;
        if (_found.TryGetValue(key, out var known)) return known;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var r = await http.CreateClient().GetAsync($"https://api.aniskip.com/v2/skip-times/{mal}/{ep}?types=op&types=ed&types=recap&types=mixed-op&types=mixed-ed&episodeLength=0", cts.Token);
            if ((int)r.StatusCode >= 500) return null;   // erreur passagère : pas mémorisée
            List<double[]>? res = null;
            if (r.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
                if (doc.RootElement.TryGetProperty("results", out var rs))
                    res = rs.EnumerateArray().Select(x =>
                    {
                        var t = Clients.Text(x, "skipType");
                        var iv = x.GetProperty("interval");
                        return new[] { t is "op" or "mixed-op" ? 0.0 : t is "ed" or "mixed-ed" ? 1.0 : 2.0, iv.GetProperty("startTime").GetDouble(), iv.GetProperty("endTime").GetDouble() };
                    }).ToList();
            }
            _found[key] = res;
            SaveCache();
            return res;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var none = Array.Empty<MediaSegmentDto>();
        try
        {
            if (library.GetItemById(request.ItemId) is not Episode { ParentIndexNumber: int season, IndexNumber: int ep } item) return none;
            var tm = System.Text.RegularExpressions.Regex.Match(item.Path ?? "", @"\[tmdbid-(\d+)\]");
            if (!tm.Success) return none;
            var id = int.Parse(tm.Groups[1].Value);
            LoadCache();
            if (!(await List(cancellationToken)).TryGetValue(id, out var all)) return none;

            // saisons redécoupées par le plugin (Re:ZERO) : retour à la numérotation absolue de TMDB (saison 1)
            var tmdbSeason = season;
            if (await clients.SeasonMap(id, cancellationToken) is { } map && map.TryGetValue(season, out var eps) && ep >= 1 && ep <= eps.Count)
            { ep = Clients.Int(eps[ep - 1], "episode_number"); tmdbSeason = 1; }

            var parts = all.Where(x => x.Season == tmdbSeason).ToList();
            if (parts.Count == 0) return none;
            int mal = 0, local = ep;
            if (parts.Count == 1) mal = parts[0].Mal;
            else
            {
                var cum = 0;
                foreach (var p in parts)
                {
                    var n = await Count(p.Mal, cancellationToken);
                    if (n <= 0) break;
                    if (ep <= cum + n) { mal = p.Mal; local = ep - cum; break; }
                    cum += n;
                }
                SaveCache();
            }
            if (mal == 0) return none;
            var found = await Skip(mal, local, cancellationToken);
            if (found is null) return none;
            return found.Select(f => new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = request.ItemId,
                Type = f[0] == 0 ? MediaSegmentType.Intro : f[0] == 1 ? MediaSegmentType.Outro : MediaSegmentType.Recap,
                StartTicks = (long)(f[1] * 10_000_000),
                EndTicks = (long)(f[2] * 10_000_000),
            }).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("AniSkip : {M}", ex.Message); return none; }
    }
}
