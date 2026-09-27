using Jellyfin.Plugin.VirtualTV.Events;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.VirtualTV;

/// <summary>
/// Registers Virtual TV runtime services with Jellyfin.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PlaybackStateProtectionManager>();
        serviceCollection.AddSingleton<ILiveTvService, VirtualTvLiveTvService>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, ProtectedPlaybackStartConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackProgressEventArgs>, ProtectedPlaybackProgressConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStopEventArgs>, ProtectedPlaybackStopConsumer>();
    }
}
