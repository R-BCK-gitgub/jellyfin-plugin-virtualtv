using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Forwards PlaybackStop to the Virtual TV coordinator. Jellyfin owns normal VOD user state;
/// the plugin does not snapshot, restore or rewrite watched/resume metadata.
/// </summary>
public sealed class VirtualTvPlaybackStopConsumer : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly LiveTvPlaybackCoordinator _coordinator;

    public VirtualTvPlaybackStopConsumer(LiveTvPlaybackCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task OnEvent(PlaybackStopEventArgs eventArgs)
        => _coordinator.HandlePlaybackStopAsync(eventArgs);
}
