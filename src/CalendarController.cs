using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Calendrier des prochains épisodes : liste pour la page de configuration, flux .ics pour les agendas.</summary>
[ApiController]
[Route("TorrServerPlugin")]
public sealed class CalendarController(ScheduleService schedule) : ControllerBase
{
    [HttpGet("calendar")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Calendar()
    {
        var names = schedule.Names();
        return Ok(new
        {
            items = schedule.Upcoming().Select(x => new
            {
                title = x.Title, season = x.Season, episode = x.Episode, name = x.Name, when = x.When, hasTime = x.HasTime,
                owners = x.Owners.Select(o => names.TryGetValue(o, out var n) ? n : "?"),
            }),
            // adresse relative du flux de chaque utilisateur (la page ajoute l'adresse du serveur)
            feeds = names.ToDictionary(n => n.Value, n => $"TorrServerPlugin/calendar/{n.Key}.ics?key={schedule.KeyFor(Guid.Parse(n.Key))}"),
        });
    }

    [HttpGet("calendar/{user}.ics")]
    [AllowAnonymous]
    public IActionResult Feed(string user, [FromQuery] string? key)
    {
        if (!Guid.TryParse(user, out var id) || !schedule.KeyOk(id, key ?? "")) return NotFound();
        return Content(schedule.Ics(id), "text/calendar; charset=utf-8");
    }
}
