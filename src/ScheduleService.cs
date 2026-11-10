using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Un épisode à venir, pour le calendrier (page de configuration et flux .ics).</summary>
public sealed record Upcoming(string Type, int TmdbId, string Title, int Season, int Episode, string Name, DateTime When, bool HasTime, List<string> Owners, string Poster = "");

/// <summary>
/// Prochains épisodes : « Prochain épisode » en tête du résumé de chaque série, avertissement à l'écran le jour de la sortie
/// (puis création de l'épisode suivant), calendrier des sorties et flux .ics par utilisateur.
/// </summary>
public sealed partial class ScheduleService(LibraryService lib, ILibraryManager library, ISessionManager sessions, IUserManager users, AniSkipProvider aniskip, ILogger<ScheduleService> log) : IHostedService
{
    private static readonly CultureInfo Fr = new("fr-FR");
    private static string File_ => Path.Combine(Plugin.Instance!.DataFolderPath, "schedule.json");
    private readonly SemaphoreSlim _gate = new(1);
    private sealed class Store { public string Secret { get; set; } = ""; public List<string> Notified { get; set; } = []; }
    private Store? _store;

    private Store Load()
    {
        if (_store is not null) return _store;
        try { if (File.Exists(File_)) _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(File_)); } catch (Exception) { }
        _store ??= new Store();
        if (_store.Secret.Length == 0) { _store.Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); Save(); }
        return _store;
    }
    private void Save() { try { File.WriteAllText(File_ + ".tmp", JsonSerializer.Serialize(_store)); File.Move(File_ + ".tmp", File_, true); } catch (Exception) { } }

    /// <summary>Clé secrète du flux .ics d'un utilisateur (le lien ne permet que de lire ses sorties à venir).</summary>
    public string KeyFor(Guid user)
    {
        lock (this) return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Load().Secret), Encoding.UTF8.GetBytes(user.ToString("N")))).ToLowerInvariant()[..32];
    }
    public bool KeyOk(Guid user, string key) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(KeyFor(user)), Encoding.UTF8.GetBytes(key ?? ""));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(90));
            while (true)
            {
                try { await Tick(CancellationToken.None); } catch (Exception ex) { log.LogWarning("Prochains épisodes : {M}", ex.Message); }
                await Task.Delay(TimeSpan.FromMinutes(10));
            }
        });
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string KeyOf(CatalogItem c) => $"{c.Type}{c.TmdbId}:S{c.Next!.Season}E{c.Next.Episode}";

    public async Task Tick(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var c in lib.LoadCatalog().Where(x => x.Type == "tv" && !x.Auto).ToList())
            {
                try
                {
                    // heure exacte des animes (AniList)
                    if (c.Anime && c.Next is { At: null })
                    {
                        var at = await aniskip.AiringAt(c.TmdbId, c.Next.Date, ct);
                        var season = c.Next.Season; var ep = c.Next.Episode;
                        lib.Update("tv", c.TmdbId, x => { if (x.Next is { } n && n.Season == season && n.Episode == ep) n.At = at; });
                        if (at is not null) c.Next.At = at;
                    }
                    // épisode sorti : avertissement puis création du suivant
                    if (c.Next is { } next && next.Due <= now)
                    {
                        string key;
                        lock (this) { key = KeyOf(c); if (Load().Notified.Contains(key)) key = ""; else { Load().Notified.Add(key); if (Load().Notified.Count > 300) Load().Notified.RemoveRange(0, 100); Save(); } }
                        await lib.RefreshOne("tv", c.TmdbId, ct);
                        if (key.Length > 0) Notify(c, next);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Prochain épisode de « {T} » : {M}", c.Title, ex.Message); }
            }
            foreach (var c in lib.LoadCatalog().Where(x => x.Type == "tv" && !x.Auto))
                try { await ApplyOverview(c, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Résumé de « {T} » : {M}", c.Title, ex.Message); }
        }
        finally { _gate.Release(); }
    }

    private void Notify(CatalogItem c, NextEp n)
    {
        var owners = c.Owners.Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).ToHashSet();
        foreach (var s in sessions.Sessions.Where(s => owners.Contains(s.UserId)).ToList())
            _ = Task.Run(async () =>
            {
                try
                {
                    await sessions.SendMessageCommand(s.Id, s.Id, new MediaBrowser.Model.Session.MessageCommand
                    {
                        Header = "Nouvel épisode",
                        Text = $"{c.Title} : S{n.Season:00}E{n.Episode:00}{(n.Name.Length > 0 ? " « " + n.Name + " »" : "")} vient de sortir.",
                        TimeoutMs = 20000,
                    }, CancellationToken.None);
                }
                catch (Exception) { }
            });
        log.LogInformation("Nouvel épisode : {T} S{S:00}E{E:00}", c.Title, n.Season, n.Episode);
    }

    private const string Marker = "⏳ Prochain épisode";
    [GeneratedRegex(@"^⏳ Prochain épisode[^\n]*\n\n?")] private static partial Regex MarkerLine();

    public static string Describe(NextEp n)
    {
        var when = n.At is { } a ? TimeZoneInfo.ConvertTimeFromUtc(a, TimeZoneInfo.Local) : DateTime.Parse(n.Date, CultureInfo.InvariantCulture);
        var day = when.ToString("dddd d MMMM", Fr);
        return $"{Marker} : S{n.Season:00}E{n.Episode:00}{(n.Name.Length > 0 ? " « " + n.Name + " »" : "")} — {day}{(n.At is null ? "" : " à " + when.ToString("HH:mm", Fr))}";
    }

    /// <summary>« Prochain épisode : … » en tête du résumé de la série (retiré quand il n'y a plus d'épisode à venir).</summary>
    private async Task ApplyOverview(CatalogItem c, CancellationToken ct)
    {
        if (lib.ShowFolder(c.TmdbId) is not { } folder || library.FindByPath(folder, true) is not Series series) return;
        var current = series.Overview ?? "";
        var basis = MarkerLine().Replace(current, "");
        var wanted = c.Next is { } n && n.Due > DateTime.UtcNow.AddDays(-1) ? Describe(n) + "\n\n" + basis : basis;
        if (wanted == current) return;
        series.Overview = wanted.TrimEnd();
        if (!series.LockedFields.Contains(MetadataField.Overview)) series.LockedFields = series.LockedFields.Append(MetadataField.Overview).ToArray();   // une actualisation ne l'efface pas
        await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
    }

    /// <summary>Sorties à venir de tous les titres suivis (une seule : le prochain épisode de chaque série).</summary>
    public List<Upcoming> Upcoming() =>
        lib.LoadCatalog().Where(x => x.Type == "tv" && !x.Auto && x.Next is not null)
            .Select(x => new Upcoming(x.Type, x.TmdbId, x.Title, x.Next!.Season, x.Next.Episode, x.Next.Name,
                x.Next.At ?? DateTime.SpecifyKind(DateTime.Parse(x.Next.Date, CultureInfo.InvariantCulture), DateTimeKind.Utc), x.Next.At is not null, x.Owners, x.Poster))
            .OrderBy(x => x.When).ToList();

    /// <summary>Flux iCalendar d'un utilisateur : ses prochains épisodes (jour entier ou heure exacte).</summary>
    public string Ics(Guid user)
    {
        var id = user.ToString("N");
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//TorrServer//Prochains episodes//FR\r\nCALSCALE:GREGORIAN\r\nX-WR-CALNAME:Prochains épisodes\r\nREFRESH-INTERVAL;VALUE=DURATION:PT6H\r\nX-PUBLISHED-TTL:PT6H\r\n");
        foreach (var u in Upcoming().Where(x => x.Owners.Contains(id)))
        {
            var title = $"{u.Title} S{u.Season:00}E{u.Episode:00}";
            sb.Append("BEGIN:VEVENT\r\n");
            sb.Append($"UID:ts-{u.TmdbId}-s{u.Season}e{u.Episode}@torrserver\r\n");
            sb.Append($"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}\r\n");
            if (u.HasTime) sb.Append($"DTSTART:{u.When:yyyyMMdd'T'HHmmss'Z'}\r\nDTEND:{u.When.AddMinutes(30):yyyyMMdd'T'HHmmss'Z'}\r\n");
            else sb.Append($"DTSTART;VALUE=DATE:{u.When:yyyyMMdd}\r\nDTEND;VALUE=DATE:{u.When.AddDays(1):yyyyMMdd}\r\n");
            sb.Append("SUMMARY:").Append(Escape(title)).Append("\r\n");
            if (u.Name.Length > 0) sb.Append("DESCRIPTION:").Append(Escape(u.Name)).Append("\r\n");
            sb.Append("END:VEVENT\r\n");
        }
        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

    public Dictionary<string, string> Names() => users.GetUsers().ToDictionary(x => x.Id.ToString("N"), x => x.Username);
}
