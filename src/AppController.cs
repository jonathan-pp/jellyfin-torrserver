using System.Text.Json;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

public sealed record PlayRequest(string Type, int Id, int Season, int Episode, string? Sid);
public sealed record DetailsRequest(string ItemId);

/// <summary>Application web façon Stremio (pages publiques, API réservée aux utilisateurs connectés).</summary>
[ApiController]
[Route("TorrServerPlugin/app")]
public sealed class AppController(Clients clients, Resolver resolver, SourceStore store, LibraryService lib, VersionService versions, MediaBrowser.Controller.Library.ILibraryManager library, IFileSystem files, ILogger<AppController> log) : ControllerBase
{
    // ------------------------------------------------------------ fichiers statiques
    private static readonly System.Reflection.Assembly Asm = typeof(AppController).Assembly;
    private IActionResult Resource(string name, string type)
    {
        var s = Asm.GetManifestResourceStream("Jellyfin.Plugin.TorrServer.Web." + name);
        if (s is null) return NotFound();
        Response.Headers.CacheControl = name.EndsWith(".html") || name == "inject.js" ? "no-cache" : "public, max-age=86400";
        return File(s, type);
    }

    [AllowAnonymous][HttpGet("")] public IActionResult Index() => Resource("index.html", "text/html; charset=utf-8");
    [AllowAnonymous][HttpGet("inject.js")] public IActionResult Inject() => Resource("inject.js", "text/javascript");
    [AllowAnonymous][HttpGet("hls.min.js")] public IActionResult Hls() => Resource("hls.min.js", "text/javascript");
    [AllowAnonymous][HttpGet("manifest.webmanifest")] public IActionResult Manifest() => Resource("manifest.webmanifest", "application/manifest+json");
    [AllowAnonymous][HttpGet("icon.png")]
    public IActionResult Icon()
    {
        var s = Asm.GetManifestResourceStream("Jellyfin.Plugin.TorrServer.logo.png");
        return s is null ? NotFound() : File(s, "image/png");
    }

    // ------------------------------------------------------------ catalogue
    private static object Card(JsonElement x, string type) => new
    {
        type, id = Clients.Int(x, "id"),
        title = Clients.Text(x, type == "movie" ? "title" : "name"),
        year = Clients.Text(x, type == "movie" ? "release_date" : "first_air_date") is { Length: >= 4 } d ? d[..4] : "",
        date = Clients.Text(x, type == "movie" ? "release_date" : "first_air_date"),
        poster = Clients.Text(x, "poster_path"), backdrop = Clients.Text(x, "backdrop_path"),
        rating = x.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDouble(), 1) : 0,
        overview = Clients.Text(x, "overview"),
    };

    /// <summary>Film sorti depuis au moins <paramref name="days"/> jours (les séries sont toujours gardées : seuls leurs épisodes diffusés sont proposés).</summary>
    private static bool Available(JsonElement x, string type, int days)
    {
        if (type != "movie") return true;
        var d = Clients.Text(x, "release_date");
        return DateTime.TryParse(d, out var when) && when.Date <= DateTime.UtcNow.Date.AddDays(-days);
    }

    private const string AnimeTv = "discover/tv?with_genres=16&with_original_language=ja";
    /// <summary>Films déjà sortis en numérique (VOD) ou en DVD/Blu-ray : les seuls qu'on peut trouver en torrent.</summary>
    private static string Released => "with_release_type=4|5&release_date.lte=" + DateTime.UtcNow.ToString("yyyy-MM-dd");
    private static (string Path, string Type)? Category(string kind, string cat) => (kind, cat) switch
    {
        ("movie", "trending") or ("movie", "new_streaming") => ($"discover/movie?{Released}&release_date.gte={DateTime.UtcNow.AddDays(-120):yyyy-MM-dd}&sort_by=popularity.desc&vote_count.gte=20", "movie"),
        ("movie", "popular") => ($"discover/movie?{Released}&sort_by=popularity.desc&vote_count.gte=100", "movie"),
        ("movie", "now_playing") => ("movie/now_playing", "movie"),
        ("movie", "top_rated") => ($"discover/movie?{Released}&sort_by=vote_average.desc&vote_count.gte=5000", "movie"),
        ("movie", "upcoming") => ("movie/upcoming", "movie"),
        ("tv", "trending") => ("trending/tv/week", "tv"),
        ("tv", "popular") => ("tv/popular", "tv"),
        ("tv", "on_the_air") => ("tv/on_the_air", "tv"),
        ("tv", "top_rated") => ("discover/tv?sort_by=vote_average.desc&vote_count.gte=2000", "tv"),
        ("anime", "popular") => (AnimeTv + "&sort_by=popularity.desc", "tv"),
        ("anime", "top_rated") => (AnimeTv + "&sort_by=vote_average.desc&vote_count.gte=200", "tv"),
        ("anime", "new") => (AnimeTv + "&sort_by=first_air_date.desc&vote_count.gte=20", "tv"),
        ("anime", "movies") => ($"discover/movie?with_genres=16&with_original_language=ja&{Released}&sort_by=popularity.desc", "movie"),
        ("movie", var g) when g.StartsWith("genre-") => ($"discover/movie?with_genres={int.Parse(g[6..])}&{Released}&sort_by=popularity.desc&vote_count.gte=20", "movie"),
        ("tv", var g) when g.StartsWith("genre-") => ($"discover/tv?with_genres={int.Parse(g[6..])}&sort_by=popularity.desc&vote_count.gte=20", "tv"),
        _ => null,
    };

    private async Task<(List<object> Items, int Pages)> Page(string kind, string cat, int page, CancellationToken ct)
    {
        var c = Category(kind, cat) ?? throw new ArgumentException("Catégorie inconnue.");
        var json = await clients.Tmdb(c.Path + (c.Path.Contains('?') ? "&" : "?") + "page=" + Math.Clamp(page, 1, 500), ct);
        var items = json.GetProperty("results").EnumerateArray().Where(x => Clients.Text(x, "poster_path").Length > 0 && Available(x, c.Type, 0)).Select(x => Card(x, c.Type)).ToList();
        return (items, Math.Min(500, Clients.Int(json, "total_pages")));
    }

    [Authorize][Discover][HttpGet("api/home")]
    public async Task<IActionResult> Home(CancellationToken ct)
    {
        var rows = new (string Title, string Kind, string Cat)[]
        {
            ("Nouveautés en streaming", "movie", "new_streaming"), ("Séries tendances", "tv", "trending"), ("Animes populaires", "anime", "popular"),
            ("Films populaires", "movie", "popular"), ("Séries populaires", "tv", "popular"),
            ("Films d'animation japonais", "anime", "movies"), ("Films cultes", "movie", "top_rated"), ("Séries cultes", "tv", "top_rated"),
        };
        var tasks = rows.Select(async r =>
        {
            try { var p = await Page(r.Kind, r.Cat, 1, ct); return (object)new { title = r.Title, kind = r.Kind, cat = r.Cat, items = p.Items }; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new { title = r.Title, kind = r.Kind, cat = r.Cat, items = new List<object>() }; }
        });
        return Ok(await Task.WhenAll(tasks));
    }

    [Authorize][Discover][HttpGet("api/list")]
    public async Task<IActionResult> List([FromQuery] string kind, [FromQuery] string cat, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var p = await Page(kind, cat, page, ct);
        return Ok(new { items = p.Items, page, totalPages = p.Pages });
    }

    [Authorize][Discover][HttpGet("api/genres/{type}")]
    public async Task<IActionResult> Genres(string type, CancellationToken ct)
    {
        if (type is not ("movie" or "tv")) return BadRequest();
        var j = await clients.Tmdb($"genre/{type}/list", ct);
        return Ok(j.GetProperty("genres").EnumerateArray().Select(g => new { id = Clients.Int(g, "id"), name = Clients.Text(g, "name") }));
    }

    [Authorize][Discover][HttpGet("api/search")]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<object>());
        var j = await clients.Tmdb("search/multi?include_adult=false&query=" + Uri.EscapeDataString(q), ct);
        return Ok(j.GetProperty("results").EnumerateArray()
            .Where(x => Clients.Text(x, "media_type") is "movie" or "tv" && Clients.Text(x, "poster_path").Length > 0 && Available(x, Clients.Text(x, "media_type"), 45))
            .Select(x => Card(x, Clients.Text(x, "media_type"))));
    }

    [Authorize][Discover][HttpGet("api/meta/{type}/{id:int}")]
    public async Task<IActionResult> Meta(string type, int id, CancellationToken ct)
    {
        if (type is not ("movie" or "tv")) return BadRequest();
        var d = await clients.Tmdb($"{type}/{id}?append_to_response=credits,recommendations,images,external_ids&include_image_language=fr,en,null", ct);
        var movie = type == "movie";
        string logo = "";
        if (d.TryGetProperty("images", out var im) && im.TryGetProperty("logos", out var logos))
        {
            var l = logos.EnumerateArray().ToList();
            var pick = l.FirstOrDefault(x => Clients.Text(x, "iso_639_1") == "fr");
            if (pick.ValueKind == JsonValueKind.Undefined) pick = l.FirstOrDefault(x => Clients.Text(x, "iso_639_1") == "en");
            if (pick.ValueKind == JsonValueKind.Undefined && l.Count > 0) pick = l[0];
            if (pick.ValueKind != JsonValueKind.Undefined) logo = Clients.Text(pick, "file_path");
        }
        var cast = d.TryGetProperty("credits", out var cr) && cr.TryGetProperty("cast", out var c)
            ? c.EnumerateArray().Take(15).Select(a => new { name = Clients.Text(a, "name"), character = Clients.Text(a, "character"), photo = Clients.Text(a, "profile_path") }).ToList<object>() : [];
        var directors = d.TryGetProperty("credits", out var cr2) && cr2.TryGetProperty("crew", out var crew)
            ? crew.EnumerateArray().Where(x => Clients.Text(x, "job") == "Director").Select(x => Clients.Text(x, "name")).Distinct().Take(3).ToList() : [];
        if (!movie && d.TryGetProperty("created_by", out var cb)) directors.AddRange(cb.EnumerateArray().Select(x => Clients.Text(x, "name")).Take(3));
        var recs = d.TryGetProperty("recommendations", out var rc) && rc.TryGetProperty("results", out var rr)
            ? rr.EnumerateArray().Where(x => Clients.Text(x, "poster_path").Length > 0).Take(20).Select(x => Card(x, type)).ToList() : [];
        var seasons = !movie && d.TryGetProperty("seasons", out var ss)
            ? ss.EnumerateArray().Where(x => Clients.Int(x, "season_number") > 0 && Clients.Int(x, "episode_count") > 0)
                .Select(x => new { number = Clients.Int(x, "season_number"), name = Clients.Text(x, "name"), episodes = Clients.Int(x, "episode_count") }).ToList<object>() : [];
        var date = Clients.Text(d, movie ? "release_date" : "first_air_date");
        var runtime = movie ? Clients.Int(d, "runtime") : (d.TryGetProperty("episode_run_time", out var ert) && ert.GetArrayLength() > 0 && ert[0].TryGetInt32(out var rt) ? rt : 0);
        return Ok(new
        {
            type, id, title = Clients.Text(d, movie ? "title" : "name"), originalTitle = Clients.Text(d, movie ? "original_title" : "original_name"),
            year = date.Length >= 4 ? date[..4] : "", date, runtime,
            rating = d.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDouble(), 1) : 0,
            overview = Clients.Text(d, "overview"), tagline = Clients.Text(d, "tagline"),
            poster = Clients.Text(d, "poster_path"), backdrop = Clients.Text(d, "backdrop_path"), logo,
            genres = d.TryGetProperty("genres", out var g) ? g.EnumerateArray().Select(x => Clients.Text(x, "name")).ToList() : [],
            directors, cast, recommendations = recs, seasons,
            imdb = d.TryGetProperty("external_ids", out var ex) ? Clients.Text(ex, "imdb_id") : Clients.Text(d, "imdb_id"),
        });
    }

    [Authorize][Discover][HttpGet("api/episodes/{id:int}/{season:int}")]
    public async Task<IActionResult> Episodes(int id, int season, CancellationToken ct)
    {
        var d = await clients.Tmdb($"tv/{id}/season/{season}", ct);
        var today = DateTime.UtcNow.Date;
        return Ok(d.GetProperty("episodes").EnumerateArray().Select(e =>
        {
            var air = Clients.Text(e, "air_date");
            return new
            {
                number = Clients.Int(e, "episode_number"), name = Clients.Text(e, "name"), overview = Clients.Text(e, "overview"),
                still = Clients.Text(e, "still_path"), air, runtime = Clients.Int(e, "runtime"),
                aired = !(DateTime.TryParse(air, out var a) && a.Date > today) && air.Length > 0,
            };
        }));
    }

    // ------------------------------------------------------------ sources
    private static readonly string[] LangTags = ["MULTI", "VFF", "VFQ", "VF2", "VFI", "TRUEFRENCH", "FRENCH", "VF", "VOSTFR", "SUBFRENCH", "VO"];
    private static readonly string[] Extras = ["HDR10+", "HDR10", "HDR", "DV", "DoVi", "Atmos", "DTS-HD", "TrueHD", "DDP", "EAC3", "AC3", "AAC", "OPUS", "x265", "HEVC", "AV1", "x264", "REMUX", "BluRay", "WEB-DL", "WEBRip", "HDLight", "mHD", "4KLight"];

    /// <summary>Droits de l'utilisateur connecté : trackers autorisés (null = tous) et accès à « Découvrir ».</summary>
    [Authorize][HttpGet("api/access")]
    public IActionResult MyAccess()
    {
        var u = Access.UserOf(User);
        return Ok(new { indexers = Access.Prefs(u).Indexers?.Order().ToList(), discover = Access.Discover(u) });
    }

    [Authorize][Discover][HttpGet("api/streams/{type}/{id:int}")]
    public async Task<IActionResult> Streams(string type, int id, [FromQuery] int season = 0, [FromQuery] int episode = 0, CancellationToken ct = default)
    {
        if (type is not ("movie" or "tv")) return BadRequest();
        var media = new MediaRef(type, id, type == "tv" ? season : 0, type == "tv" ? episode : 0);
        var user = Access.UserOf(User);
        var (scope, prefs) = await resolver.ScopeFor(media, user);
        var plan = await resolver.BuildPlan(media, ct, prefs);
        var current = await resolver.Peek(media, scope) ?? await resolver.Peek(media);
        var list = plan.Ranked.Take(Math.Clamp(Clients.Config.MaxResults, 5, 80)).Select(x =>
        {
            var r = x.Release;
            var res = Ranking.Resolution(r.Title);
            return new
            {
                sid = store.Put(r), title = r.Title, indexer = r.Indexer, seeders = r.Seeders, size = r.Size, score = x.Score,
                quality = res == 0 ? "?" : res == 2160 ? "4K" : res + "p",
                langs = LangTags.Where(t => Ranking.HasTag(r.Title, t)).ToList(),
                tags = Extras.Where(t => Ranking.HasTag(r.Title, t)).Take(5).ToList(),
                pack = type == "tv" && Ranking.TvFit(r.Title, media.Season, media.Episode) == 2,
                current = current is not null && (current.Link == r.Link || current.Release == r.Title),
            };
        }).ToList();
        store.Save();
        return Ok(new { total = plan.Total, current = current is null ? null : new { release = current.Release, pinned = current.Pinned }, items = list });
    }

    /// <summary>Ouvre un titre dans Jellyfin : le crée au besoin et renvoie l'élément (film ou série) à afficher.</summary>
    [Authorize][Discover][HttpPost("api/open")]
    public async Task<IActionResult> Open([FromBody] PlayRequest r, CancellationToken ct)
    {
        if (r.Type is not ("movie" or "tv")) return BadRequest(new { error = "Type invalide." });
        try
        {
            var item = await versions.Open(r.Type, r.Id, ct);
            return Ok(new { itemId = item.Id.ToString("N") });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Ouverture {T}{I} : {M}", r.Type, r.Id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Fiche Jellyfin ouverte : prépare les sources (menu « Version ») de l'élément. changed = recharger la page.</summary>
    [Authorize][HttpGet("api/progress/{type}/{id:int}")]
    public IActionResult OpenProgress(string type, int id) => Ok(new { pct = VersionService.Progress.GetValueOrDefault(type + id, 0) });

    [Authorize][HttpPost("api/details")]
    public async Task<IActionResult> Details([FromBody] DetailsRequest r, CancellationToken ct)
    {
        if (!Guid.TryParse(r.ItemId, out var gid) || library.GetItemById(gid) is not { } item) return Ok(new { changed = false });
        try { return Ok(new { changed = await versions.Details(item, ct) }); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Sources de {N} : {M}", item.Name, ex.Message); return Ok(new { changed = false }); }
    }

    /// <summary>Prépare la lecture : élément Jellyfin, source choisie (ou automatique), torrent prêt, média analysé.</summary>
    [Authorize][Discover][HttpPost("api/play")]
    public async Task<IActionResult> Play([FromBody] PlayRequest r, CancellationToken ct)
    {
        if (r.Type is not ("movie" or "tv")) return BadRequest(new { error = "Type invalide." });
        var media = new MediaRef(r.Type, r.Id, r.Type == "tv" ? r.Season : 0, r.Type == "tv" ? r.Episode : 0);
        try
        {
            var dirs = new DirectoryService(files);
            var item = await lib.EnsureItem(media, dirs, ct);
            var user = Access.UserOf(User);
            var own = Access.UserScope(user);
            Entry entry;
            if (!string.IsNullOrEmpty(r.Sid))
            {
                var release = store.Get(r.Sid) ?? throw new InvalidOperationException("Source expirée : rechargez la liste.");
                if (!Access.Ok(Access.Prefs(user).Indexers, release.Indexer)) throw new InvalidOperationException("Ce tracker ne vous est pas autorisé.");
                // source choisie à la main : propre à cet utilisateur
                var cur = await resolver.Peek(media, own);
                if (cur is null || cur.Link != release.Link || !cur.Pinned) await resolver.Pin(media, release, own);
                entry = await resolver.ResolveWith(media, release, ct, pin: true, scope: own);
            }
            else
            {
                var cur = await resolver.Peek(media, own);
                if (cur is { Pinned: true }) await resolver.Unpin(media, own);
                entry = await resolver.Resolve(media, ct, user);
            }
            await lib.ProbeIfChanged(item, entry, dirs, ct);
            return Ok(new { itemId = item.Id.ToString("N"), release = entry.Release, file = entry.FileName });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning("Lecture {Key} : {M}", media.Key, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }
}

/// <summary>Réservé aux utilisateurs qui ont accès à « Découvrir » (catalogue TMDB).</summary>
public sealed class DiscoverAttribute : Microsoft.AspNetCore.Mvc.Filters.ActionFilterAttribute
{
    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        if (!Access.Discover(Access.UserOf(context.HttpContext.User)))
            context.Result = new ObjectResult(new { error = "Le catalogue « Découvrir » n'est pas autorisé pour votre compte." }) { StatusCode = 403 };
    }
}
