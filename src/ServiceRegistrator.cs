using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TorrServer;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpClient("ts-api", c => c.Timeout = TimeSpan.FromSeconds(100))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddHttpClient("ts-stream", c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<Clients>();
        services.AddSingleton<Resolver>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ProbeService>();
        services.AddSingleton<SourceStore>();
        services.AddSingleton<PlaybackTracker>();
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.Filters.AddService<PlaybackTracker>());
        services.AddSingleton<VersionService>();
        services.AddHostedService<WebInjector>();
        services.AddHostedService<RuntimeFiller>();
        services.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, RefreshTask>();
    }
}
