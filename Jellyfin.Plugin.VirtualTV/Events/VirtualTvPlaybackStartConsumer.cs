using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Coordinates the channel-to-source handoff and keeps protected library state unchanged.
/// LG webOS traditional channels stay in the native rebased Live TV transport instead of
/// receiving the mid-file VOD handoff used by other clients.
/// </summary>
public sealed class VirtualTvPlaybackStartConsumer : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly LgWebOsLivePlaybackManager _lgWebOsPlayback;
    private readonly LiveTvPlaybackCoordinator _coordinator;
    private readonly PlaybackStateProtectionManager _stateProtection;

    public VirtualTvPlaybackStartConsumer(
        LgWebOsLivePlaybackManager lgWebOsPlayback,
        LiveTvPlaybackCoordinator coordinator,
        PlaybackStateProtectionManager stateProtection)
    {
        _lgWebOsPlayback = lgWebOsPlayback;
        _coordinator = coordinator;
        _stateProtection = stateProtection;
    }

    public async Task OnEvent(PlaybackStartEventArgs eventArgs)
    {
        if (await _lgWebOsPlayback.TryHandlePlaybackStartAsync(eventArgs).ConfigureAwait(false))
        {
            return;
        }

        await _coordinator.HandlePlaybackStartAsync(eventArgs).ConfigureAwait(false);
        _stateProtection.RestoreIfProtected(eventArgs, clearAfterRestore: false);
    }
}
