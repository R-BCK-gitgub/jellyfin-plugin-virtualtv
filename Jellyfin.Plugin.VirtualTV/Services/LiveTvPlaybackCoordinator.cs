using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Coordinates native Virtual TV playback actions that must happen after a stock Jellyfin client
/// has opened a Live TV channel.
/// </summary>
public sealed class LiveTvPlaybackCoordinator
{
    private static readonly TimeSpan DuplicateGuard = TimeSpan.FromSeconds(15);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LiveTvPlaybackCoordinator> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _recentlyHandled = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvPlaybackCoordinator"/> class.
    /// </summary>
    public LiveTvPlaybackCoordinator(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<LiveTvPlaybackCoordinator> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Applies the real wall-clock position of the currently airing programme when a Virtual TV
    /// channel is launched directly from Jellyfin's native Live TV UI.
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

        var now = DateTime.UtcNow;
        var currentProgram = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvProgram],
            ChannelIds = [channel.Id],
            MaxStartDate = now,
            MinEndDate = now,
            Limit = 1
        })
        .OfType<LiveTvProgram>()
        .OrderByDescending(program => program.StartDate)
        .FirstOrDefault();

        if (currentProgram is null)
        {
            return;
        }

        var targetTicks = Math.Max(0, (now - currentProgram.StartDate).Ticks);

        // A stream restart caused by the seek/track change may emit another PlaybackStart event.
        // If it is already at the requested wall-clock position, there is nothing left to do.
        if (eventArgs.PlaybackPositionTicks >= targetTicks - TimeSpan.FromSeconds(5).Ticks)
        {
            return;
        }

        if (_recentlyHandled.TryGetValue(session.Id, out var lastHandled)
            && now - lastHandled < DuplicateGuard)
        {
            return;
        }

        _recentlyHandled[session.Id] = now;

        _logger.LogInformation(
            "Virtual TV initial Live seek for session {SessionId}: programme {ProgramName}, target {TargetSeconds} seconds.",
            session.Id,
            currentProgram.Name,
            TimeSpan.FromTicks(targetTicks).TotalSeconds);

        // The PlaybackStart event means the stock client has already opened the full-timeline
        // Live TV source. A short delay gives the local player time to attach the HLS/remux source
        // before moving to the wall-clock programme position.
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
