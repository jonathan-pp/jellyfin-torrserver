using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Retire de la bibliothèque les titres délaissés, pour ne pas accumuler des séries qu'on ne regarde plus.</summary>
public sealed class CleanupTask(LibraryService lib, PrivacyService privacy) : IScheduledTask
{
    public string Name => "Retirer les titres non regardés (TorrServer)";
    public string Key => "TorrServerCleanup";
    public string Description => "Retire les films, séries et animes ni ouverts ni lus depuis le délai choisi dans la configuration.";
    public string Category => "TorrServer";
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        await lib.Cleanup(Clients.Config.CleanupDays, ct);
        await privacy.Sync(ct);   // nouveaux comptes : chacun ne voit que ses titres
    }
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks }];
}
