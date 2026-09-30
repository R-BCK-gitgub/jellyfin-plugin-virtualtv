using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
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
/// Concrete modes (Sequential, Random and Movies) keep the validated 1.8.2 wall-clock
/// StartPositionTicks behavior and protect Jellyfin watched/resume state.
///
/// Dynamic unwatched modes use fixed schedule blocks: the schedule selects the series while
/// VirtualTvEpisodeResolver selects a user-specific episode. Jellyfin state is intentionally
/// allowed to update only for Next Unwatched and Random Unwatched.
/// </summary>
public sealed class LiveTvPlaybackCoordinator
{
    private static readonly TimeSpan CommandTransitCompensation = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TransitionPositionTolerance = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PhysicalEndTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingCommandWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ContinuationFallbackDelay = TimeSpan.FromMilliseconds(1500);
    private const int ManagedStaticQueueLength = 6;

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly VirtualTvEpisodeResolver _episodeResolver;
    private readonly VirtualTvMovieResolver _movieResolver;
    private readonly VirtualTvRuntimeFallbackResolver _runtimeFallback;
    private readonly VirtualTvVisibilityManager _visibility;
    private readonly PlaybackStateProtectionManager _stateProtection;
    private readonly ILogger<LiveTvPlaybackCoordinator> _logger;
    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new(StringComparer.Ordinal);

    public LiveTvPlaybackCoordinator(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        VirtualTvScheduleStore scheduleStore,
        VirtualTvEpisodeResolver episodeResolver,
        VirtualTvMovieResolver movieResolver,
        VirtualTvRuntimeFallbackResolver runtimeFallback,
        VirtualTvVisibilityManager visibility,
        PlaybackStateProtectionManager stateProtection,
        ILogger<LiveTvPlaybackCoordinator> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _scheduleStore = scheduleStore;
        _episodeResolver = episodeResolver;
        _movieResolver = movieResolver;
        _runtimeFallback = runtimeFallback;
        _visibility = visibility;
        _stateProtection = stateProtection;
        _logger = logger;
    }

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

        // The native LiveTvChannel is expected to stop when the source-item handoff occurs.
        if (eventArgs.Item is LiveTvChannel)
        {
            return;
        }

        bool isCurrent;
        bool isReplacementStop;
        long continuationGeneration;

        lock (context.Gate)
        {
            isCurrent = context.CurrentSourceItemId == eventArgs.Item.Id;
            isReplacementStop = !string.IsNullOrWhiteSpace(context.ReplacingPlaySessionId)
                && string.Equals(context.ReplacingPlaySessionId, eventArgs.PlaySessionId, StringComparison.Ordinal);

            if (isReplacementStop)
            {
                context.ReplacingPlaySessionId = string.Empty;
                return;
            }

            if (!isCurrent)
            {
                return;
            }

            var reachedPhysicalEnd = IsAtPhysicalEnd(eventArgs);
            context.AwaitingContinuation = eventArgs.PlayedToCompletion && reachedPhysicalEnd;
            continuationGeneration = ++context.ContinuationGeneration;
        }

        if (!eventArgs.PlayedToCompletion || !IsAtPhysicalEnd(eventArgs))
        {
            EndSession(sessionId, "manual stop");
            return;
        }

        lock (context.Gate)
        {
            context.LastCompletedItemId = eventArgs.Item.Id;
        }

        var nowUtc = DateTime.UtcNow;
        var schedule = LoadSchedule(context.ChannelId);
        var liveEntry = FindActiveEntry(schedule, nowUtc);

        if (liveEntry is null)
        {
            EndSession(sessionId, "programme completed while channel is off air");
            return;
        }

        if (context.IsDynamicUnwatched)
        {
            await HandleDynamicCompletionAsync(
                context,
                liveEntry,
                nowUtc,
                continuationGeneration).ConfigureAwait(false);
            return;
        }

        await HandleConcreteCompletionAsync(
            context,
            schedule,
            liveEntry,
            nowUtc,
            continuationGeneration).ConfigureAwait(false);
    }

    private async Task HandleChannelTuneAsync(
        PlaybackStartEventArgs eventArgs,
        LiveTvChannel channel,
        string configurationChannelId)
    {
        var channelConfiguration = GetChannelConfiguration(configurationChannelId);
        if (channelConfiguration is null)
        {
            _logger.LogWarning(
                "Virtual TV cannot hand off session {SessionId}: channel configuration {ChannelId} is missing.",
                eventArgs.Session!.Id,
                configurationChannelId);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var activeEntry = FindActiveEntry(LoadSchedule(configurationChannelId), nowUtc);
        if (activeEntry is null)
        {
            _logger.LogWarning(
                "Virtual TV cannot hand off session {SessionId}: no active programme was found for channel {ChannelName}.",
                eventArgs.Session!.Id,
                channel.Name);
            return;
        }

        var sessionId = eventArgs.Session!.Id;
        if (_sessions.TryRemove(sessionId, out _))
        {
            _stateProtection.CancelProtection(sessionId, restore: true);
        }

        var contentMode = string.Equals(channelConfiguration.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
            ? (string.Equals(channelConfiguration.ContentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase)
                ? VirtualTvModePolicy.RandomUnwatched
                : VirtualTvModePolicy.Random)
            : VirtualTvModePolicy.NormalizeContentMode(channelConfiguration.ContentMode);

        var playbackUserId = ResolvePlaybackUserId(eventArgs);

        if (playbackUserId != Guid.Empty && !_visibility.IsVisibleToUser(channelConfiguration, playbackUserId))
        {
            _logger.LogWarning(
                "Virtual TV rejected session {SessionId} for channel {ChannelName}: user {UserId} is not allowed to use this channel.",
                sessionId,
                channel.Name,
                playbackUserId);
            return;
        }

        var context = new SessionContext(
            sessionId,
            configurationChannelId,
            playbackUserId,
            channel.Name,
            channelConfiguration.ChannelType,
            contentMode);

        _logger.LogInformation(
            "Virtual TV tune context: session {SessionId}, channel {ChannelName}, mode {Mode}, session user {SessionUserId}, resolved playback user {PlaybackUserId}, event users {EventUserCount}.",
            sessionId,
            channel.Name,
            contentMode,
            eventArgs.Session.UserId,
            playbackUserId,
            eventArgs.Users?.Count ?? 0);

        _sessions[sessionId] = context;

        await PlayScheduledEntryAsync(
            context,
            activeEntry,
            nowUtc,
            "initial channel tune").ConfigureAwait(false);
    }

    private async Task HandleSourcePlaybackStartAsync(PlaybackStartEventArgs eventArgs)
    {
        var sessionId = eventArgs.Session!.Id;
        if (!_sessions.TryGetValue(sessionId, out var context))
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var liveEntry = FindActiveEntry(LoadSchedule(context.ChannelId), nowUtc);
        if (liveEntry is null)
        {
            EndSession(sessionId, "source started while channel has no playable live programme");
            return;
        }

        var startedItemId = eventArgs.Item!.Id;
        bool isCurrent;
        bool isPendingTarget;
        bool isManagedQueueItem;
        bool awaitingContinuation;
        string queueSeriesId;

        lock (context.Gate)
        {
            isCurrent = context.CurrentSourceItemId == startedItemId;
            isPendingTarget = context.PendingTargetItemId == startedItemId
                && nowUtc - context.PendingCommandUtc <= PendingCommandWindow;
            isManagedQueueItem = context.ManagedQueueSourceIds.Contains(startedItemId);
            awaitingContinuation = context.AwaitingContinuation;
            queueSeriesId = context.QueueSeriesId;
        }

        // Stream/subtitle changes can restart the current source. Preserve the viewer's
        // chosen position and do not force a return to live on such restarts.
        if (isCurrent && !awaitingContinuation && !isPendingTarget)
        {
            lock (context.Gate)
            {
                context.CurrentPlaySessionId = eventArgs.PlaySessionId ?? string.Empty;
            }

            return;
        }

        if (context.IsDynamicUnwatched)
        {
            await HandleDynamicSourceStartAsync(
                context,
                liveEntry,
                eventArgs,
                isPendingTarget,
                isManagedQueueItem,
                awaitingContinuation,
                queueSeriesId,
                nowUtc).ConfigureAwait(false);
            return;
        }

        await HandleConcreteSourceStartAsync(
            context,
            liveEntry,
            eventArgs,
            isPendingTarget,
            isManagedQueueItem,
            awaitingContinuation,
            nowUtc).ConfigureAwait(false);
    }

    private async Task HandleDynamicSourceStartAsync(
        SessionContext context,
        VirtualTvScheduleEntry liveEntry,
        PlaybackStartEventArgs eventArgs,
        bool isPendingTarget,
        bool isManagedQueueItem,
        bool awaitingContinuation,
        string queueSeriesId,
        DateTime nowUtc)
    {
        _ = queueSeriesId;
        _ = nowUtc;

        if (!liveEntry.IsDynamicBlock)
        {
            EndSession(context.SessionId, "dynamic playback reached a non-dynamic schedule entry");
            return;
        }

        var startedItemId = eventArgs.Item!.Id;

        if (isPendingTarget && context.PendingTargetItemId == startedItemId)
        {
            AcceptSourceStart(
                context,
                liveEntry,
                startedItemId,
                eventArgs.PlaySessionId,
                startedFromResume: context.PendingStartPositionTicks > 0);
            return;
        }

        // Dynamic channels never trust a client-generated Next Up transition. Re-resolve the
        // wall-clock block and issue one explicit VOD PlayNow.
        if (isManagedQueueItem || awaitingContinuation || isPendingTarget)
        {
            await PlayScheduledEntryAsync(
                context,
                liveEntry,
                DateTime.UtcNow,
                "dynamic playback must be re-resolved from the active schedule block").ConfigureAwait(false);
            return;
        }

        EndSession(context.SessionId, "unrelated playback started");
    }

    private async Task HandleConcreteSourceStartAsync(
        SessionContext context,
        VirtualTvScheduleEntry liveEntry,
        PlaybackStartEventArgs eventArgs,
        bool isPendingTarget,
        bool isManagedQueueItem,
        bool awaitingContinuation,
        DateTime nowUtc)
    {
        if (!Guid.TryParse(liveEntry.SourceItemId, out var liveSourceItemId))
        {
            EndSession(context.SessionId, "concrete schedule entry has an invalid source id");
            return;
        }

        var startedItemId = eventArgs.Item!.Id;

        if (isPendingTarget && context.PendingTargetItemId == startedItemId)
        {
            AcceptSourceStart(
                context,
                liveEntry,
                startedItemId,
                eventArgs.PlaySessionId,
                context.PendingStartPositionTicks > 0);
            return;
        }

        if (startedItemId == liveSourceItemId && (awaitingContinuation || isManagedQueueItem))
        {
            var expectedTicks = CalculateConcreteTargetTicks(liveEntry, eventArgs.Item.RunTimeTicks, nowUtc);
            var actualTicks = Math.Max(0, eventArgs.PlaybackPositionTicks ?? 0);

            if (Math.Abs(expectedTicks - actualTicks) > TransitionPositionTolerance.Ticks)
            {
                await PlayConcreteEntryAsync(
                    context,
                    liveEntry,
                    nowUtc,
                    "managed queue reached correct item at stale position").ConfigureAwait(false);
                return;
            }

            AcceptSourceStart(
                context,
                liveEntry,
                startedItemId,
                eventArgs.PlaySessionId,
                startedFromResume: expectedTicks > 0);
            return;
        }

        if (isManagedQueueItem || awaitingContinuation || isPendingTarget)
        {
            await PlayConcreteEntryAsync(
                context,
                liveEntry,
                nowUtc,
                "client transition did not match current schedule").ConfigureAwait(false);
            return;
        }

        EndSession(context.SessionId, "unrelated playback started");
    }

    private async Task HandleDynamicCompletionAsync(
        SessionContext context,
        VirtualTvScheduleEntry liveEntry,
        DateTime nowUtc,
        long continuationGeneration)
    {
        _ = continuationGeneration;

        if (context.IsDynamicMovie)
        {
            await HandleDynamicMovieCompletionAsync(context, liveEntry, nowUtc).ConfigureAwait(false);
            return;
        }

        if (!liveEntry.IsDynamicBlock
            || !string.Equals(liveEntry.DynamicKind, "Series", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(liveEntry.SourceSeriesId))
        {
            EndSession(context.SessionId, "dynamic series completion reached invalid schedule block");
            return;
        }

        string previousSeriesId;
        lock (context.Gate)
        {
            previousSeriesId = context.QueueSeriesId;
        }

        var sameSeriesBlock = string.Equals(
            previousSeriesId,
            liveEntry.SourceSeriesId,
            StringComparison.OrdinalIgnoreCase);

        // Keep the approved 1.9.1 rule: at physical EOF consult the wall-clock schedule again,
        // then resolve one episode from whichever series owns the active block.
        await PlayDynamicSeriesEntryAsync(
            context,
            liveEntry,
            sameSeriesBlock
                ? "episode ended; active block is still the same series"
                : "episode ended; schedule has moved to another series block").ConfigureAwait(false);
    }

    private async Task HandleDynamicMovieCompletionAsync(
        SessionContext context,
        VirtualTvScheduleEntry liveEntry,
        DateTime nowUtc)
    {
        string currentEntryId;
        lock (context.Gate)
        {
            currentEntryId = context.CurrentEntryId;
        }

        if (string.Equals(liveEntry.Id, currentEntryId, StringComparison.Ordinal)
            && nowUtc < liveEntry.GetEndUtc())
        {
            // Movies are the one watched-dependent mode that allows planned dead air inside
            // the nominal block. Keep the managed session alive and start the next live block
            // when its boundary arrives.
            var delay = liveEntry.GetEndUtc() - nowUtc;
            _logger.LogInformation(
                "Virtual TV movie ended before block boundary on {ChannelName}; next block begins in {DelaySeconds:F0}s.",
                context.ChannelName,
                delay.TotalSeconds);

            var localBoundary = TimeZoneInfo.ConvertTimeFromUtc(liveEntry.GetEndUtc(), TimeZoneInfo.Local);
            await TrySendMessageAsync(
                context,
                "Virtual TV",
                $"Next movie starts at {localBoundary:HH:mm}.",
                delay).ConfigureAwait(false);

            ScheduleDynamicMovieBoundary(context.SessionId, liveEntry.GetEndUtc());
            return;
        }

        await PlayDynamicMovieEntryAsync(
            context,
            liveEntry,
            "movie ended; resolve the block that is live now").ConfigureAwait(false);
    }

    private async Task HandleConcreteCompletionAsync(
        SessionContext context,
        IReadOnlyList<VirtualTvScheduleEntry> schedule,
        VirtualTvScheduleEntry liveEntry,
        DateTime nowUtc,
        long continuationGeneration)
    {
        string currentEntryId;
        lock (context.Gate)
        {
            currentEntryId = context.CurrentEntryId;
        }

        bool runtimeFallbackActive;
        Guid? currentFallbackItem;
        lock (context.Gate)
        {
            runtimeFallbackActive = context.RuntimeFallbackActive;
            currentFallbackItem = context.CurrentSourceItemId;
        }

        if (runtimeFallbackActive
            && string.Equals(liveEntry.Id, currentEntryId, StringComparison.Ordinal))
        {
            await PlayTraditionalFallbackAsync(
                context,
                liveEntry,
                currentFallbackItem,
                "runtime fallback ended while the unavailable schedule block is still live").ConfigureAwait(false);
            return;
        }

        // Fast-forward to physical EOF before the scheduled programme end: return to the
        // same concrete episode at the current wall-clock live position.
        if (string.Equals(liveEntry.Id, currentEntryId, StringComparison.Ordinal))
        {
            await PlayConcreteEntryAsync(
                context,
                liveEntry,
                nowUtc,
                "completed before scheduled end; return to live").ConfigureAwait(false);
            return;
        }

        var nextEntry = FindNextPlayableEntry(schedule, currentEntryId);
        var liveOffset = nowUtc - liveEntry.GetStartUtc();

        if (nextEntry is not null
            && string.Equals(nextEntry.Id, liveEntry.Id, StringComparison.Ordinal)
            && liveOffset >= TimeSpan.Zero
            && liveOffset <= TransitionPositionTolerance)
        {
            ScheduleContinuationFallback(context.SessionId, continuationGeneration);
            return;
        }

        await PlayConcreteEntryAsync(
            context,
            liveEntry,
            nowUtc,
            "completed away from live boundary; resynchronise to live").ConfigureAwait(false);
    }

    private async Task PlayTraditionalFallbackAsync(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        Guid? failedItemId,
        string reason)
    {
        var channel = GetChannelConfiguration(context.ChannelId);
        if (channel is null)
        {
            EndSession(context.SessionId, "runtime fallback lost channel configuration");
            return;
        }

        var fallback = _runtimeFallback.ResolveTraditionalFallback(channel, entry, failedItemId);
        if (fallback is null)
        {
            EndSession(context.SessionId, "no eligible runtime fallback content");
            return;
        }

        var singleItem = new[] { fallback.Id };

        if (context.UserId != Guid.Empty)
        {
            _stateProtection.BeginProtection(context.SessionId, singleItem, context.UserId);
        }

        lock (context.Gate)
        {
            context.RuntimeFallbackActive = true;
        }

        PreparePendingCommand(
            context,
            entry,
            fallback.Id,
            singleItem,
            string.Empty,
            startPositionTicks: 0);

        var command = new PlayRequest
        {
            ItemIds = singleItem,
            StartPositionTicks = 0,
            StartIndex = 0,
            PlayCommand = PlayCommand.PlayNow
        };

        var fallbackMessage = string.Equals(channel.ContentMode, VirtualTvModePolicy.Random, StringComparison.OrdinalIgnoreCase)
            ? "This content is not available. Selecting another random item…"
            : "This content is not available. Playing the next eligible item…";

        await TrySendMessageAsync(
            context,
            "Virtual TV",
            fallbackMessage,
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        _logger.LogWarning(
            "Virtual TV local runtime fallback on {ChannelName}: {Reason}; playing {FallbackItemId} without changing persisted Guide.",
            context.ChannelName,
            reason,
            fallback.Id);

        await SendPlayCommandAsync(context, command).ConfigureAwait(false);
    }

    private Task PlayScheduledEntryAsync(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        DateTime nowUtc,
        string reason)
    {
        if (!context.IsDynamicUnwatched)
        {
            return PlayConcreteEntryAsync(context, entry, nowUtc, reason);
        }

        return context.IsDynamicMovie
            ? PlayDynamicMovieEntryAsync(context, entry, reason)
            : PlayDynamicSeriesEntryAsync(context, entry, reason);
    }

    private async Task PlayDynamicSeriesEntryAsync(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        string reason)
    {
        if (!entry.IsDynamicBlock
            || !string.Equals(entry.DynamicKind, "Series", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(entry.SourceSeriesId, out var seriesId))
        {
            _logger.LogError(
                "Virtual TV cannot resolve dynamic series block {EntryId}: invalid series id {SeriesId}.",
                entry.Id,
                entry.SourceSeriesId);
            EndSession(context.SessionId, "invalid dynamic series schedule block");
            return;
        }

        if (context.UserId == Guid.Empty)
        {
            EndSession(context.SessionId, "watched-dependent playback requires an authenticated user");
            return;
        }

        var channel = GetChannelConfiguration(context.ChannelId);
        if (channel is null)
        {
            EndSession(context.SessionId, "channel configuration disappeared");
            return;
        }

        VirtualTvEpisodeResolver.EpisodeResolution resolution;
        try
        {
            resolution = _episodeResolver.ResolveEpisode(
                context.UserId,
                channel,
                seriesId,
                context.LastCompletedItemId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Virtual TV could not resolve an episode for series {SeriesName} in session {SessionId}.",
                entry.SeriesName,
                context.SessionId);
            EndSession(context.SessionId, "dynamic episode resolver returned no playable content");
            return;
        }

        // Approved 1.9.1 behavior: exactly one concrete Episode, normal Jellyfin VOD player,
        // always 00:00 for series watched-dependent modes.
        var singleEpisode = new[] { resolution.ItemId };

        PreparePendingCommand(
            context,
            entry,
            resolution.ItemId,
            singleEpisode,
            entry.SourceSeriesId,
            startPositionTicks: 0);

        var command = new PlayRequest
        {
            ItemIds = singleEpisode,
            StartPositionTicks = 0,
            StartIndex = 0,
            PlayCommand = PlayCommand.PlayNow
        };

        _logger.LogInformation(
            "Virtual TV dynamic series VOD handoff: session {SessionId}, channel {ChannelName}, series {SeriesName}, mode {Mode}, reason {Reason}, selected episode {SourceItemId}, selection {SelectionReason}, playback starts 0.0s.",
            context.SessionId,
            context.ChannelName,
            entry.SeriesName,
            context.ContentMode,
            reason,
            resolution.ItemId,
            resolution.SelectionReason);

        await SendPlayCommandAsync(context, command).ConfigureAwait(false);
    }

    private async Task PlayDynamicMovieEntryAsync(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        string reason)
    {
        if (!entry.IsDynamicBlock
            || !string.Equals(entry.DynamicKind, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            EndSession(context.SessionId, "invalid dynamic movie schedule block");
            return;
        }

        var channel = GetChannelConfiguration(context.ChannelId);
        if (channel is null || context.UserId == Guid.Empty)
        {
            EndSession(context.SessionId, "movie watched-dependent playback requires an authenticated user");
            return;
        }

        Guid? scheduledId = Guid.TryParse(entry.SourceItemId, out var parsed) ? parsed : null;

        VirtualTvMovieResolver.MovieResolution resolution;
        try
        {
            resolution = _movieResolver.Resolve(
                context.UserId,
                channel,
                scheduledId,
                context.LastCompletedItemId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Virtual TV could not resolve a movie for channel {ChannelName}.", context.ChannelName);
            EndSession(context.SessionId, "dynamic movie resolver returned no playable content");
            return;
        }

        if (resolution.ReplacedScheduledItem)
        {
            await TrySendMessageAsync(
                context,
                "Virtual TV",
                "This movie has already been watched or is unavailable. Selecting another unwatched movie…",
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        var singleMovie = new[] { resolution.ItemId };
        PreparePendingCommand(
            context,
            entry,
            resolution.ItemId,
            singleMovie,
            string.Empty,
            resolution.StartPositionTicks);

        var command = new PlayRequest
        {
            ItemIds = singleMovie,
            StartPositionTicks = resolution.StartPositionTicks,
            StartIndex = 0,
            PlayCommand = PlayCommand.PlayNow
        };

        _logger.LogInformation(
            "Virtual TV dynamic movie VOD handoff: session {SessionId}, channel {ChannelName}, reason {Reason}, source {SourceItemId}, selection {SelectionReason}, resume {ResumeSeconds:F1}s.",
            context.SessionId,
            context.ChannelName,
            reason,
            resolution.ItemId,
            resolution.SelectionReason,
            TimeSpan.FromTicks(resolution.StartPositionTicks).TotalSeconds);

        await SendPlayCommandAsync(context, command).ConfigureAwait(false);
    }

    private async Task PlayConcreteEntryAsync(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        DateTime nowUtc,
        string reason)
    {
        if (!Guid.TryParse(entry.SourceItemId, out var sourceItemId))
        {
            _logger.LogError(
                "Virtual TV cannot play programme {ProgramName}: invalid source item id {SourceItemId}.",
                entry.Name,
                entry.SourceItemId);
            EndSession(context.SessionId, "invalid concrete schedule item");
            return;
        }

        var sourceItem = _libraryManager.GetItemById(sourceItemId);
        if (sourceItem is null)
        {
            await PlayTraditionalFallbackAsync(
                context,
                entry,
                sourceItemId,
                "scheduled source item no longer exists").ConfigureAwait(false);
            return;
        }

        lock (context.Gate)
        {
            context.RuntimeFallbackActive = false;
        }

        var queueItemIds = BuildConcreteManagedQueue(context.ChannelId, entry);
        if (queueItemIds.Length == 0)
        {
            queueItemIds = [sourceItemId];
        }

        // Sequential, Random and Movies behave like TV and must not alter Jellyfin watched,
        // resume, play count or last-played state.
        if (!context.TracksJellyfinState && context.UserId != Guid.Empty)
        {
            if (!_stateProtection.BeginProtection(context.SessionId, queueItemIds, context.UserId))
            {
                _logger.LogWarning(
                    "Virtual TV could not capture watched/resume state for session {SessionId}.",
                    context.SessionId);
            }
        }

        var targetTicks = CalculateConcreteTargetTicks(entry, sourceItem.RunTimeTicks, nowUtc);

        PreparePendingCommand(
            context,
            entry,
            sourceItemId,
            queueItemIds,
            string.Empty,
            targetTicks);

        var command = new PlayRequest
        {
            ItemIds = queueItemIds,
            StartPositionTicks = targetTicks,
            StartIndex = 0,
            PlayCommand = PlayCommand.PlayNow
        };

        _logger.LogInformation(
            "Virtual TV concrete play command: session {SessionId}, channel {ChannelName}, programme {ProgramName}, mode {Mode}, reason {Reason}, wall clock {NowUtc:o}, source {SourceItemId}, StartPosition {TargetSeconds:F1}s, queue {QueueCount} item(s).",
            context.SessionId,
            context.ChannelName,
            entry.Name,
            context.ContentMode,
            reason,
            nowUtc,
            sourceItemId,
            TimeSpan.FromTicks(targetTicks).TotalSeconds,
            queueItemIds.Length);

        await SendPlayCommandAsync(context, command).ConfigureAwait(false);
    }

    private void PreparePendingCommand(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        Guid targetItemId,
        Guid[] queueItemIds,
        string queueSeriesId,
        long startPositionTicks)
    {
        lock (context.Gate)
        {
            context.ReplacingPlaySessionId = context.CurrentPlaySessionId;
            context.PendingTargetItemId = targetItemId;
            context.PendingCommandUtc = DateTime.UtcNow;
            context.PendingStartPositionTicks = startPositionTicks;
            context.ManagedQueueOrder = queueItemIds;
            context.ManagedQueueSourceIds = queueItemIds.ToHashSet();
            context.QueueSeriesId = queueSeriesId;
            context.CurrentEntryId = entry.Id;
            context.AwaitingContinuation = false;
            context.ContinuationGeneration++;
        }
    }

    private async Task TrySendMessageAsync(
        SessionContext context,
        string header,
        string message,
        TimeSpan timeout)
    {
        try
        {
            var timeoutMs = Math.Clamp(
                (long)timeout.TotalMilliseconds,
                3000,
                (long)TimeSpan.FromMinutes(15).TotalMilliseconds);

            await _sessionManager.SendMessageCommand(
                context.SessionId,
                context.SessionId,
                new MessageCommand
                {
                    Header = header,
                    Text = message,
                    TimeoutMs = timeoutMs
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Client message support is best-effort and must never interfere with playback.
            _logger.LogDebug(ex, "Virtual TV client {SessionId} did not accept a display message.", context.SessionId);
        }
    }

    private async Task SendPlayCommandAsync(SessionContext context, PlayRequest command)
    {
        try
        {
            await _sessionManager.SendPlayCommand(
                context.SessionId,
                context.SessionId,
                command,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            EndSession(context.SessionId, "play command failed");
            throw;
        }
    }

    private void AcceptSourceStart(
        SessionContext context,
        VirtualTvScheduleEntry liveEntry,
        Guid sourceItemId,
        string? playSessionId,
        bool startedFromResume)
    {
        lock (context.Gate)
        {
            context.CurrentEntryId = liveEntry.Id;
            context.CurrentSourceItemId = sourceItemId;
            context.CurrentPlaySessionId = playSessionId ?? string.Empty;
            context.CurrentStartedFromResume = startedFromResume;
            context.PendingTargetItemId = null;
            context.PendingCommandUtc = DateTime.MinValue;
            context.PendingStartPositionTicks = 0;
            context.ReplacingPlaySessionId = string.Empty;
            context.AwaitingContinuation = false;
            context.ContinuationGeneration++;
        }

        _logger.LogDebug(
            "Virtual TV accepted source start for channel {ChannelName}, programme {ProgramName}, item {ItemId}.",
            context.ChannelName,
            liveEntry.SeriesName.Length > 0 ? liveEntry.SeriesName : liveEntry.Name,
            sourceItemId);
    }

    private Guid[] BuildConcreteManagedQueue(string channelId, VirtualTvScheduleEntry activeEntry)
    {
        var schedule = LoadSchedule(channelId);
        var startIndex = schedule.FindIndex(item => string.Equals(item.Id, activeEntry.Id, StringComparison.Ordinal));
        if (startIndex < 0)
        {
            return [];
        }

        var result = new List<Guid>(ManagedStaticQueueLength);
        var seenSourceItems = new HashSet<Guid>();

        for (var index = startIndex; index < schedule.Count && result.Count < ManagedStaticQueueLength; index++)
        {
            var candidate = schedule[index];
            if (candidate.IsOffAir || candidate.IsDynamicBlock)
            {
                break;
            }

            if (!Guid.TryParse(candidate.SourceItemId, out var itemId)
                || _libraryManager.GetItemById(itemId) is null)
            {
                continue;
            }

            if (seenSourceItems.Add(itemId))
            {
                result.Add(itemId);
            }
        }

        return result.ToArray();
    }

    private void ScheduleDynamicMovieBoundary(string sessionId, DateTime boundaryUtc)
        => _ = ResumeDynamicMovieAtBoundaryAsync(sessionId, boundaryUtc);

    private async Task ResumeDynamicMovieAtBoundaryAsync(string sessionId, DateTime boundaryUtc)
    {
        try
        {
            var delay = boundaryUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }

            if (!_sessions.TryGetValue(sessionId, out var context) || !context.IsDynamicMovie)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var liveEntry = FindActiveEntry(LoadSchedule(context.ChannelId), nowUtc);
            if (liveEntry is null)
            {
                EndSession(sessionId, "movie boundary reached Off Air or unavailable schedule");
                return;
            }

            await PlayDynamicMovieEntryAsync(
                context,
                liveEntry,
                "nominal movie block boundary reached").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Virtual TV movie boundary continuation failed for session {SessionId}.", sessionId);
        }
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

            lock (context.Gate)
            {
                if (!context.AwaitingContinuation || context.ContinuationGeneration != generation)
                {
                    return;
                }
            }

            var nowUtc = DateTime.UtcNow;
            var liveEntry = FindActiveEntry(LoadSchedule(context.ChannelId), nowUtc);
            if (liveEntry is null)
            {
                EndSession(sessionId, "continuation watchdog found channel off air");
                return;
            }

            await PlayScheduledEntryAsync(
                context,
                liveEntry,
                nowUtc,
                "continuation watchdog").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Virtual TV continuation watchdog failed for session {SessionId}.", sessionId);
        }
    }

    private void ScheduleContinuationFallback(string sessionId, long generation)
        => _ = EnsureContinuationAsync(sessionId, generation);

    private static Guid ResolvePlaybackUserId(PlaybackStartEventArgs eventArgs)
    {
        if (eventArgs.Session is not null && eventArgs.Session.UserId != Guid.Empty)
        {
            return eventArgs.Session.UserId;
        }

        if (eventArgs.Users is not null)
        {
            var eventUser = eventArgs.Users.FirstOrDefault(user => user.Id != Guid.Empty);
            if (eventUser is not null)
            {
                return eventUser.Id;
            }
        }

        return Guid.Empty;
    }

    private ChannelConfiguration? GetChannelConfiguration(string channelId)
        => Plugin.Instance?.Configuration.Channels.FirstOrDefault(
            channel => string.Equals(channel.Id, channelId, StringComparison.OrdinalIgnoreCase));

    private List<VirtualTvScheduleEntry> LoadSchedule(string channelId)
        => _scheduleStore.Load(channelId)
            .OrderBy(item => item.GetStartUtc())
            .ToList();

    private static VirtualTvScheduleEntry? FindActiveEntry(
        IReadOnlyList<VirtualTvScheduleEntry> schedule,
        DateTime nowUtc)
        => schedule.FirstOrDefault(entry =>
            !entry.IsOffAir
            && !entry.IsContentUnavailable
            && !entry.IsScheduleUnavailable
            && entry.GetStartUtc() <= nowUtc
            && entry.GetEndUtc() > nowUtc);

    private static VirtualTvScheduleEntry? FindNextPlayableEntry(
        IReadOnlyList<VirtualTvScheduleEntry> schedule,
        string currentEntryId)
    {
        var index = -1;
        for (var candidate = 0; candidate < schedule.Count; candidate++)
        {
            if (string.Equals(schedule[candidate].Id, currentEntryId, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0 || index + 1 >= schedule.Count)
        {
            return null;
        }

        var next = schedule[index + 1];
        return next.IsOffAir ? null : next;
    }

    private static bool IsAtPhysicalEnd(PlaybackStopEventArgs eventArgs)
    {
        if (!eventArgs.Item.RunTimeTicks.HasValue || eventArgs.Item.RunTimeTicks.Value <= 0)
        {
            return eventArgs.PlayedToCompletion;
        }

        var positionTicks = Math.Max(0, eventArgs.PlaybackPositionTicks ?? 0);
        var remainingTicks = eventArgs.Item.RunTimeTicks.Value - positionTicks;
        return remainingTicks <= PhysicalEndTolerance.Ticks;
    }

    private static long CalculateConcreteTargetTicks(
        VirtualTvScheduleEntry entry,
        long? runTimeTicks,
        DateTime nowUtc)
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

    private void EndSession(string sessionId, string reason)
    {
        if (!_sessions.TryRemove(sessionId, out var context))
        {
            return;
        }

        if (!context.TracksJellyfinState)
        {
            _stateProtection.CancelProtection(sessionId, restore: true);
        }

        _logger.LogInformation(
            "Virtual TV ended managed playback session {SessionId} for channel {ChannelName}: {Reason}.",
            sessionId,
            context.ChannelName,
            reason);
    }

    private sealed class SessionContext
    {
        public SessionContext(
            string sessionId,
            string channelId,
            Guid userId,
            string channelName,
            string channelType,
            string contentMode)
        {
            SessionId = sessionId;
            ChannelId = channelId;
            UserId = userId;
            ChannelName = channelName;
            ChannelType = channelType;
            ContentMode = contentMode;
            IsDynamicUnwatched = VirtualTvModePolicy.IsDynamicUnwatched(contentMode);
            IsDynamicMovie = IsDynamicUnwatched
                && string.Equals(channelType, "Movies", StringComparison.OrdinalIgnoreCase);
            TracksJellyfinState = VirtualTvModePolicy.TracksJellyfinState(contentMode);
        }

        public object Gate { get; } = new();

        public string SessionId { get; }

        public string ChannelId { get; }

        public Guid UserId { get; }

        public string ChannelName { get; }

        public string ChannelType { get; }

        public string ContentMode { get; }

        public bool IsDynamicUnwatched { get; }

        public bool IsDynamicMovie { get; }

        public bool TracksJellyfinState { get; }

        public string CurrentEntryId { get; set; } = string.Empty;

        public Guid? CurrentSourceItemId { get; set; }

        public Guid? LastCompletedItemId { get; set; }

        public string CurrentPlaySessionId { get; set; } = string.Empty;

        public bool CurrentStartedFromResume { get; set; }

        public Guid? PendingTargetItemId { get; set; }

        public DateTime PendingCommandUtc { get; set; }

        public long PendingStartPositionTicks { get; set; }

        public Guid[] ManagedQueueOrder { get; set; } = [];

        public HashSet<Guid> ManagedQueueSourceIds { get; set; } = [];

        public string QueueSeriesId { get; set; } = string.Empty;

        public string ReplacingPlaySessionId { get; set; } = string.Empty;

        public bool AwaitingContinuation { get; set; }

        public bool RuntimeFallbackActive { get; set; }

        public long ContinuationGeneration { get; set; }
    }
}
