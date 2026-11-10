using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>
/// Chaque utilisateur ne voit que ses propres titres. Les titres restent dans les mêmes bibliothèques ; chacun reçoit une étiquette
/// « ts-hide:&lt;utilisateur&gt; » pour tous les comptes qui n'en sont pas propriétaires, et chaque compte bloque sa propre étiquette
/// (filtre « Étiquettes bloquées » de Jellyfin : listes, recherche, « Reprendre », accès direct).
/// </summary>
public sealed class PrivacyService(IUserManager users, IUserDataManager userData, ILibraryManager library, LibraryService lib, ILogger<PrivacyService> log) : IHostedService
{
    private const string Prefix = "ts-hide:";
    private readonly SemaphoreSlim _gate = new(1);

    public static string HideTag(Guid user) => Prefix + user.ToString("N");

    /// <summary>Un titre ajouté ou actualisé par Jellyfin : ses étiquettes sont remises tout de suite si elles ont disparu.</summary>
    private void OnChanged(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (item is not (Series or Movie) || item.Path is not { } path || !path.StartsWith(LibraryService.Root, StringComparison.Ordinal)) return;
        var m = System.Text.RegularExpressions.Regex.Match(path, @"\[tmdbid-(\d+)\]");
        if (!m.Success) return;
        var id = int.Parse(m.Groups[1].Value);
        var type = item is Series ? "tv" : "movie";
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);   // laisse Jellyfin terminer son écriture
            if (lib.LoadCatalog().FirstOrDefault(x => x.Type == type && x.TmdbId == id) is { } c)
                try { await _gate.WaitAsync(); try { await Apply(c, CancellationToken.None); } finally { _gate.Release(); } } catch (Exception) { }
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded += OnChanged;
        library.ItemUpdated += OnChanged;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(45));
            // les actualisations de métadonnées de Jellyfin peuvent effacer les étiquettes : on les remet régulièrement
            while (true)
            {
                try { await Sync(CancellationToken.None); } catch (Exception ex) { log.LogWarning("Confidentialité : {M}", ex.Message); }
                await Task.Delay(TimeSpan.FromMinutes(5));
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded -= OnChanged;
        library.ItemUpdated -= OnChanged;
        return Task.CompletedTask;
    }

    private static string N(Guid g) => g.ToString("N");

    /// <summary>Chaque compte bloque sa propre étiquette.</summary>
    private async Task EnsureUsers()
    {
        foreach (var user in users.GetUsers())
        {
            var tag = HideTag(user.Id);
            var blocked = user.GetPreferenceValues<string>(PreferenceKind.BlockedTags) ?? [];
            if (blocked.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            user.SetPreference(PreferenceKind.BlockedTags, blocked.Append(tag).ToArray());
            await users.UpdateUserAsync(user);
            log.LogInformation("Confidentialité : étiquette de blocage ajoutée pour {U}", user.Username);
        }
    }

    /// <summary>Applique les étiquettes d'un titre (propriétaires = ceux qui le voient ; aucun propriétaire = visible de tous).</summary>
    public async Task Apply(CatalogItem item, CancellationToken ct)
    {
        BaseItem? target = item.Type == "movie" ? lib.FindMovie(item.TmdbId)
            : lib.ShowFolder(item.TmdbId) is { } f ? library.FindByPath(f, true) : null;
        if (target is null) return;
        var owners = item.Owners.Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
        var wanted = owners.Count == 0 ? [] : users.GetUsers().Where(u => !owners.Contains(u.Id)).Select(u => HideTag(u.Id)).ToList();
        var current = target.Tags ?? [];
        var keep = current.Where(t => !t.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        var next = keep.Concat(wanted).ToArray();
        var locked = target.LockedFields.Contains(MetadataField.Tags);
        if (current.OrderBy(x => x).SequenceEqual(next.OrderBy(x => x), StringComparer.Ordinal) && locked) return;
        target.Tags = next;
        if (!locked) target.LockedFields = target.LockedFields.Append(MetadataField.Tags).ToArray();   // une actualisation ne les efface pas
        await target.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
    }

    /// <summary>
    /// Titres d'avant la séparation (ajoutés à la main, sans propriétaire) : propriétaires = comptes qui les ont lus, commencés ou mis en favori ;
    /// sans aucune activité, le titre revient à l'administrateur (l'autre compte le retrouve en l'ouvrant de nouveau depuis Découvrir).
    /// </summary>
    private void Migrate()
    {
        var all = users.GetUsers().ToList();
        var admin = all.FirstOrDefault(u => u.HasPermission(PermissionKind.IsAdministrator)) ?? all.FirstOrDefault();
        foreach (var item in lib.LoadCatalog().Where(x => !x.Auto && x.Owners.Count == 0))
        {
            BaseItem? target = item.Type == "movie" ? lib.FindMovie(item.TmdbId)
                : lib.ShowFolder(item.TmdbId) is { } f ? library.FindByPath(f, true) : null;
            var owners = new List<string>();
            if (target is not null)
            {
                var parts = target is Folder
                    ? library.GetItemList(new InternalItemsQuery { AncestorIds = [target.Id], IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode], Recursive = true }).Cast<BaseItem>().Append(target).ToList()
                    : [target];
                foreach (var u in all)
                    if (parts.Any(p => userData.GetUserData(u, p) is { } d && (d.Played || d.PlaybackPositionTicks > 0 || d.IsFavorite)))
                        owners.Add(N(u.Id));
            }
            if (owners.Count == 0 && admin is not null) owners.Add(N(admin.Id));
            if (owners.Count > 0) { foreach (var o in owners) lib.AddOwner(item.Type, item.TmdbId, o); log.LogInformation("Confidentialité : « {T} » attribué à {N} compte(s)", item.Title, owners.Count); }
        }
    }

    /// <summary>Remet à jour tous les comptes et tous les titres (démarrage, tâche de nuit, nouvel utilisateur).</summary>
    public async Task Sync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureUsers();
            Migrate();
            var catalog = lib.LoadCatalog();
            foreach (var item in catalog)
                try { await Apply(item, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Confidentialité de « {T} » : {M}", item.Title, ex.Message); }
            await HideOrphans(catalog, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Éléments de nos bibliothèques absents du catalogue (dossier oublié) : visibles du seul administrateur.</summary>
    private async Task HideOrphans(List<CatalogItem> catalog, CancellationToken ct)
    {
        var all = users.GetUsers().ToList();
        var admin = all.FirstOrDefault(u => u.HasPermission(PermissionKind.IsAdministrator));
        if (admin is null) return;
        var known = catalog.Select(x => x.TmdbId).ToHashSet();
        var items = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series],
            Recursive = true,
        });
        foreach (var target in items)
        {
            if (target.Path is not { } path || !path.StartsWith(LibraryService.Root, StringComparison.Ordinal)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(path, @"\[tmdbid-(\d+)\]");
            if (!m.Success || known.Contains(int.Parse(m.Groups[1].Value))) continue;
            var wanted = all.Where(u => u.Id != admin.Id).Select(u => HideTag(u.Id)).ToArray();
            var current = target.Tags ?? [];
            var next = current.Where(t => !t.StartsWith(Prefix, StringComparison.Ordinal)).Concat(wanted).ToArray();
            if (current.OrderBy(x => x).SequenceEqual(next.OrderBy(x => x), StringComparer.Ordinal)) continue;
            target.Tags = next;
            await target.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
        }
    }

    /// <summary>Ce titre devient visible de cet utilisateur (il l'a ouvert).</summary>
    public async Task Own(string type, int tmdbId, Guid? user, CancellationToken ct)
    {
        if (user is not { } u) return;
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureUsers();
            var item = lib.AddOwner(type, tmdbId, N(u));
            if (item is not null) await Apply(item, ct);
        }
        finally { _gate.Release(); }
    }
}
