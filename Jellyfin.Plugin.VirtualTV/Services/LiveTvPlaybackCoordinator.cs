using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Converts a native Virtual TV channel tune into normal Jellyfin item playback at the
/// exact wall-clock programme position. The offset is supplied as StartPositionTicks on
/// the PlayNow command, so the player opens the source at the requested timeline position
/// instead of opening at 00:00 and attempting a later Live TV seek.
/// </summary>
public sealed class LiveTvPlaybackCoordinator
{
    // Small allowance for command delivery and player setup. The wall-clock position is
    // calculated immediately before the PlayNow command; this keeps the source within
    // roughly one second of the linear-TV clock without relying on a post-start Seek.
    private static readonly TimeSpan CommandTransitCompensation = TimeSpan.FromSeconds(1);

    // Jellyfin can occasionally emit more than one PlaybackStart notification for the same
    // channel tune. Suppress duplicate handoffs while still allowing an intentional retune.
    private static readonly TimeSpan DuplicateHandoffWindow = TimeSpan.FromSeconds(5);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly PlaybackStateProtectionManager _stateProtection;
    private readonly ILogger<LiveTvPlaybackCoordinator> _logger;
    private readonly ConcurrentDictionary<string, HandoffStamp> _lastHandoff = new(StringComparer.Ordinal);

    public LiveTvPlaybackCoordinator(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        VirtualTvScheduleStore scheduleStore,
        PlaybackStateProtectionManager stateProtection,
        ILogger<LiveTvPlaybackCoordinator> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _scheduleStore = scheduleStore;
        _stateProtection = stateProtection;
        _logger = logger;
    }

    public async Task HandlePlaybackStartAsync(PlaybackStartEventArgs eventArgs)
    {
        if (eventArgs.Item is not LiveTvChannel channel
            || !string.Equals(channel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase)
            || eventArgs.Session is null
            || string.IsNullOrWhiteSpace(eventArgs.Session.Id)
            || !VirtualTvLiveTvService.TryGetConfigurationChannelId(channel.ExternalId, out var configurationChannelId))
        {
            return;
        }

        // Re-evaluate the schedule at handoff time rather than trusting the programme that was
        // current when the Guide row was rendered. This also handles a tune exactly on a boundary.
        var nowUtc = DateTime.UtcNow;
        var activeEntry = FindActiveEntry(configurationChannelId, nowUtc);
        if (activeEntry is null || activeEntry.IsOffAir)
        {
            _logger.LogWarning(
                "Virtual TV cannot hand off session {SessionId}: no active programme was found for channel {ChannelName}.",
                eventArgs.Session.Id,
                channel.Name);
            return;
        }

        if (!Guid.TryParse(activeEntry.SourceItemId, out var sourceItemId))
        {
            _logger.LogError(
                "Virtual TV cannot hand off channel {ChannelName}: schedule entry {EntryId} has invalid source item id {SourceItemId}.",
                channel.Name,
                activeEntry.Id,
                activeEntry.SourceItemId);
            return;
        }

        var sourceItem = _libraryManager.GetItemById(sourceItemId);
        if (sourceItem is null)
        {
            _logger.LogError(
                "Virtual TV cannot hand off channel {ChannelName}: Jellyfin source item {SourceItemId} no longer exists.",
                channel.Name,
                sourceItemId);
            return;
        }

        var sessionId = eventArgs.Session.Id;
        if (IsDuplicateHandoff(sessionId, activeEntry.Id, nowUtc))
        {
            _logger.LogDebug(
                "Virtual TV ignored a duplicate handoff for session {SessionId}, programme {ProgramName}.",
                sessionId,
                activeEntry.Name);
            return;
        }

        var targetTicks = CalculateTargetTicks(activeEntry, sourceItem.RunTimeTicks, nowUtc);
        var target = TimeSpan.FromTicks(targetTicks);

        if (eventArgs.Session.UserId != Guid.Empty)
        {
            if (!_stateProtection.BeginProtection(sessionId, sourceItemId, eventArgs.Session.UserId))
            {
                _logger.LogWarning(
                    "Virtual TV could not capture watched/resume state for {ItemName} in session {SessionId}. Playback will continue, but state protection is unavailable for this tune.",
                    sourceItem.Name,
                    sessionId);
            }
        }

        var command = new PlayRequest
        {
            ItemIds = [sourceItemId],
            StartPositionTicks = targetTicks,
            PlayCommand = PlayCommand.PlayNow
        };

        _logger.LogInformation(
            "Virtual TV handoff: session {SessionId}, channel {ChannelName}, programme {ProgramName}, programme start {ProgramStartUtc:o}, wall clock {NowUtc:o}, source {SourceItemId}, StartPosition {TargetSeconds:F1}s.",
            sessionId,
            channel.Name,
            activeEntry.Name,
            activeEntry.GetStartUtc(),
            nowUtc,
            sourceItemId,
            target.TotalSeconds);

        try
        {
            await _sessionManager.SendPlayCommand(
                sessionId,
                sessionId,
                command,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            _stateProtection.CancelProtection(sessionId, restore: true);
            throw;
        }
    }

    private VirtualTvScheduleEntry? FindActiveEntry(string channelId, DateTime nowUtc)
        => _scheduleStore.Load(channelId)
            .FirstOrDefault(entry =>
                !entry.IsOffAir
                && entry.GetStartUtc() <= nowUtc
                && entry.GetEndUtc() > nowUtc);

    private long CalculateTargetTicks(VirtualTvScheduleEntry entry, long? runTimeTicks, DateTime nowUtc)
    {
        var rawTicks = Math.Max(
            0,
            (nowUtc.Add(CommandTransitCompensation) - entry.GetStartUtc()).Ticks);

        if (!runTimeTicks.HasValue || runTimeTicks.Value <= 0)
        {
            return rawTicks;
        }

        // Never ask Jellyfin to start at or beyond EOF. Keeping one second of media available
        // also avoids an immediate stop when tuning very close to the programme boundary.
        var latestSafeTick = Math.Max(0, runTimeTicks.Value - TimeSpan.TicksPerSecond);
        return Math.Min(rawTicks, latestSafeTick);
    }

    private bool IsDuplicateHandoff(string sessionId, string entryId, DateTime nowUtc)
    {
        var previous = _lastHandoff.GetOrAdd(sessionId, _ => new HandoffStamp(entryId, nowUtc));
        if (string.Equals(previous.EntryId, entryId, StringComparison.Ordinal)
            && nowUtc - previous.CreatedUtc < DuplicateHandoffWindow)
        {
            // The first event creates the stamp and must not be suppressed.
            if (previous.CreatedUtc == nowUtc)
            {
                return false;
            }

            return true;
        }

        _lastHandoff[sessionId] = new HandoffStamp(entryId, nowUtc);
        return false;
    }

    private sealed record HandoffStamp(string EntryId, DateTime CreatedUtc);
}
