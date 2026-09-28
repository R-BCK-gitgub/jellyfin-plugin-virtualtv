using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Applies the wall-clock Virtual TV position after a native Jellyfin Live TV channel starts.
/// </summary>
public sealed class VirtualTvPlaybackStartConsumer : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly LiveTvPlaybackCoordinator _coordinator;

    public VirtualTvPlaybackStartConsumer(LiveTvPlaybackCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task OnEvent(PlaybackStartEventArgs eventArgs)
        => _coordinator.HandlePlaybackStartAsync(eventArgs);
}
