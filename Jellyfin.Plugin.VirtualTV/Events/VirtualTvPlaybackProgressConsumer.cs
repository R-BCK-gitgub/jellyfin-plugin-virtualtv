using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Feeds playback progress to the coordinator for Loading confirmation and watched-dependent
/// Resume only. It never restores or saves Jellyfin user data while VOD is running.
/// </summary>
public sealed class VirtualTvPlaybackProgressConsumer : IEventConsumer<PlaybackProgressEventArgs>
{
    private readonly LiveTvPlaybackCoordinator _coordinator;

    public VirtualTvPlaybackProgressConsumer(LiveTvPlaybackCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task OnEvent(PlaybackProgressEventArgs eventArgs)
        => _coordinator.HandlePlaybackProgressAsync(eventArgs);
}
