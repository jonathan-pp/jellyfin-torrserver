using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TorrServer;

public sealed class RefreshTask(LibraryService lib) : IScheduledTask
{
    public string Name => "Mettre à jour le catalogue (TorrServer)";
    public string Key => "TorrServerRefreshShows";
    public string Description => "Ajoute les nouveaux titres populaires de TMDB et les épisodes récemment diffusés.";
    public string Category => "TorrServer";
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) => await lib.SyncAll(ct);
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(12).Ticks }];
}
