using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Events;

/// <summary>
/// Restores protected user state immediately after a playback-start report.
/// </summary>
public sealed class ProtectedPlaybackStartConsumer : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly PlaybackStateProtectionManager _manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtectedPlaybackStartConsumer"/> class.
    /// </summary>
    public ProtectedPlaybackStartConsumer(PlaybackStateProtectionManager manager)
    {
        _manager = manager;
    }

    /// <inheritdoc />
    public Task OnEvent(PlaybackStartEventArgs eventArgs)
    {
        _manager.RestoreIfProtected(eventArgs, false);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Restores protected user state immediately after each playback-progress report.
/// </summary>
public sealed class ProtectedPlaybackProgressConsumer : IEventConsumer<PlaybackProgressEventArgs>
{
    private readonly PlaybackStateProtectionManager _manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtectedPlaybackProgressConsumer"/> class.
    /// </summary>
    public ProtectedPlaybackProgressConsumer(PlaybackStateProtectionManager manager)
    {
        _manager = manager;
    }

    /// <inheritdoc />
    public Task OnEvent(PlaybackProgressEventArgs eventArgs)
    {
        _manager.RestoreIfProtected(eventArgs, false);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Restores protected user state after playback stops, then removes the temporary protection context.
/// </summary>
public sealed class ProtectedPlaybackStopConsumer : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly PlaybackStateProtectionManager _manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtectedPlaybackStopConsumer"/> class.
    /// </summary>
    public ProtectedPlaybackStopConsumer(PlaybackStateProtectionManager manager)
    {
        _manager = manager;
    }

    /// <inheritdoc />
    public Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        _manager.RestoreIfProtected(eventArgs, true);
        return Task.CompletedTask;
    }
}
