using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Restores the source episode's pre-tune watched/resume state when Virtual TV playback stops.
/// </summary>
public sealed class VirtualTvPlaybackStopConsumer : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly PlaybackStateProtectionManager _stateProtection;

    public VirtualTvPlaybackStopConsumer(PlaybackStateProtectionManager stateProtection)
    {
        _stateProtection = stateProtection;
    }

    public Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        _stateProtection.RestoreIfProtected(eventArgs, clearAfterRestore: true);
        return Task.CompletedTask;
    }
}
