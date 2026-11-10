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
public sealed class StreamController(Resolver resolver, LibraryService lib, Clients clients, SourceStore store, VersionService versions, PlaybackTracker tracker, UsageService usage, MediaBrowser.Controller.Session.ISessionManager sessions, ILogger<StreamController> log) : ControllerBase
{
    [HttpGet("movie/{id:int}")]
    [HttpHead("movie/{id:int}")]
    public Task Movie(string key, int id, [FromQuery] string? sid, CancellationToken ct) => Serve(key, new MediaRef("movie", id), sid, ct);

    [HttpGet("tv/{id:int}/{season:int}/{episode:int}")]
    [HttpHead("tv/{id:int}/{season:int}/{episode:int}")]
    public Task Episode(string key, int id, int season, int episode, [FromQuery] string? sid, CancellationToken ct) => Serve(key, new MediaRef("tv", id, season, episode), sid, ct);

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

    /// <summary>
    /// ffprobe lancé par Jellyfin lui-même ? Il actualise toutes les versions d'un .strm à chaque lecture : les laisser passer
    /// chargerait un torrent par version et ferait attendre le lecteur. Le plugin fait sa propre analyse (probe=1) de la version lue.
    /// </summary>
    private bool UnwantedProbe()
    {
        var c = HttpContext.Connection;
        if (c.RemoteIpAddress is null || !(System.Net.IPAddress.IsLoopback(c.RemoteIpAddress) || c.RemoteIpAddress.Equals(c.LocalIpAddress))) return false;
        return PeerProcess.IsFfprobe(c.RemoteIpAddress, c.RemotePort);
    }

    /// <summary>Aucune donnée après ce délai au début d'un fichier : la source est morte, on passe à la suivante.</summary>
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Compte les octets lus ; prévient l'utilisateur (message dans son lecteur) à 80 % de son quota.</summary>
    private void Count(Guid user, long bytes, MediaRef media, long quota)
    {
        var title = media.IsTv ? "tv:" + media.TmdbId : "movie:" + media.TmdbId;
        if (!usage.Add(user, bytes, title, quota)) return;
        _ = Task.Run(async () =>
        {
            foreach (var session in sessions.Sessions.Where(x => x.UserId == user).ToList())
                try
                {
                    await sessions.SendMessageCommand(session.Id, session.Id, new MediaBrowser.Model.Session.MessageCommand
                    {
                        Header = "Quota mensuel",
                        Text = $"Vous avez utilisé 80 % de votre quota de {quota / 1_000_000_000} Go ce mois-ci ({usage.Used(user) / 1_000_000_000.0:0.0} Go).",
                        TimeoutMs = 15000,
                    }, CancellationToken.None);
                }
                catch (Exception) { }
        });
    }

    private async Task Serve(string key, MediaRef media, string? sid, CancellationToken ct)
    {
        if (!KeyOk(key)) { Response.StatusCode = 404; return; }
        if (lib.IsUnaired(media)) { await Fail(404, "Cet épisode n'est pas encore sorti."); return; }
        var analysis = Request.Query["probe"] == "1";
        if (!analysis && UnwantedProbe())
        {
            await Fail(503, "Analyse automatique ignorée.");
            return;
        }
        var user = tracker.UserFor(media.Key);
        var prefs = Access.Prefs(user);
        // quota mensuel : refusé avant même de chercher une source (les analyses internes ne comptent pas)
        var quota = !analysis && user is { } qu ? UsageService.Quota(user) : 0;
        if (quota > 0 && usage.Used(user!.Value) >= quota)
        {
            await Fail(429, $"Quota mensuel de {quota / 1_000_000_000} Go atteint : la lecture reprendra le mois prochain (ou demandez à l'administrateur d'augmenter votre quota).");
            return;
        }
        var release = sid is null ? null : store.Get(sid);
        if (release is not null && (!Access.Ok(prefs.Indexers, release.Indexer) || resolver.IsDead(release.Title)))
        {
            log.LogInformation("{K} : source {I} écartée (non autorisée ou muette), choix automatique", media.Key, release.Indexer);
            release = null;
        }
        // Changer de torrent n'est possible qu'au début du fichier : au milieu, les positions ne correspondraient plus.
        var range = Request.Headers.Range.ToString();
        var fromStart = !HttpMethods.IsHead(Request.Method) && (range.Length == 0 || range.StartsWith("bytes=0-", StringComparison.Ordinal));

        const int Attempts = 4;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var last = attempt == Attempts - 1;
            Entry entry;
            string scope;
            try
            {
                if (release is null)
                {
                    scope = (await resolver.ScopeFor(media, user)).Scope;
                    entry = await resolver.Resolve(media, ct, user);
                }
                else
                {
                    // version choisie dans le menu : mémorisée pour cet utilisateur seulement
                    scope = Access.UserScope(user);
                    entry = await resolver.ResolveWith(media, release, ct, scope);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (KeyNotFoundException ex) { await Fail(404, ex.Message); return; }
            catch (Exception ex) when (release is not null)
            {
                log.LogWarning("{Key} : source choisie inutilisable ({M}), choix automatique", media.Key, ex.Message);
                release = null;
                continue;
            }
            catch (Exception ex) { log.LogWarning("{Key} : {M}", media.Key, ex.Message); await Fail(502, ex.Message); return; }

            if (!analysis && attempt == 0) versions.Played(media, user, sid);

            try
            {
                using var request = clients.Torr(HttpMethod.Get, clients.StreamUrl(entry.Hash, entry.FileId, entry.FileName));
                if (HttpMethods.IsHead(Request.Method)) request.Method = HttpMethod.Head;
                foreach (var h in new[] { "Range", "If-Range" })
                    if (Request.Headers.TryGetValue(h, out var v)) request.Headers.TryAddWithoutValidation(h, v.ToArray());
                using var upstream = await clients.StreamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)upstream.StatusCode >= 400 && !last)
                {
                    log.LogInformation("{Key} : TorrServer répond {C}, nouvelle résolution", media.Key, (int)upstream.StatusCode);
                    await resolver.Invalidate(media, scope, dead: false);
                    if (release is null) versions.SourceChanged(media);
                    continue;
                }

                await using var body = await upstream.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[81920];
                var read = 0;
                if (!HttpMethods.IsHead(Request.Method))
                {
                    using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (fromStart && !last) first.CancelAfter(FirstByteTimeout);
                    try { read = await body.ReadAsync(buffer, first.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        log.LogWarning("{Key} : aucune donnée de « {R} » après {S} s, source suivante", media.Key, entry.Release, (int)FirstByteTimeout.TotalSeconds);
                        await resolver.Invalidate(media, scope, dead: true);
                        if (release is null) versions.SourceChanged(media);
                        release = null;
                        continue;
                    }
                }

                Response.StatusCode = (int)upstream.StatusCode;
                foreach (var h in new[] { "Accept-Ranges", "Content-Range", "Content-Length", "Content-Type", "Last-Modified", "ETag" })
                    if (upstream.Headers.TryGetValues(h, out var vals) || upstream.Content.Headers.TryGetValues(h, out vals))
                        Response.Headers[h] = string.Join(", ", vals);
                if (read > 0)
                {
                    await Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (analysis || user is null) await body.CopyToAsync(Response.Body, ct);
                    else
                    {
                        // comptage au fil de l'eau ; coupe le flux dès que le quota est dépassé
                        var uid = user.Value;
                        Count(uid, read, media, quota);
                        int n;
                        while ((n = await body.ReadAsync(buffer, ct)) > 0)
                        {
                            await Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                            Count(uid, n, media, quota);
                            if (quota > 0 && usage.Used(uid) >= quota)
                            {
                                log.LogWarning("Quota mensuel atteint pour l'utilisateur {U} : flux coupé", uid);
                                break;
                            }
                        }
                        usage.Save();
                    }
                }
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (HttpRequestException ex) when (!last)
            {
                log.LogWarning("{Key} : {M}", media.Key, ex.Message);
                await resolver.Invalidate(media, scope, dead: false);
                if (release is null) versions.SourceChanged(media);
            }
            catch (Exception ex) { log.LogWarning("{Key} : {M}", media.Key, ex.Message); if (!Response.HasStarted) await Fail(502, ex.Message); return; }
        }
    }
}
