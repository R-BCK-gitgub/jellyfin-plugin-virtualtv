using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Restores protected television-style state before the coordinator handles continuity.
/// Unwatched modes have no protection snapshot, so Jellyfin progress is intentionally retained.
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
        if (eventArgs.Session is not null)
        {
            _stateProtection.RestoreAllIfProtected(eventArgs.Session.Id, eventArgs.PlaySessionId);
        }

        await _coordinator.HandlePlaybackStopAsync(eventArgs).ConfigureAwait(false);
    }
}
