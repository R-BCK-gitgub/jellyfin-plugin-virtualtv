using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Restores protected television-style state before the coordinator handles continuity.
/// LG webOS native Live TV sessions are handled separately so natural EOF retunes the channel
/// without creating a VOD Resume/Continue Watching session for the source episode.
/// </summary>
public sealed class VirtualTvPlaybackStopConsumer : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly LgWebOsLivePlaybackManager _lgWebOsPlayback;
    private readonly LiveTvPlaybackCoordinator _coordinator;
    private readonly PlaybackStateProtectionManager _stateProtection;

    public VirtualTvPlaybackStopConsumer(
        LgWebOsLivePlaybackManager lgWebOsPlayback,
        LiveTvPlaybackCoordinator coordinator,
        PlaybackStateProtectionManager stateProtection)
    {
        _lgWebOsPlayback = lgWebOsPlayback;
        _coordinator = coordinator;
        _stateProtection = stateProtection;
    }

    public async Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        if (await _lgWebOsPlayback.TryHandlePlaybackStopAsync(eventArgs).ConfigureAwait(false))
        {
            return;
        }

        if (eventArgs.Session is not null)
        {
            _stateProtection.RestoreAllIfProtected(eventArgs.Session.Id, eventArgs.PlaySessionId);
        }

        await _coordinator.HandlePlaybackStopAsync(eventArgs).ConfigureAwait(false);
    }
}
