using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Restores protected library state, then lets the Virtual TV coordinator decide whether the
/// stop is a user exit or a natural programme transition that must stay on the channel clock.
/// </summary>
public sealed class VirtualTvPlaybackStopConsumer : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly LiveTvPlaybackCoordinator _coordinator;
    private readonly PlaybackStateProtectionManager _stateProtection;

    public VirtualTvPlaybackStopConsumer(
        LiveTvPlaybackCoordinator coordinator,
        PlaybackStateProtectionManager stateProtection)
    {
        _coordinator = coordinator;
        _stateProtection = stateProtection;
    }

    public async Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        _stateProtection.RestoreIfProtected(eventArgs, clearAfterRestore: false);
        await _coordinator.HandlePlaybackStopAsync(eventArgs).ConfigureAwait(false);
    }
}
