using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Coordinates native Virtual TV playback actions that must happen after a stock Jellyfin client
/// has opened a Live TV channel.
/// </summary>
public sealed class LiveTvPlaybackCoordinator
{
    private static readonly TimeSpan ArchitectureTestOffset = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan DuplicateGuard = TimeSpan.FromSeconds(15);

    private readonly ISessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, DateTime> _recentlyHandled = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvPlaybackCoordinator"/> class.
    /// </summary>
    public LiveTvPlaybackCoordinator(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Applies the wall-clock position to the temporary architecture-test channel when it is
    /// launched directly from Jellyfin's native Live TV UI.
    /// </summary>
    public async Task HandlePlaybackStartAsync(PlaybackStartEventArgs eventArgs)
    {
        if (Plugin.Instance?.Configuration.ArchitectureLiveTvTestEnabled != true
            || eventArgs.Item is not LiveTvChannel channel
            || !string.Equals(channel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(channel.ExternalId, VirtualTvLiveTvService.ArchitectureTestChannelId, StringComparison.Ordinal))
        {
            return;
        }

        var session = eventArgs.Session;
        if (session is null || string.IsNullOrWhiteSpace(session.Id))
        {
            return;
        }

        var targetTicks = ArchitectureTestOffset.Ticks;

        // A stream restart caused by the seek/track change may emit another PlaybackStart event.
        // If it is already at the requested position, there is nothing left to do.
        if (eventArgs.PlaybackPositionTicks >= targetTicks - TimeSpan.FromSeconds(5).Ticks)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_recentlyHandled.TryGetValue(session.Id, out var lastHandled)
            && now - lastHandled < DuplicateGuard)
        {
            return;
        }

        _recentlyHandled[session.Id] = now;

        // The PlaybackStart event means the stock client has already opened the Live TV item.
        // A short delay gives the local player time to attach the transcoded source before seeking.
        await Task.Delay(500).ConfigureAwait(false);

        await _sessionManager.SendPlaystateCommand(
            session.Id,
            session.Id,
            new PlaystateRequest
            {
                Command = PlaystateCommand.Seek,
                SeekPositionTicks = targetTicks
            },
            CancellationToken.None).ConfigureAwait(false);
    }
}
