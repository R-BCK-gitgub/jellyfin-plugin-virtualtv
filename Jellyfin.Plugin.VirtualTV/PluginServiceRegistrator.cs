using Jellyfin.Plugin.VirtualTV.Events;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.VirtualTV;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<VirtualTvScheduleStore>();
        serviceCollection.AddSingleton<VirtualTvScheduleGenerator>();
        serviceCollection.AddSingleton<LiveTvPlaybackCoordinator>();
        serviceCollection.AddSingleton<ILiveTvService, VirtualTvLiveTvService>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, VirtualTvPlaybackStartConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackProgressEventArgs>, VirtualTvPlaybackProgressConsumer>();
    }
}
