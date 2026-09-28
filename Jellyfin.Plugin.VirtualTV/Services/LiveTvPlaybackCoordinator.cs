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
/// Keeps Virtual TV playback aligned with the materialized programme timeline.
/// It also remembers the most recent position for the active play session so a
/// source restart (for example when changing subtitles) can resume at the same
/// point instead of falling back to the beginning of the source file.
/// </summary>
public sealed class LiveTvPlaybackCoordinator
{
    private static readonly TimeSpan ProgressMemoryWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommandDebounce = TimeSpan.FromSeconds(1);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LiveTvPlaybackCoordinator> _logger;
    private readonly ConcurrentDictionary<string, PlaybackSnapshot> _recentProgress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _recentCommands = new(StringComparer.Ordinal);

    public LiveTvPlaybackCoordinator(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<LiveTvPlaybackCoordinator> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task HandlePlaybackProgressAsync(PlaybackProgressEventArgs eventArgs)
    {
        if (!TryGetVirtualTvSession(eventArgs, out var channel, out var sessionId)
            || !eventArgs.PlaybackPositionTicks.HasValue)
        {
            return Task.CompletedTask;
        }

        _recentProgress[sessionId] = new PlaybackSnapshot(
            channel.Id,
            eventArgs.PlaySessionId ?? string.Empty,
            Math.Max(0, eventArgs.PlaybackPositionTicks.Value),
            eventArgs.IsPaused,
            DateTime.UtcNow);

        return Task.CompletedTask;
    }

    public async Task HandlePlaybackStartAsync(PlaybackStartEventArgs eventArgs)
    {
        if (!TryGetVirtualTvSession(eventArgs, out var channel, out var sessionId))
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

        var liveTargetTicks = Math.Max(0, (now - currentProgram.StartDate).Ticks);
        var targetTicks = liveTargetTicks;
        var reason = "wall-clock Live position";

        // A subtitle/audio-track change can cause the stock player to reopen the Live TV
        // media source at 00:00. If Jellyfin keeps the same PlaySessionId, restore the
        // latest position instead of unexpectedly jumping the viewer back to the start.
        if (_recentProgress.TryGetValue(sessionId, out var snapshot)
            && snapshot.ChannelId == channel.Id
            && now - snapshot.ObservedUtc <= ProgressMemoryWindow
            && !string.IsNullOrWhiteSpace(snapshot.PlaySessionId)
            && string.Equals(snapshot.PlaySessionId, eventArgs.PlaySessionId, StringComparison.Ordinal))
        {
            var elapsed = snapshot.IsPaused ? TimeSpan.Zero : now - snapshot.ObservedUtc;
            targetTicks = Math.Max(0, snapshot.PositionTicks + elapsed.Ticks);
            reason = "recent position after stream restart";
        }

        var reportedPosition = Math.Max(0, eventArgs.PlaybackPositionTicks ?? 0);
        if (Math.Abs(reportedPosition - targetTicks) <= TimeSpan.FromSeconds(5).Ticks)
        {
            return;
        }

        var commandKey = sessionId + "|" + (eventArgs.PlaySessionId ?? string.Empty);
        if (_recentCommands.TryGetValue(commandKey, out var lastCommand)
            && now - lastCommand < CommandDebounce)
        {
            return;
        }

        _recentCommands[commandKey] = now;

        _logger.LogInformation(
            "Virtual TV seek for session {SessionId}: channel {ChannelName}, programme {ProgramName}, target {TargetSeconds} seconds ({Reason}).",
            sessionId,
            channel.Name,
            currentProgram.Name,
            TimeSpan.FromTicks(targetTicks).TotalSeconds,
            reason);

        await Task.Delay(500).ConfigureAwait(false);

        await _sessionManager.SendPlaystateCommand(
            sessionId,
            sessionId,
            new PlaystateRequest
            {
                Command = PlaystateCommand.Seek,
                SeekPositionTicks = targetTicks
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static bool TryGetVirtualTvSession(
        PlaybackProgressEventArgs eventArgs,
        out LiveTvChannel channel,
        out string sessionId)
    {
        channel = null!;
        sessionId = string.Empty;

        if (eventArgs.Item is not LiveTvChannel liveTvChannel
            || !string.Equals(liveTvChannel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (eventArgs.Session is null || string.IsNullOrWhiteSpace(eventArgs.Session.Id))
        {
            return false;
        }

        channel = liveTvChannel;
        sessionId = eventArgs.Session.Id;
        return true;
    }

    private sealed record PlaybackSnapshot(
        Guid ChannelId,
        string PlaySessionId,
        long PositionTicks,
        bool IsPaused,
        DateTime ObservedUtc);
}
