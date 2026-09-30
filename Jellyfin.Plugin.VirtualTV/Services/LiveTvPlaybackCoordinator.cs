using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Owns Virtual TV playback sessions.
///
/// A channel tune is handed off to normal Jellyfin item playback using the wall-clock
/// StartPositionTicks established in 1.8.1. 1.8.2 additionally supplies a short queue built
/// from the Virtual TV schedule (preventing Jellyfin from expanding an Episode into the next
/// episodes of the same series) and re-validates the schedule whenever playback transitions.
/// </summary>
public sealed class LiveTvPlaybackCoordinator
{
    private static readonly TimeSpan CommandTransitCompensation = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TransitionPositionTolerance = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PendingCommandWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ContinuationFallbackDelay = TimeSpan.FromMilliseconds(1500);
    private const int ManagedQueueLength = 6;

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly PlaybackStateProtectionManager _stateProtection;
    private readonly ILogger<LiveTvPlaybackCoordinator> _logger;
    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Handles both the initial native Live TV channel start and subsequent source-item starts.
    /// </summary>
    public async Task HandlePlaybackStartAsync(PlaybackStartEventArgs eventArgs)
    {
        if (eventArgs.Session is null || string.IsNullOrWhiteSpace(eventArgs.Session.Id) || eventArgs.Item is null)
        {
            return;
        }

        if (eventArgs.Item is LiveTvChannel channel
            && string.Equals(channel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase)
            && VirtualTvLiveTvService.TryGetConfigurationChannelId(channel.ExternalId, out var configurationChannelId))
        {
            await HandleChannelTuneAsync(eventArgs, channel, configurationChannelId).ConfigureAwait(false);
            return;
        }

        await HandleSourcePlaybackStartAsync(eventArgs).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the end of a source item. Manual stops end the Virtual TV session. Natural
    /// completion re-resolves the live programme so early fast-forward and late time-shift
    /// both converge back to the channel's actual wall-clock position.
    /// </summary>
    public async Task HandlePlaybackStopAsync(PlaybackStopEventArgs eventArgs)
    {
        if (eventArgs.Session is null || string.IsNullOrWhiteSpace(eventArgs.Session.Id) || eventArgs.Item is null)
        {
            return;
        }

        var sessionId = eventArgs.Session.Id;
        if (!_sessions.TryGetValue(sessionId, out var context))
        {
            return;
        }

        // The native TvChannel is expected to stop when the 1.8.1 source-item handoff occurs.
        if (eventArgs.Item is LiveTvChannel)
        {
            return;
        }

        var itemId = eventArgs.Item.Id;
        bool isCurrent;
        bool isReplacementStop;
        string channelId;
        string currentEntryId;
        long continuationGeneration;

        lock (context.Gate)
        {
            context.LastActivityUtc = DateTime.UtcNow;
            isCurrent = context.CurrentSourceItemId == itemId;
            isReplacementStop = !string.IsNullOrWhiteSpace(context.ReplacingPlaySessionId)
                && string.Equals(context.ReplacingPlaySessionId, eventArgs.PlaySessionId, StringComparison.Ordinal);

            channelId = context.ChannelId;
            currentEntryId = context.CurrentEntryId;

            if (isReplacementStop)
            {
                context.ReplacingPlaySessionId = string.Empty;
                return;
            }

            if (!isCurrent)
            {
                // Ignore stale stop notifications from an item that is no longer the managed source.
                return;
            }

            if (!eventArgs.PlayedToCompletion)
            {
                // Back/Stop is a user decision. Do not "fight" the client by reopening the channel.
                context.AwaitingContinuation = false;
                continuationGeneration = ++context.ContinuationGeneration;
            }
            else
            {
                context.AwaitingContinuation = true;
                continuationGeneration = ++context.ContinuationGeneration;
            }
        }

        if (!eventArgs.PlayedToCompletion)
        {
            EndSession(sessionId, "manual stop");
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var schedule = LoadSchedule(channelId);
        var liveEntry = FindActiveEntry(schedule, nowUtc);
        if (liveEntry is null)
        {
            EndSession(sessionId, "programme completed while channel is off air");
            return;
        }

        // If the physical file ended before its scheduled block, the user fast-forwarded.
        // Return immediately to the same programme at the current wall-clock position.
        if (string.Equals(liveEntry.Id, currentEntryId, StringComparison.Ordinal))
        {
            await PlayLiveEntryAsync(
                context,
                liveEntry,
                nowUtc,
                "completed before scheduled end; return to live").ConfigureAwait(false);
            return;
        }

        var nextEntry = FindNextPlayableEntry(schedule, currentEntryId);
        var liveOffset = nowUtc - liveEntry.GetStartUtc();

        // The normal case: the file and schedule ended together and the managed queue already
        // contains the next scheduled programme. Let the client transition naturally so the
        // change is instant. A short watchdog below takes over if auto-play is disabled.
        if (nextEntry is not null
            && string.Equals(nextEntry.Id, liveEntry.Id, StringComparison.Ordinal)
            && liveOffset >= TimeSpan.Zero
            && liveOffset <= TransitionPositionTolerance)
        {
            ScheduleContinuationFallback(sessionId, continuationGeneration);
            return;
        }

        // The viewer was behind the live edge (for example after rewinding) or multiple schedule
        // blocks have elapsed. Skip stale queue entries and rejoin the programme that is live now.
        await PlayLiveEntryAsync(
            context,
            liveEntry,
            nowUtc,
            "completed away from live boundary; resynchronise to live").ConfigureAwait(false);
    }

    private async Task HandleChannelTuneAsync(
        PlaybackStartEventArgs eventArgs,
        LiveTvChannel channel,
        string configurationChannelId)
    {
        var nowUtc = DateTime.UtcNow;
        var schedule = LoadSchedule(configurationChannelId);
        var activeEntry = FindActiveEntry(schedule, nowUtc);

        if (activeEntry is null)
        {
            _logger.LogWarning(
                "Virtual TV cannot hand off session {SessionId}: no active programme was found for channel {ChannelName}.",
                eventArgs.Session!.Id,
                channel.Name);
            return;
        }

        var sessionId = eventArgs.Session!.Id;
        var context = new SessionContext(
            configurationChannelId,
            eventArgs.Session.UserId,
            channel.Name);

        if (_sessions.TryRemove(sessionId, out _))
        {
            _stateProtection.CancelProtection(sessionId, restore: true);
        }

        _sessions[sessionId] = context;

        await PlayLiveEntryAsync(
            context,
            activeEntry,
            nowUtc,
            "initial channel tune",
            sessionId).ConfigureAwait(false);
    }

    private async Task HandleSourcePlaybackStartAsync(PlaybackStartEventArgs eventArgs)
    {
        var sessionId = eventArgs.Session!.Id;
        if (!_sessions.TryGetValue(sessionId, out var context))
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var schedule = LoadSchedule(context.ChannelId);
        var liveEntry = FindActiveEntry(schedule, nowUtc);

        if (liveEntry is null || !Guid.TryParse(liveEntry.SourceItemId, out var liveSourceItemId))
        {
            EndSession(sessionId, "source started while channel has no playable live programme");
            return;
        }

        var startedItemId = eventArgs.Item!.Id;
        bool isCurrent;
        bool isPendingTarget;
        bool isManagedQueueItem;
        bool awaitingContinuation;

        lock (context.Gate)
        {
            context.LastActivityUtc = nowUtc;

            isCurrent = context.CurrentSourceItemId == startedItemId;
            isPendingTarget = context.PendingTargetItemId == startedItemId
                && nowUtc - context.PendingCommandUtc <= PendingCommandWindow;
            isManagedQueueItem = context.ManagedQueueSourceIds.Contains(startedItemId);
            awaitingContinuation = context.AwaitingContinuation;
        }

        // A restart of the item the viewer is already watching can be caused by normal client
        // operations such as stream/subtitle changes. Preserve the viewer's chosen time-shift;
        // do not force such restarts back to live.
        if (isCurrent && !awaitingContinuation && !isPendingTarget)
        {
            lock (context.Gate)
            {
                context.CurrentPlaySessionId = eventArgs.PlaySessionId ?? string.Empty;
            }

            return;
        }

        // Expected target of a plugin-issued handoff/resync. 1.8.1 already proved the native
        // StartPositionTicks path on LG webOS, so accept the player position without a second seek.
        if (isPendingTarget && startedItemId == liveSourceItemId)
        {
            AcceptSourceStart(context, liveEntry, startedItemId, eventArgs.PlaySessionId);
            return;
        }

        if (startedItemId == liveSourceItemId && (awaitingContinuation || isManagedQueueItem))
        {
            // A natural managed-queue transition is only correct if it also lands near the
            // wall-clock position. At a normal boundary both values are ~0. If the viewer had
            // been behind live, reissue PlayNow with the proper StartPositionTicks.
            var expectedTicks = CalculateTargetTicks(liveEntry, eventArgs.Item.RunTimeTicks, nowUtc);
            var actualTicks = Math.Max(0, eventArgs.PlaybackPositionTicks ?? 0);

            if (Math.Abs(expectedTicks - actualTicks) > TransitionPositionTolerance.Ticks)
            {
                await PlayLiveEntryAsync(
                    context,
                    liveEntry,
                    nowUtc,
                    "managed queue reached correct item at stale position",
                    sessionId).ConfigureAwait(false);
                return;
            }

            AcceptSourceStart(context, liveEntry, startedItemId, eventArgs.PlaySessionId);
            return;
        }

        if (isManagedQueueItem || awaitingContinuation || isPendingTarget)
        {
            // The client moved to a queued/stale item that is not actually live. This is the
            // key correction for early fast-forward and late time-shift transitions.
            await PlayLiveEntryAsync(
                context,
                liveEntry,
                nowUtc,
                "client transition did not match current schedule",
                sessionId).ConfigureAwait(false);
            return;
        }

        // An unrelated item started with no Virtual TV transition in progress. Treat that as the
        // user intentionally leaving the virtual channel and do not interfere with normal playback.
        EndSession(sessionId, "unrelated playback started");
    }

    private void AcceptSourceStart(
        SessionContext context,
        VirtualTvScheduleEntry liveEntry,
        Guid sourceItemId,
        string? playSessionId)
    {
        lock (context.Gate)
        {
            context.CurrentEntryId = liveEntry.Id;
            context.CurrentSourceItemId = sourceItemId;
            context.CurrentPlaySessionId = playSessionId ?? string.Empty;
            context.PendingTargetItemId = null;
            context.PendingTargetEntryId = string.Empty;
            context.PendingCommandUtc = DateTime.MinValue;
            context.ReplacingPlaySessionId = string.Empty;
            context.AwaitingContinuation = false;
            context.ContinuationGeneration++;
            context.LastActivityUtc = DateTime.UtcNow;
        }

        _logger.LogDebug(
            "Virtual TV accepted source start for channel {ChannelName}, programme {ProgramName}, item {ItemId}.",
            context.ChannelName,
            liveEntry.Name,
            sourceItemId);
    }

    private async Task PlayLiveEntryAsync(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        DateTime nowUtc,
        string reason,
        string? explicitSessionId = null)
    {
        var sessionId = explicitSessionId ?? FindSessionId(context);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (!Guid.TryParse(entry.SourceItemId, out var sourceItemId))
        {
            _logger.LogError(
                "Virtual TV cannot play programme {ProgramName}: invalid source item id {SourceItemId}.",
                entry.Name,
                entry.SourceItemId);
            return;
        }

        var sourceItem = _libraryManager.GetItemById(sourceItemId);
        if (sourceItem is null)
        {
            _logger.LogError(
                "Virtual TV cannot play programme {ProgramName}: Jellyfin source item {SourceItemId} no longer exists.",
                entry.Name,
                sourceItemId);
            return;
        }

        var queue = BuildManagedQueue(context.ChannelId, entry);
        if (queue.Count == 0)
        {
            queue = [new QueueEntry(entry, sourceItemId)];
        }

        var queueItemIds = queue.Select(item => item.SourceItemId).ToArray();

        if (context.UserId != Guid.Empty)
        {
            if (!_stateProtection.BeginProtection(sessionId, queueItemIds, context.UserId))
            {
                _logger.LogWarning(
                    "Virtual TV could not capture watched/resume state for the managed queue in session {SessionId}. Playback will continue, but state protection is unavailable for this tune.",
                    sessionId);
            }
        }

        var targetTicks = CalculateTargetTicks(entry, sourceItem.RunTimeTicks, nowUtc);
        var target = TimeSpan.FromTicks(targetTicks);

        lock (context.Gate)
        {
            context.ReplacingPlaySessionId = context.CurrentPlaySessionId;
            context.PendingTargetItemId = sourceItemId;
            context.PendingTargetEntryId = entry.Id;
            context.PendingCommandUtc = nowUtc;
            context.ManagedQueueSourceIds = queueItemIds.ToHashSet();
            context.AwaitingContinuation = false;
            context.ContinuationGeneration++;
            context.LastActivityUtc = nowUtc;
        }

        var command = new PlayRequest
        {
            ItemIds = queueItemIds,
            StartPositionTicks = targetTicks,
            StartIndex = 0,
            PlayCommand = PlayCommand.PlayNow
        };

        _logger.LogInformation(
            "Virtual TV play command: session {SessionId}, channel {ChannelName}, programme {ProgramName}, reason {Reason}, programme start {ProgramStartUtc:o}, wall clock {NowUtc:o}, source {SourceItemId}, StartPosition {TargetSeconds:F1}s, managed queue {QueueCount} item(s).",
            sessionId,
            context.ChannelName,
            entry.Name,
            reason,
            entry.GetStartUtc(),
            nowUtc,
            sourceItemId,
            target.TotalSeconds,
            queueItemIds.Length);

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
            EndSession(sessionId, "play command failed");
            throw;
        }
    }

    private IReadOnlyList<QueueEntry> BuildManagedQueue(string channelId, VirtualTvScheduleEntry activeEntry)
    {
        var schedule = LoadSchedule(channelId);
        var startIndex = schedule.FindIndex(item => string.Equals(item.Id, activeEntry.Id, StringComparison.Ordinal));
        if (startIndex < 0)
        {
            return Array.Empty<QueueEntry>();
        }

        var result = new List<QueueEntry>(ManagedQueueLength);
        var seenSourceItems = new HashSet<Guid>();

        for (var index = startIndex; index < schedule.Count && result.Count < ManagedQueueLength; index++)
        {
            var candidate = schedule[index];

            if (candidate.IsOffAir)
            {
                // Do not silently queue media across an Off Air boundary.
                break;
            }

            if (!Guid.TryParse(candidate.SourceItemId, out var itemId))
            {
                continue;
            }

            // Jellyfin Web sorts a multi-item remote-play request by the supplied ID list.
            // Keeping IDs unique avoids ambiguous indexOf ordering if an episode appears again.
            if (!seenSourceItems.Add(itemId))
            {
                continue;
            }

            result.Add(new QueueEntry(candidate, itemId));
        }

        return result;
    }

    private async Task EnsureContinuationAsync(string sessionId, long generation)
    {
        try
        {
            await Task.Delay(ContinuationFallbackDelay).ConfigureAwait(false);

            if (!_sessions.TryGetValue(sessionId, out var context))
            {
                return;
            }

            bool stillWaiting;
            lock (context.Gate)
            {
                stillWaiting = context.AwaitingContinuation
                    && context.ContinuationGeneration == generation;
            }

            if (!stillWaiting)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var liveEntry = FindActiveEntry(LoadSchedule(context.ChannelId), nowUtc);
            if (liveEntry is null)
            {
                EndSession(sessionId, "continuation watchdog found channel off air");
                return;
            }

            await PlayLiveEntryAsync(
                context,
                liveEntry,
                nowUtc,
                "continuation watchdog",
                sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Virtual TV continuation watchdog failed for session {SessionId}.", sessionId);
        }
    }

    private void ScheduleContinuationFallback(string sessionId, long generation)
        => _ = EnsureContinuationAsync(sessionId, generation);

    private List<VirtualTvScheduleEntry> LoadSchedule(string channelId)
        => _scheduleStore.Load(channelId)
            .OrderBy(item => item.GetStartUtc())
            .ToList();

    private static VirtualTvScheduleEntry? FindActiveEntry(
        IReadOnlyList<VirtualTvScheduleEntry> schedule,
        DateTime nowUtc)
        => schedule.FirstOrDefault(entry =>
            !entry.IsOffAir
            && entry.GetStartUtc() <= nowUtc
            && entry.GetEndUtc() > nowUtc);

    private static VirtualTvScheduleEntry? FindNextPlayableEntry(
        IReadOnlyList<VirtualTvScheduleEntry> schedule,
        string currentEntryId)
    {
        for (var index = 0; index < schedule.Count; index++)
        {
            if (!string.Equals(schedule[index].Id, currentEntryId, StringComparison.Ordinal))
            {
                continue;
            }

            for (var next = index + 1; next < schedule.Count; next++)
            {
                if (!schedule[next].IsOffAir)
                {
                    return schedule[next];
                }

                // Off Air is a real boundary; do not treat content after it as an immediate next item.
                return null;
            }

            break;
        }

        return null;
    }

    private long CalculateTargetTicks(VirtualTvScheduleEntry entry, long? runTimeTicks, DateTime nowUtc)
    {
        var rawTicks = Math.Max(
            0,
            (nowUtc.Add(CommandTransitCompensation) - entry.GetStartUtc()).Ticks);

        if (!runTimeTicks.HasValue || runTimeTicks.Value <= 0)
        {
            return rawTicks;
        }

        var latestSafeTick = Math.Max(0, runTimeTicks.Value - TimeSpan.TicksPerSecond);
        return Math.Min(rawTicks, latestSafeTick);
    }

    private string? FindSessionId(SessionContext context)
        => _sessions.FirstOrDefault(pair => ReferenceEquals(pair.Value, context)).Key;

    private void EndSession(string sessionId, string reason)
    {
        if (!_sessions.TryRemove(sessionId, out var context))
        {
            return;
        }

        _stateProtection.CancelProtection(sessionId, restore: true);

        _logger.LogInformation(
            "Virtual TV ended managed playback session {SessionId} for channel {ChannelName}: {Reason}.",
            sessionId,
            context.ChannelName,
            reason);
    }

    private sealed class SessionContext
    {
        public SessionContext(string channelId, Guid userId, string channelName)
        {
            ChannelId = channelId;
            UserId = userId;
            ChannelName = channelName;
            LastActivityUtc = DateTime.UtcNow;
        }

        public object Gate { get; } = new();

        public string ChannelId { get; }

        public Guid UserId { get; }

        public string ChannelName { get; }

        public string CurrentEntryId { get; set; } = string.Empty;

        public Guid? CurrentSourceItemId { get; set; }

        public string CurrentPlaySessionId { get; set; } = string.Empty;

        public Guid? PendingTargetItemId { get; set; }

        public string PendingTargetEntryId { get; set; } = string.Empty;

        public DateTime PendingCommandUtc { get; set; }

        public HashSet<Guid> ManagedQueueSourceIds { get; set; } = [];

        public string ReplacingPlaySessionId { get; set; } = string.Empty;

        public bool AwaitingContinuation { get; set; }

        public long ContinuationGeneration { get; set; }

        public DateTime LastActivityUtc { get; set; }
    }

    private sealed record QueueEntry(VirtualTvScheduleEntry Entry, Guid SourceItemId);
}
