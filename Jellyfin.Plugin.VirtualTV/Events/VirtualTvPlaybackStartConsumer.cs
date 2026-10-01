using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Coordinates Virtual TV playback starts only. State protection intentionally performs no
/// user-data writes here; once VOD starts, Jellyfin and the client own the player.
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
