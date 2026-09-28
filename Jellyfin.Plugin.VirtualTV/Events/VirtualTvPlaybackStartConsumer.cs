using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Coordinates the channel-to-source handoff and keeps protected library state unchanged.
/// </summary>
public sealed class VirtualTvPlaybackStartConsumer : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly LiveTvPlaybackCoordinator _coordinator;
    private readonly PlaybackStateProtectionManager _stateProtection;

    public VirtualTvPlaybackStartConsumer(
        LiveTvPlaybackCoordinator coordinator,
        PlaybackStateProtectionManager stateProtection)
    {
        _coordinator = coordinator;
        _stateProtection = stateProtection;
    }

    public async Task OnEvent(PlaybackStartEventArgs eventArgs)
    {
        await _coordinator.HandlePlaybackStartAsync(eventArgs).ConfigureAwait(false);
        _stateProtection.RestoreIfProtected(eventArgs, clearAfterRestore: false);
    }
}
