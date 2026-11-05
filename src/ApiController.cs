using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>API utilisée par la page de configuration (administrateurs uniquement).</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TorrServerPlugin")]
public sealed class ApiController(Clients clients, LibraryService lib) : ControllerBase
{
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct) => Ok(await clients.TestAll(ct));

    [HttpGet("indexers")]
    public async Task<IActionResult> Indexers(CancellationToken ct) =>
        Ok((await clients.Indexers(ct)).Select(x => new { name = x.Name, enabled = x.Enabled }));

    [HttpPost("setup")]
    public async Task<IActionResult> Setup() => Ok(new { message = await lib.EnsureLibraries(), root = LibraryService.Root });

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

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct) => Ok(new { added = await lib.RefreshShows(null, ct) });
}
