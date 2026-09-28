using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Prevents normal playback-progress reporting from changing the source episode's resume state
/// while it is being used as a Virtual TV transport.
/// </summary>
public sealed class VirtualTvPlaybackProgressConsumer : IEventConsumer<PlaybackProgressEventArgs>
{
    private readonly PlaybackStateProtectionManager _stateProtection;

    public VirtualTvPlaybackProgressConsumer(PlaybackStateProtectionManager stateProtection)
    {
        _stateProtection = stateProtection;
    }

    public Task OnEvent(PlaybackProgressEventArgs eventArgs)
    {
        _stateProtection.RestoreIfProtected(eventArgs, clearAfterRestore: false);
        return Task.CompletedTask;
    }
}
