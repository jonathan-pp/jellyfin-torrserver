using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>API utilisée par la page de configuration (administrateurs uniquement).</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TorrServerPlugin")]
public sealed class ApiController(Clients clients, LibraryService lib, PrivacyService privacy, UsageService usage, MediaBrowser.Controller.Library.IUserManager users, ILogger<ApiController> log) : ControllerBase
{
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct) => Ok(await clients.TestAll(ct));

    [HttpGet("indexers")]
    public async Task<IActionResult> Indexers(CancellationToken ct) =>
        Ok((await clients.Indexers(ct)).Select(x => new { name = x.Name, enabled = x.Enabled }));

    [HttpPost("setup")]
    public async Task<IActionResult> Setup()
    {
        var message = await lib.EnsureLibraries();
        await privacy.Sync(CancellationToken.None);
        return Ok(new { message, root = LibraryService.Root });
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
    public async Task<IActionResult> Remove(string type, int id, CancellationToken ct)
    {
        log.LogWarning("Titre retiré de la bibliothèque : {T}{I} (utilisateur {U}, {IP})", type, id, Access.UserOf(User), HttpContext.Connection.RemoteIpAddress);
        await lib.Remove(type, id, ct);
        return NoContent();
    }

    /// <summary>Données lues : ce mois-ci par utilisateur, mois passés, titres les plus regardés.</summary>
    [HttpGet("usage")]
    public IActionResult Usage()
    {
        var u = usage.Snapshot();
        var names = users.GetUsers().ToDictionary(x => x.Id.ToString("N"), x => x.Username);
        var catalog = lib.LoadCatalog();
        string Title(string key)
        {
            var parts = key.Split(':');
            return catalog.FirstOrDefault(x => x.Type == parts[0] && x.TmdbId.ToString() == parts[1])?.Title ?? key;
        }
        return Ok(new
        {
            month = u.Month,
            users = u.Bytes,   // compatibilité : octets par utilisateur ce mois-ci
            names,
            quotas = names.Keys.ToDictionary(k => k, k => UsageService.Quota(Guid.Parse(k))),
            history = u.History.OrderByDescending(x => x.Key).Select(x => new { month = x.Key, users = x.Value }),
            titles = u.Titles.OrderByDescending(x => x.Value.Bytes).Take(15)
                .Select(x => new { title = Title(x.Key), type = x.Key.Split(':')[0], bytes = x.Value.Bytes, viewers = x.Value.Users.Count }),
        });
    }

    /// <summary>État du VPN écrit toutes les 30 s par un petit script côté serveur (absent = non configuré).</summary>
    [HttpGet("vpn")]
    public IActionResult Vpn()
    {
        try
        {
            var f = Path.Combine(Plugin.Instance!.DataFolderPath, "vpn.json");
            if (!System.IO.File.Exists(f)) return Ok(new { available = false });
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(f));
            var r = doc.RootElement;
            var at = r.TryGetProperty("at", out var a) ? a.GetInt64() : 0;
            var fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at < 180;
            return Ok(new
            {
                available = true, fresh,
                ip = r.TryGetProperty("ip", out var ip) ? ip.GetString() : "",
                online = fresh && r.TryGetProperty("online", out var o) && o.GetBoolean(),
                port = r.TryGetProperty("port", out var p) ? p.GetString() : "",
            });
        }
        catch (Exception) { return Ok(new { available = false }); }
    }

    [HttpPost("usage/reset")]
    public IActionResult ResetUsage([FromQuery] Guid? user) { usage.Reset(user); return NoContent(); }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct) => Ok(new { added = await lib.RefreshShows(null, ct) });
}
