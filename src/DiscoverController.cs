using MediaBrowser.Model.Entities;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

public sealed record OpenRequest(string Type, int Id);
public sealed record DetailsRequest(string ItemId, bool Full = false);

/// <summary>Script injecté dans l'interface de Jellyfin et API qu'il utilise (page Découvrir, recherche TMDB, fiches).</summary>
[ApiController]
[Route("TorrServerPlugin/app")]
public sealed class DiscoverController(Clients clients, VersionService versions, MediaBrowser.Controller.Library.ILibraryManager library, ScheduleService schedule, ILogger<DiscoverController> log) : ControllerBase
{
    [AllowAnonymous][HttpGet("inject.js")]
    public IActionResult Inject()
    {
        var script = typeof(DiscoverController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.TorrServer.Web.inject.js");
        if (script is null) return NotFound();
        Response.Headers.CacheControl = "no-cache";
        return File(script, "text/javascript");
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

    /// <summary>Fausse catégorie « En production » (séries encore diffusées), placée avec les genres.</summary>
    private const int ProdId = 99999;
    private const string AnimeTv = "discover/tv?with_genres=16&with_original_language=ja";
    /// <summary>Films déjà sortis en numérique (VOD) ou en DVD/Blu-ray : les seuls qu'on peut trouver en torrent.</summary>
    private static string Released => "with_release_type=4|5&release_date.lte=" + DateTime.UtcNow.ToString("yyyy-MM-dd");
    private static (string Path, string Type)? Category(string kind, string cat) => (kind, cat) switch
    {
        ("movie", "new_streaming") => ($"discover/movie?{Released}&release_date.gte={DateTime.UtcNow.AddDays(-120):yyyy-MM-dd}&sort_by=popularity.desc&vote_count.gte=20", "movie"),
        ("movie", "popular") => ($"discover/movie?{Released}&sort_by=popularity.desc&vote_count.gte=100", "movie"),
        ("movie", "top_rated") => ($"discover/movie?{Released}&sort_by=vote_average.desc&vote_count.gte=5000", "movie"),
        ("tv", "trending") => ("trending/tv/week", "tv"),
        ("tv", "popular") => ("tv/popular", "tv"),
        ("tv", "on_the_air") => ("tv/on_the_air", "tv"),
        ("tv", "top_rated") => ("discover/tv?sort_by=vote_average.desc&vote_count.gte=2000", "tv"),
        ("anime", "popular") => (AnimeTv + "&sort_by=popularity.desc", "tv"),
        ("anime", "top_rated") => (AnimeTv + "&sort_by=vote_average.desc&vote_count.gte=200", "tv"),
        ("anime", "new") => (AnimeTv + "&sort_by=first_air_date.desc&vote_count.gte=20", "tv"),
        ("anime", "movies") => ($"discover/movie?with_genres=16&with_original_language=ja&{Released}&sort_by=popularity.desc", "movie"),
        ("movie", var g) when g.StartsWith("genre-") => ($"discover/movie?with_genres={int.Parse(g[6..])}&{Released}&sort_by=popularity.desc&vote_count.gte=20", "movie"),
        ("tv", "genre-99999") => ("discover/tv?with_status=0&sort_by=popularity.desc&vote_count.gte=100", "tv"),
        ("tv", var g) when g.StartsWith("genre-") => ($"discover/tv?with_genres={int.Parse(g[6..])}&sort_by=popularity.desc&vote_count.gte=20", "tv"),
        ("anime", "genre-99999") => ($"{AnimeTv}&with_status=0&sort_by=popularity.desc&vote_count.gte=20", "tv"),
        ("anime", var g) when g.StartsWith("genre-") => ($"{AnimeTv.Replace("with_genres=16", "with_genres=16," + int.Parse(g[6..]))}&sort_by=popularity.desc&vote_count.gte=20", "tv"),
        _ => null,
    };

    private async Task<(List<object> Items, int Pages)> Page(string kind, string cat, int page, CancellationToken ct)
    {
        var c = Category(kind, cat) ?? throw new ArgumentException("Catégorie inconnue.");
        var json = await clients.Tmdb(c.Path + (c.Path.Contains('?') ? "&" : "?") + "page=" + Math.Clamp(page, 1, 500), ct);
        var items = json.GetProperty("results").EnumerateArray().Where(x => Clients.Text(x, "poster_path").Length > 0 && Available(x, c.Type, 0)).Select(x => Card(x, c.Type)).ToList();
        return (items, Math.Min(500, Clients.Int(json, "total_pages")));
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
        if (type is not ("movie" or "tv" or "anime")) return BadRequest();
        var j = await clients.Tmdb($"genre/{(type == "anime" ? "tv" : type)}/list", ct);
        var list = j.GetProperty("genres").EnumerateArray().Where(g => type != "anime" || Clients.Int(g, "id") != 16).Select(g => new { id = Clients.Int(g, "id"), name = Clients.Text(g, "name") });
        return Ok(type == "movie" ? list : new[] { new { id = ProdId, name = "En production" } }.Concat(list));
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

    /// <summary>
    /// Ligne TMDB d'une fiche Jellyfin : filmographie complète d'une personne, ou recommandations d'un film / d'une série
    /// (Jellyfin ne montre que ce qui est déjà dans la bibliothèque).
    /// </summary>
    [Authorize][Discover][HttpGet("api/related")]
    public async Task<IActionResult> Related([FromQuery] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid) || library.GetItemById(gid) is not { } item) return Ok(new { title = "", items = Array.Empty<object>() });
        if (item is MediaBrowser.Controller.Entities.Person person)
        {
            var tmdb = person.GetProviderId("Tmdb");
            if (string.IsNullOrEmpty(tmdb))
            {
                var found = await clients.Tmdb("search/person?query=" + Uri.EscapeDataString(person.Name), ct);
                tmdb = found.GetProperty("results").EnumerateArray().Select(x => Clients.Int(x, "id")).FirstOrDefault().ToString();
            }
            if (tmdb is null or "0") return Ok(new { title = "", items = Array.Empty<object>() });
            var c = await clients.Tmdb($"person/{tmdb}/combined_credits", ct);
            var all = new[] { "cast", "crew" }.Where(k => c.TryGetProperty(k, out _)).SelectMany(k => c.GetProperty(k).EnumerateArray())
                .Where(x => Clients.Text(x, "media_type") is "movie" or "tv" && Clients.Text(x, "poster_path").Length > 0)
                // talk-shows, cérémonies… : apparitions sans intérêt pour la lecture
                .Where(x => !(x.TryGetProperty("genre_ids", out var g) && g.EnumerateArray().Any(v => v.GetInt32() is 10767 or 10763)))
                .DistinctBy(x => Clients.Text(x, "media_type") + Clients.Int(x, "id"))
                .OrderByDescending(x => x.TryGetProperty("vote_count", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0)
                .Take(60).Select(x => Card(x, Clients.Text(x, "media_type")));
            return Ok(new { title = "Filmographie (TMDB, lecture en streaming)", items = all });
        }
        var type = item is MediaBrowser.Controller.Entities.TV.Series ? "tv" : item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : null;
        var tid = type is null ? null : System.Text.RegularExpressions.Regex.Match(item.Path ?? "", @"\[tmdbid-(\d+)\]") is { Success: true } m ? m.Groups[1].Value : item.GetProviderId("Tmdb");
        if (type is null || string.IsNullOrEmpty(tid)) return Ok(new { title = "", items = Array.Empty<object>() });
        var recs = (await clients.Tmdb($"{type}/{tid}/recommendations", ct)).GetProperty("results").EnumerateArray().ToList();
        if (recs.Count < 6) recs.AddRange((await clients.Tmdb($"{type}/{tid}/similar", ct)).GetProperty("results").EnumerateArray());
        return Ok(new
        {
            title = "Recommandations (TMDB)",
            items = recs.Where(x => Clients.Text(x, "poster_path").Length > 0 && Available(x, type, 45))
                .DistinctBy(x => Clients.Int(x, "id")).Take(30).Select(x => Card(x, type)),
        });
    }

    /// <summary>Prochains épisodes des séries de l'utilisateur connecté (page « À venir »).</summary>
    [Authorize][HttpGet("api/soon")]
    public IActionResult Soon()
    {
        var me = Access.UserOf(User)?.ToString("N");
        return Ok(schedule.Upcoming().Where(x => me is not null && x.Owners.Contains(me))
            .Select(x => new { type = "tv", id = x.TmdbId, title = x.Title, poster = x.Poster, season = x.Season, episode = x.Episode, name = x.Name, when = x.When, hasTime = x.HasTime }));
    }

    /// <summary>Droits de l'utilisateur connecté : trackers autorisés (null = tous) et accès à « Découvrir ».</summary>
    [Authorize][HttpGet("api/access")]
    public IActionResult MyAccess()
    {
        var u = Access.UserOf(User);
        return Ok(new { indexers = Access.Prefs(u).Indexers?.Order().ToList(), discover = Access.Discover(u) });
    }

    /// <summary>Ouvre un titre dans Jellyfin : le crée au besoin et renvoie l'élément (film ou série) à afficher.</summary>
    [Authorize][Discover][HttpPost("api/open")]
    public async Task<IActionResult> Open([FromBody] OpenRequest r, CancellationToken ct)
    {
        if (r.Type is not ("movie" or "tv")) return BadRequest(new { error = "Type invalide." });
        try
        {
            var item = await versions.Open(r.Type, r.Id, Access.UserOf(User), ct);
            return Ok(new { itemId = item.Id.ToString("N") });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Ouverture {T}{I} : {M}", r.Type, r.Id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Avancement de l'ouverture d'un titre (pourcentage affiché sur l'affiche).</summary>
    [Authorize][HttpGet("api/progress/{type}/{id:int}")]
    public IActionResult OpenProgress(string type, int id) => Ok(new { pct = VersionService.Progress.GetValueOrDefault(type + id, 0) });

    /// <summary>Fiche Jellyfin ouverte : prépare les sources (menu « Version ») de l'élément. changed = recharger la page.</summary>
    [Authorize][HttpPost("api/details")]
    public async Task<IActionResult> Details([FromBody] DetailsRequest r, CancellationToken ct)
    {
        if (!Guid.TryParse(r.ItemId, out var gid) || library.GetItemById(gid) is not { } item) return Ok(new { changed = false, pending = false });
        try { var (changed, pending) = await versions.Details(item, ct, Access.UserOf(User), r.Full); return Ok(new { changed, pending }); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Sources de {N} : {M}", item.Name, ex.Message); return Ok(new { changed = false, pending = false }); }
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
