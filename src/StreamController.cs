using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Cible des fichiers .strm : résout la source puis relaie le flux de TorrServer (Range inclus).</summary>
[ApiController]
[AllowAnonymous]
[Route("TorrServerPlugin/stream/{key}")]
public sealed class StreamController(Resolver resolver, Clients clients, SourceStore store, VersionService versions, PlaybackTracker tracker, ILogger<StreamController> log) : ControllerBase
{
    [HttpGet("movie/{id:int}")]
    [HttpHead("movie/{id:int}")]
    public Task Movie(string key, int id, [FromQuery] string? sid, CancellationToken ct) => Serve(key, new MediaRef("movie", id), sid, ct);

    [HttpGet("tv/{id:int}/{season:int}/{episode:int}")]
    [HttpHead("tv/{id:int}/{season:int}/{episode:int}")]
    public Task Episode(string key, int id, int season, int episode, [FromQuery] string? sid, CancellationToken ct) => Serve(key, new MediaRef("tv", id, season, episode), sid, ct);

    /// <summary>Flux direct d'un fichier connu (chaîne TMDB).</summary>
    [HttpGet("h/{hash}/{file:int}")]
    [HttpHead("h/{hash}/{file:int}")]
    public async Task Hash(string key, string hash, int file, CancellationToken ct)
    {
        if (!KeyOk(key) || hash.Length is < 32 or > 64 || hash.Any(c => !Uri.IsHexDigit(c))) { Response.StatusCode = 404; return; }
        try { await Proxy(clients.StreamUrl(hash, file, "video"), ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogWarning("Flux {H} : {M}", hash, ex.Message); if (!Response.HasStarted) await Fail(502, ex.Message); }
    }

    private async Task Proxy(string url, CancellationToken ct)
    {
        using var request = clients.Torr(HttpMethod.Get, url);
        if (HttpMethods.IsHead(Request.Method)) request.Method = HttpMethod.Head;
        foreach (var h in new[] { "Range", "If-Range" })
            if (Request.Headers.TryGetValue(h, out var v)) request.Headers.TryAddWithoutValidation(h, v.ToArray());
        using var upstream = await clients.StreamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Response.StatusCode = (int)upstream.StatusCode;
        foreach (var h in new[] { "Accept-Ranges", "Content-Range", "Content-Length", "Content-Type", "Last-Modified", "ETag" })
            if (upstream.Headers.TryGetValues(h, out var vals) || upstream.Content.Headers.TryGetValues(h, out vals))
                Response.Headers[h] = string.Join(", ", vals);
        if (!HttpMethods.IsHead(Request.Method)) await upstream.Content.CopyToAsync(Response.Body, ct);
    }

    private static bool KeyOk(string given)
    {
        var expected = Clients.Config.StreamKey;
        return expected.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
    }

    private async Task Fail(int status, string message)
    {
        Response.StatusCode = status;
        Response.ContentType = "text/plain; charset=utf-8";
        if (!HttpMethods.IsHead(Request.Method)) await Response.WriteAsync(message);
    }

    /// <summary>ffprobe lancé par Jellyfin sur une version dont le torrent n'est pas encore chargé ?</summary>
    private async Task<bool> UnwantedProbe(MediaRef media, string? sid)
    {
        var c = HttpContext.Connection;
        if (c.RemoteIpAddress is null || !(System.Net.IPAddress.IsLoopback(c.RemoteIpAddress) || c.RemoteIpAddress.Equals(c.LocalIpAddress))) return false;
        if (!PeerProcess.IsFfprobe(c.RemoteIpAddress, c.RemotePort)) return false;
        var known = await resolver.Peek(media);
        if (known is not { Hash.Length: > 0 }) return true;
        if (sid is null) return false;
        var release = store.Get(sid);
        return release is null || known.Link != release.Link;
    }

    private async Task Serve(string key, MediaRef media, string? sid, CancellationToken ct)
    {
        if (!KeyOk(key)) { Response.StatusCode = 404; return; }
        if (Request.Query["probe"] != "1" && await UnwantedProbe(media, sid))
        {
            log.LogDebug("{K} : analyse automatique de Jellyfin ignorée (source pas encore chargée)", media.Key);
            await Fail(503, "Analyse ignorée : la source sera chargée à la lecture.");
            return;
        }
        log.LogDebug("FLUX {K} sid={S} {M} range={R} UA={U}", media.Key, sid ?? "auto", Request.Method, Request.Headers.Range.ToString(), Request.Headers.UserAgent.ToString());
        var user = tracker.UserFor(media.Key);
        var prefs = Access.Prefs(user);
        var scope = "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Entry entry;
            try
            {
                var release = sid is null ? null : store.Get(sid);
                if (release is not null && !Access.Ok(prefs.Indexers, release.Indexer))
                {
                    log.LogInformation("{K} : source {I} non autorisée pour cet utilisateur, choix parmi ses trackers", media.Key, release.Indexer);
                    release = null;
                }
                if (release is null)
                {
                    scope = (await resolver.ScopeFor(media, user)).Scope;
                    entry = await resolver.Resolve(media, ct, user);
                }
                else
                {
                    // version choisie dans le menu : mémorisée pour cet utilisateur seulement
                    scope = Access.UserScope(user);
                    entry = await resolver.ResolveWith(media, release, ct, scope: scope);
                }
                versions.Played(media, release is null ? null : sid);
            }
            catch (OperationCanceledException) { return; }
            catch (KeyNotFoundException ex) { await Fail(404, ex.Message); return; }
            catch (Exception ex) { log.LogWarning("{Key} : {M}", media.Key, ex.Message); await Fail(502, ex.Message); return; }

            try
            {
                using var request = clients.Torr(HttpMethod.Get, clients.StreamUrl(entry.Hash, entry.FileId, entry.FileName));
                if (HttpMethods.IsHead(Request.Method)) request.Method = HttpMethod.Head;
                foreach (var h in new[] { "Range", "If-Range" })
                    if (Request.Headers.TryGetValue(h, out var v)) request.Headers.TryAddWithoutValidation(h, v.ToArray());
                using var upstream = await clients.StreamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)upstream.StatusCode >= 400 && attempt == 0)
                {
                    log.LogInformation("{Key} : TorrServer répond {C}, nouvelle résolution", media.Key, (int)upstream.StatusCode);
                    await resolver.Invalidate(media.Key + scope, ban: false);
                    continue;
                }
                Response.StatusCode = (int)upstream.StatusCode;
                foreach (var h in new[] { "Accept-Ranges", "Content-Range", "Content-Length", "Content-Type", "Last-Modified", "ETag" })
                    if (upstream.Headers.TryGetValues(h, out var vals) || upstream.Content.Headers.TryGetValues(h, out vals))
                        Response.Headers[h] = string.Join(", ", vals);
                if (!HttpMethods.IsHead(Request.Method)) await upstream.Content.CopyToAsync(Response.Body, ct);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (HttpRequestException ex) when (attempt == 0)
            {
                log.LogWarning("{Key} : {M}", media.Key, ex.Message);
                await resolver.Invalidate(media.Key + scope, ban: false);
            }
            catch (Exception ex) { log.LogWarning("{Key} : {M}", media.Key, ex.Message); if (!Response.HasStarted) await Fail(502, ex.Message); return; }
        }
    }
}
