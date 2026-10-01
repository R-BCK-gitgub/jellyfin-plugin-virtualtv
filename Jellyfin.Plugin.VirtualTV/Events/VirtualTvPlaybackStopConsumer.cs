using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Restores a protected traditional Personalized TV item only after Jellyfin has already
/// processed PlaybackStop. Watched-dependent modes have no snapshot and keep normal state.
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
        if (eventArgs.Session is not null && eventArgs.Item is not null)
        {
            _stateProtection.RestoreStoppedItem(
                eventArgs.Session.Id,
                eventArgs.Item.Id,
                eventArgs.PlaySessionId);
        }

        await _coordinator.HandlePlaybackStopAsync(eventArgs).ConfigureAwait(false);
    }
}
