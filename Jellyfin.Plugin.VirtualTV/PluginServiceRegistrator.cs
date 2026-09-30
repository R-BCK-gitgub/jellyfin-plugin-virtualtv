using Jellyfin.Plugin.VirtualTV.Events;
using Jellyfin.Plugin.VirtualTV.Services;
using Jellyfin.Plugin.VirtualTV.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.VirtualTV;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<VirtualTvScheduleStore>();
        serviceCollection.AddSingleton<VirtualTvScheduleGenerator>();
        serviceCollection.AddSingleton<VirtualTvEpisodeResolver>();
        serviceCollection.AddSingleton<PlaybackStateProtectionManager>();
        serviceCollection.AddSingleton<LiveTvPlaybackCoordinator>();
        serviceCollection.AddSingleton<IScheduledTask, VirtualTvSmartScheduleRefreshTask>();
        serviceCollection.AddSingleton<ILiveTvService, VirtualTvLiveTvService>();

        serviceCollection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, VirtualTvPlaybackStartConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackProgressEventArgs>, VirtualTvPlaybackProgressConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStopEventArgs>, VirtualTvPlaybackStopConsumer>();
    }
}
