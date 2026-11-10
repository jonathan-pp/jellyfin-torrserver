using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Profil d'un utilisateur (champ null ou vide = réglage global).</summary>
public sealed class UserProfile
{
    /// <summary>Profil de sources personnalisé ; sinon réglages globaux et tous les trackers.</summary>
    public bool Custom { get; set; }
    public List<string>? Indexers { get; set; }
    public string? PreferredTags { get; set; }
    public string? AvoidTags { get; set; }
    public int? MaxResolution { get; set; }
    public bool? PreferH264 { get; set; }
    public int? MinSeeders { get; set; }
    public int? MaxMovieSizeGb { get; set; }
    public int? MaxEpisodeSizeGb { get; set; }
    /// <summary>Accès à « Découvrir » (TMDB) : voir et ouvrir des titres absents de la bibliothèque.</summary>
    public bool Discover { get; set; } = true;
    /// <summary>Quota mensuel en Go (null = réglage global, 0 = illimité).</summary>
    public int? QuotaGb { get; set; }
}

/// <summary>Réglages de choix des sources effectifs pour une lecture.</summary>
public sealed record SourcePrefs(string[] Preferred, string[] Avoid, int MaxResolution, bool PreferH264, int MinSeeders,
    int MaxMovieSizeGb, int MaxEpisodeSizeGb, IReadOnlySet<string>? Indexers, string Scope);

public static class Access
{
    private static string _raw = "\u0000";
    private static Dictionary<string, UserProfile> _map = new();

    private static string[] Tags(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static Dictionary<string, UserProfile> Profiles()
    {
        var c = Clients.Config;
        var raw = (c.UserProfiles ?? "") + "\u0001" + (c.UserIndexers ?? "");
        if (raw == _raw) return _map;
        var map = new Dictionary<string, UserProfile>();
        try
        {
            if ((c.UserProfiles ?? "").Trim().Length > 0)
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, UserProfile>>(c.UserProfiles!) ?? new())
                    if (Guid.TryParse(k, out var g) && v is not null) map[g.ToString("N")] = v;
            // ancien réglage « trackers par utilisateur »
            if ((c.UserIndexers ?? "").Trim().Length > 0)
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, List<string>>>(c.UserIndexers!) ?? new())
                    if (Guid.TryParse(k, out var g) && v is not null && !map.ContainsKey(g.ToString("N")))
                        map[g.ToString("N")] = new UserProfile { Custom = true, Indexers = v };
        }
        catch (JsonException) { }
        _map = map; _raw = raw;
        return map;
    }

    public static UserProfile? Profile(Guid? user) =>
        user is { } u && u != Guid.Empty && Profiles().TryGetValue(u.ToString("N"), out var p) ? p : null;

    public static string UserScope(Guid? user) => user is { } u && u != Guid.Empty ? "@u:" + u.ToString("N") : "";

    /// <summary>Réglages effectifs ; Scope = "" pour les réglages globaux, sinon propre à l'utilisateur.</summary>
    public static SourcePrefs Prefs(Guid? user)
    {
        var c = Clients.Config;
        var p = Profile(user);
        if (p is not { Custom: true })
            return new SourcePrefs(Tags(c.PreferredTags), Tags(c.AvoidTags), c.MaxResolution, c.PreferH264, c.MinSeeders, c.MaxMovieSizeGb, c.MaxEpisodeSizeGb, null, "");
        return new SourcePrefs(
            string.IsNullOrWhiteSpace(p.PreferredTags) ? Tags(c.PreferredTags) : Tags(p.PreferredTags),
            p.AvoidTags is null ? Tags(c.AvoidTags) : Tags(p.AvoidTags),
            p.MaxResolution is > 0 ? p.MaxResolution.Value : c.MaxResolution,
            p.PreferH264 ?? c.PreferH264,
            p.MinSeeders is >= 0 ? p.MinSeeders.Value : c.MinSeeders,
            p.MaxMovieSizeGb is > 0 ? p.MaxMovieSizeGb.Value : c.MaxMovieSizeGb,
            p.MaxEpisodeSizeGb is > 0 ? p.MaxEpisodeSizeGb.Value : c.MaxEpisodeSizeGb,
            p.Indexers is null ? null : new HashSet<string>(p.Indexers, StringComparer.OrdinalIgnoreCase),
            UserScope(user));
    }

    public static bool Discover(Guid? user) => Profile(user)?.Discover ?? true;

    public static bool Ok(IReadOnlySet<string>? allow, string? indexer) => allow is null || (!string.IsNullOrEmpty(indexer) && allow.Contains(indexer));

    public static Guid? UserOf(ClaimsPrincipal? p) =>
        Guid.TryParse(p?.FindFirst("Jellyfin-UserId")?.Value, out var g) && g != Guid.Empty ? g : null;
}

/// <summary>
/// Retient qui vient de demander à lire quel titre (appel PlaybackInfo des applis Jellyfin).
/// Le flux lui-même est demandé par ffmpeg, sans utilisateur : on s'en sert pour appliquer ses droits.
/// </summary>
public sealed class PlaybackTracker(ILibraryManager library, ProbeService probe) : IAsyncActionFilter
{
    private readonly ConcurrentDictionary<string, (Guid User, DateTime At)> _last = new();

    /// <summary>Durée de validité prolongée à chaque lecture : un film peut durer des heures, et ffmpeg rouvre le flux à chaque saut.</summary>
    public Guid? UserFor(string key)
    {
        if (!_last.TryGetValue(key, out var v) || DateTime.UtcNow - v.At > TimeSpan.FromHours(6)) return null;
        _last[key] = (v.User, DateTime.UtcNow);
        return v.User;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        try
        {
            if (context.HttpContext.Request.Path.Value?.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) == true
                && context.ActionArguments.TryGetValue("itemId", out var raw) && raw is Guid itemId)
            {
                var user = context.ActionArguments.TryGetValue("userId", out var u) && u is Guid g && g != Guid.Empty ? g : Access.UserOf(context.HttpContext.User);
                if (user is not null && library.GetItemById(itemId) is { } item && KeyOf(item) is { } key)
                    _last[key] = (user.Value, DateTime.UtcNow);
                // la durée réelle de la version choisie doit être connue AVANT que le lecteur ne la reçoive,
                // sinon la barre de progression dépasse la fin réelle de la vidéo et l'épisode suivant ne démarre jamais
                var msid = context.ActionArguments.TryGetValue("mediaSourceId", out var m) ? m as string : null;
                if (msid is null)
                    foreach (var arg in context.ActionArguments.Values)
                        if (arg is not null && arg.GetType().GetProperty("MediaSourceId")?.GetValue(arg) is string ms2) { msid = ms2; break; }
                var target = Guid.TryParse(msid, out var sg) ? library.GetItemById(sg) : null;
                target ??= library.GetItemById(itemId);
                if (target?.Path is { } tp && tp.StartsWith(LibraryService.Root, StringComparison.Ordinal) && tp.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                    await probe.ProbePath(tp).WaitAsync(TimeSpan.FromSeconds(30), context.HttpContext.RequestAborted).ConfigureAwait(false);
            }
        }
        catch { /* ne jamais gêner la lecture */ }
        var executed = await next();
        try
        {
            // Jellyfin vient d'actualiser l'élément : le lecteur doit recevoir la durée mesurée, pas celle de TMDB
            if (executed.Result is ObjectResult { Value: PlaybackInfoResponse { MediaSources: { } sources } })
                foreach (var ms in sources)
                    if (Guid.TryParse(ms.Id, out var id) && probe.RealTicks(id) is { } real)
                    {
                        ms.RunTimeTicks = real;
                        if (library.GetItemById(id) is { } version) probe.Restore(version);
                    }
        }
        catch { /* idem */ }
    }

    /// <summary>Clé TorrServer (movie:ID ou tv:ID:S:E) d'un élément de nos bibliothèques.</summary>
    public static string? KeyOf(BaseItem item)
    {
        var path = item.Path ?? "";
        if (!path.StartsWith(LibraryService.Root, StringComparison.Ordinal)) return null;
        var tm = Regex.Match(path, @"\[tmdbid-(\d+)\]");
        if (!tm.Success) return null;
        var id = int.Parse(tm.Groups[1].Value);
        if (item is Episode ep && ep.ParentIndexNumber is int s && ep.IndexNumber is int e) return new MediaRef("tv", id, s, e).Key;
        return path.StartsWith(LibraryService.MoviesDir, StringComparison.Ordinal) ? new MediaRef("movie", id).Key : null;
    }
}
