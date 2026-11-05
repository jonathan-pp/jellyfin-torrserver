using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrServer;

public sealed record AddRequest(string Type, int Id, int[]? Seasons);
public sealed record PinRequest(string Type, int Id, int Season, int Episode, string Link, string Title);

/// <summary>API utilisée par la page de configuration (administrateurs uniquement).</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TorrServerPlugin")]
public sealed class ApiController(Clients clients, LibraryService lib, Resolver resolver) : ControllerBase
{
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct) => Ok(await clients.TestAll(ct));

    [HttpGet("indexers")]
    public async Task<IActionResult> Indexers(CancellationToken ct) =>
        Ok((await clients.Indexers(ct)).Select(x => new { name = x.Name, enabled = x.Enabled }));

    [HttpPost("setup")]
    public async Task<IActionResult> Setup() => Ok(new { message = await lib.EnsureLibraries(), root = LibraryService.Root });

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<object>());
        var json = await clients.Tmdb("search/multi?include_adult=false&query=" + Uri.EscapeDataString(q), ct);
        var inLib = lib.LoadCatalog().Select(x => x.Type + x.TmdbId).ToHashSet();
        var list = json.GetProperty("results").EnumerateArray()
            .Where(x => Clients.Text(x, "media_type") is "movie" or "tv")
            .Take(20)
            .Select(x =>
            {
                var type = Clients.Text(x, "media_type");
                var date = Clients.Text(x, type == "movie" ? "release_date" : "first_air_date");
                var id = Clients.Int(x, "id");
                return new
                {
                    type, id,
                    title = Clients.Text(x, type == "movie" ? "title" : "name"),
                    year = date.Length >= 4 ? date[..4] : "",
                    overview = Clients.Text(x, "overview"),
                    poster = Clients.Text(x, "poster_path"),
                    inLibrary = inLib.Contains(type + id),
                };
            });
        return Ok(list);
    }

    [HttpGet("seasons/{id:int}")]
    public async Task<IActionResult> Seasons(int id, CancellationToken ct)
    {
        var d = await clients.Tmdb($"tv/{id}", ct);
        return Ok(d.GetProperty("seasons").EnumerateArray().Where(s => Clients.Int(s, "season_number") > 0)
            .Select(s => new { number = Clients.Int(s, "season_number"), episodes = Clients.Int(s, "episode_count") }));
    }

    [HttpPost("add")]
    public async Task<IActionResult> Add([FromBody] AddRequest r, CancellationToken ct)
    {
        try { return Ok(await lib.Add(r.Type, r.Id, r.Seasons, ct)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Titres ajoutés à la main (le catalogue automatique n'est pas listé : il peut compter des milliers de titres).</summary>
    [HttpGet("catalog")]
    public IActionResult Catalog() => Ok(lib.LoadCatalog().Where(x => !x.Auto).OrderBy(x => x.Title));

    [HttpGet("status")]
    public IActionResult Status()
    {
        var all = lib.LoadCatalog();
        return Ok(new
        {
            syncing = lib.Syncing, done = lib.SyncDone, total = lib.SyncTotal, message = lib.SyncMessage,
            movies = all.Count(x => x.Type == "movie"), shows = all.Count(x => x.Type == "tv"), manual = all.Count(x => !x.Auto),
        });
    }

    /// <summary>Lance en arrière-plan le remplissage du catalogue et la mise à jour des séries.</summary>
    [HttpPost("sync")]
    public IActionResult Sync()
    {
        if (!lib.Syncing) _ = Task.Run(() => lib.SyncAll(CancellationToken.None));
        return Accepted();
    }

    [HttpDelete("catalog/{type}/{id:int}")]
    public async Task<IActionResult> Remove(string type, int id, CancellationToken ct) { await lib.Remove(type, id, ct); return NoContent(); }

    private static object Card(JsonElement x, string type, HashSet<string> inLib)
    {
        var date = Clients.Text(x, type == "movie" ? "release_date" : "first_air_date");
        var id = Clients.Int(x, "id");
        return new
        {
            type, id,
            title = Clients.Text(x, type == "movie" ? "title" : "name"),
            year = date.Length >= 4 ? date[..4] : "",
            overview = Clients.Text(x, "overview"),
            poster = Clients.Text(x, "poster_path"),
            inLibrary = inLib.Contains(type + id),
        };
    }

    /// <summary>Onglets : films, séries, animes (populaires).</summary>
    [HttpGet("browse")]
    public async Task<IActionResult> Browse([FromQuery] string kind = "movie", [FromQuery] int page = 1, CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 500);
        var (path, type) = kind switch
        {
            "tv" => ($"tv/popular?page={page}", "tv"),
            "anime" => ($"discover/tv?with_genres=16&with_original_language=ja&sort_by=popularity.desc&page={page}", "tv"),
            _ => ($"movie/popular?page={page}", "movie"),
        };
        var json = await clients.Tmdb(path, ct);
        var inLib = lib.LoadCatalog().Select(x => x.Type + x.TmdbId).ToHashSet();
        var items = json.GetProperty("results").EnumerateArray().Where(x => Clients.Text(x, "poster_path").Length > 0).Select(x => Card(x, type, inLib));
        return Ok(new { items, page, totalPages = Math.Min(500, Clients.Int(json, "total_pages")) });
    }

    /// <summary>Fiche : résumé, acteurs, date de sortie, recommandations.</summary>
    [HttpGet("detail/{type}/{id:int}")]
    public async Task<IActionResult> Detail(string type, int id, CancellationToken ct)
    {
        if (type is not ("movie" or "tv")) return BadRequest(new { error = "Type invalide." });
        var d = await clients.Tmdb($"{type}/{id}?append_to_response=credits,recommendations", ct);
        var inLib = lib.LoadCatalog().Select(x => x.Type + x.TmdbId).ToHashSet();
        var date = Clients.Text(d, type == "movie" ? "release_date" : "first_air_date");
        var cast = d.TryGetProperty("credits", out var cr) && cr.TryGetProperty("cast", out var c)
            ? c.EnumerateArray().Take(10).Select(a => new { name = Clients.Text(a, "name"), character = Clients.Text(a, "character"), photo = Clients.Text(a, "profile_path") }).ToList()
            : [];
        var recs = d.TryGetProperty("recommendations", out var rc) && rc.TryGetProperty("results", out var rr)
            ? rr.EnumerateArray().Where(x => Clients.Text(x, "poster_path").Length > 0).Take(12).Select(x => Card(x, type, inLib)).ToList()
            : [];
        var genres = d.TryGetProperty("genres", out var g) ? g.EnumerateArray().Select(x => Clients.Text(x, "name")).ToList() : [];
        var seasons = type == "tv" && d.TryGetProperty("seasons", out var ss)
            ? ss.EnumerateArray().Where(x => Clients.Int(x, "season_number") > 0).Select(x => new { number = Clients.Int(x, "season_number"), episodes = Clients.Int(x, "episode_count") }).ToList()
            : [];
        return Ok(new
        {
            type, id,
            title = Clients.Text(d, type == "movie" ? "title" : "name"),
            date,
            overview = Clients.Text(d, "overview"),
            poster = Clients.Text(d, "poster_path"),
            backdrop = Clients.Text(d, "backdrop_path"),
            runtime = Clients.Int(d, "runtime"),
            genres, cast, recommendations = recs, seasons,
            inLibrary = inLib.Contains(type + id),
        });
    }

    /// <summary>Sources classées pour un film / un épisode (recherches mises en cache 4 h).</summary>
    [HttpGet("sources/{type}/{id:int}")]
    public async Task<IActionResult> Sources(string type, int id, [FromQuery] int season = 1, [FromQuery] int episode = 1, CancellationToken ct = default)
    {
        if (type is not ("movie" or "tv")) return BadRequest(new { error = "Type invalide." });
        var media = new MediaRef(type, id, type == "tv" ? season : 0, type == "tv" ? episode : 0);
        var plan = await resolver.BuildPlan(media, ct);
        var current = await resolver.Peek(media);
        var items = plan.Ranked.Take(40).Select(x => new
        {
            title = x.Release.Title, link = x.Release.Link, seeders = x.Release.Seeders, size = x.Release.Size,
            indexer = x.Release.Indexer, score = x.Score,
            current = current is not null && (current.Link == x.Release.Link || current.Release == x.Release.Title),
            pack = type == "tv" && Ranking.TvFit(x.Release.Title, media.Season, media.Episode) == 2,
        });
        return Ok(new { total = plan.Total, current = current is null ? null : new { release = current.Release, pinned = current.Pinned }, items });
    }

    /// <summary>Impose la source choisie.</summary>
    [HttpPost("pin")]
    public async Task<IActionResult> Pin([FromBody] PinRequest r)
    {
        if (r.Type is not ("movie" or "tv") || string.IsNullOrWhiteSpace(r.Link)) return BadRequest(new { error = "Requête invalide." });
        await resolver.Pin(new MediaRef(r.Type, r.Id, r.Type == "tv" ? r.Season : 0, r.Type == "tv" ? r.Episode : 0), new Release(r.Title, r.Link, 0, 0, ""));
        return NoContent();
    }

    /// <summary>Revient au choix automatique.</summary>
    [HttpDelete("pin/{type}/{id:int}")]
    public async Task<IActionResult> Unpin(string type, int id, [FromQuery] int season = 0, [FromQuery] int episode = 0)
    {
        await resolver.Unpin(new MediaRef(type, id, season, episode));
        return NoContent();
    }

    /// <summary>Écarte la source actuelle : une autre sera choisie à la prochaine lecture.</summary>
    [HttpPost("reject/{type}/{id:int}")]
    public async Task<IActionResult> Reject(string type, int id, [FromQuery] int season = 0, [FromQuery] int episode = 0)
    {
        await resolver.Invalidate(new MediaRef(type, id, season, episode).Key, ban: true);
        return NoContent();
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct) => Ok(new { added = await lib.RefreshShows(null, ct) });
}
