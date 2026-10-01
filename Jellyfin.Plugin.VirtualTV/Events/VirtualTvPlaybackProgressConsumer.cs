using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Uses actual playback progress to confirm that the neutral Virtual TV loading source is really
/// running before the five-second VOD handoff buffer starts. Watched-state protection remains
/// independent and is still restored for traditional Virtual TV modes.
/// </summary>
public sealed class VirtualTvPlaybackProgressConsumer : IEventConsumer<PlaybackProgressEventArgs>
{
    private readonly LiveTvPlaybackCoordinator _coordinator;
    private readonly PlaybackStateProtectionManager _stateProtection;

    public VirtualTvPlaybackProgressConsumer(
        LiveTvPlaybackCoordinator coordinator,
        PlaybackStateProtectionManager stateProtection)
    {
        _coordinator = coordinator;
        _stateProtection = stateProtection;
    }

    public async Task OnEvent(PlaybackProgressEventArgs eventArgs)
    {
        await _coordinator.HandlePlaybackProgressAsync(eventArgs).ConfigureAwait(false);
        _stateProtection.RestoreIfProtected(eventArgs, clearAfterRestore: false);
    }
}
