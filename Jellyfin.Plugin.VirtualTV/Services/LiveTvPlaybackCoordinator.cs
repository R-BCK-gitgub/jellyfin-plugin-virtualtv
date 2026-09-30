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
/// Coordinates Virtual TV playback with one deterministic VOD handoff per tune.
///
/// The Live TV channel is only the entry point. Once Jellyfin reports that the channel was
/// opened, Virtual TV resolves the programme that owns the wall clock, resolves the concrete
/// media item, and sends exactly one PlayNow request for that item.
///
/// No Current+Next queue, client auto-next dependency, delayed seek, continuation watchdog or
/// client-specific playback command is used. Every physical EOF re-reads the schedule and opens
/// exactly the item that should be live at that instant.
/// </summary>
public sealed class LiveTvPlaybackCoordinator
{
    private static readonly TimeSpan InitialHandoffDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CommandTransitCompensation = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PhysicalEndTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DuplicateTuneFallbackWindow = TimeSpan.FromSeconds(2);

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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _commandGates = new(StringComparer.Ordinal);
    private long _nextGeneration;

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

        HandleSourcePlaybackStart(eventArgs);
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

        // The bootstrap LiveTvChannel is expected to stop when PlayNow opens the real item.
        if (eventArgs.Item is LiveTvChannel)
        {
            return;
        }

        bool isCurrent;
        bool isReplacementStop;

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
        }

        // Old stop events from a superseded tune are harmless. Never let them close the new tune.
        if (!isCurrent)
        {
            _logger.LogDebug(
                "Virtual TV ignored stale stop for item {ItemId} in session {SessionId}, generation {Generation}.",
                eventArgs.Item.Id,
                sessionId,
                context.Generation);
            return;
        }

        if (!eventArgs.PlayedToCompletion || !IsAtPhysicalEnd(eventArgs))
        {
            EndSession(sessionId, context.Generation, "manual stop");
            return;
        }

        lock (context.Gate)
        {
            context.LastCompletedItemId = eventArgs.Item.Id;
        }

        // Schedule-authoritative EOF. The player never decides what comes next.
        var nowUtc = DateTime.UtcNow;
        var liveEntry = FindActiveEntry(LoadSchedule(context.ChannelId), nowUtc);
        if (liveEntry is null)
        {
            EndSession(sessionId, context.Generation, "programme completed while channel is off air");
            return;
        }

        await PlayScheduledEntryAsync(
            context,
            liveEntry,
            nowUtc,
            "physical EOF; re-resolve current schedule").ConfigureAwait(false);
    }

    private async Task HandleChannelTuneAsync(
        PlaybackStartEventArgs eventArgs,
        LiveTvChannel channel,
        string configurationChannelId)
    {
        var channelConfiguration = GetChannelConfiguration(configurationChannelId);
        if (channelConfiguration is null)
        {
            return;
        }

        var session = eventArgs.Session!;
        var sessionId = session.Id;
        var nowUtc = DateTime.UtcNow;
        var activeEntry = FindActiveEntry(LoadSchedule(configurationChannelId), nowUtc);

        if (activeEntry is null)
        {
            _logger.LogWarning(
                "Virtual TV cannot hand off session {SessionId}: no active programme for {ChannelName}.",
                sessionId,
                channel.Name);
            return;
        }

        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            var samePlaySession = !string.IsNullOrWhiteSpace(eventArgs.PlaySessionId)
                && string.Equals(existing.ChannelPlaySessionId, eventArgs.PlaySessionId, StringComparison.Ordinal);

            var fallbackDuplicate = string.IsNullOrWhiteSpace(eventArgs.PlaySessionId)
                && string.Equals(existing.ChannelId, configurationChannelId, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - existing.CreatedUtc < DuplicateTuneFallbackWindow;

            if (samePlaySession || fallbackDuplicate)
            {
                _logger.LogDebug(
                    "Virtual TV ignored duplicate channel tune for session {SessionId}, generation {Generation}.",
                    sessionId,
                    existing.Generation);
                return;
            }
        }

        var contentMode = string.Equals(channelConfiguration.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
            ? (string.Equals(channelConfiguration.ContentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase)
                ? VirtualTvModePolicy.RandomUnwatched
                : VirtualTvModePolicy.Random)
            : VirtualTvModePolicy.NormalizeContentMode(channelConfiguration.ContentMode);

        var playbackUserId = ResolvePlaybackUserId(eventArgs);
        if (playbackUserId != Guid.Empty && !_visibility.IsVisibleToUser(channelConfiguration, playbackUserId))
        {
            return;
        }

        var tracksState = VirtualTvModePolicy.TracksJellyfinState(contentMode);

        if (_sessions.TryRemove(sessionId, out var previous))
        {
            previous.Cancel();

            // Traditional channels share one protection envelope while the user surfs between
            // traditional Virtual TV channels. Switching into a watched-dependent channel closes
            // that envelope so the new VOD item can update Jellyfin normally.
            if (!previous.TracksJellyfinState && tracksState)
            {
                _stateProtection.CompleteProtection(sessionId);
            }
        }

        var context = new SessionContext(
            sessionId,
            configurationChannelId,
            playbackUserId,
            channel.Name,
            channelConfiguration.ChannelType,
            contentMode,
            eventArgs.PlaySessionId ?? string.Empty,
            Interlocked.Increment(ref _nextGeneration));

        _sessions[sessionId] = context;

        _logger.LogInformation(
            "Virtual TV tune {Generation}: session {SessionId}, channel {ChannelName}, mode {Mode}, active entry {EntryId}.",
            context.Generation,
            sessionId,
            channel.Name,
            contentMode,
            activeEntry.Id);

        try
        {
            // One small, cancellable settling delay. No playback command is sent until the
            // stock client has had a chance to attach its Live TV player. A newer tune cancels it.
            await Task.Delay(InitialHandoffDelay, context.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!IsCurrent(context))
        {
            return;
        }

        await PlayScheduledEntryAsync(
            context,
            activeEntry,
            DateTime.UtcNow,
            "initial channel tune").ConfigureAwait(false);
    }

    private void HandleSourcePlaybackStart(PlaybackStartEventArgs eventArgs)
    {
        var sessionId = eventArgs.Session!.Id;
        if (!_sessions.TryGetValue(sessionId, out var context))
        {
            return;
        }

        var startedItemId = eventArgs.Item!.Id;
        Guid? pendingTarget;
        Guid? currentItem;

        lock (context.Gate)
        {
            pendingTarget = context.PendingTargetItemId;
            currentItem = context.CurrentSourceItemId;
        }

        if (pendingTarget.HasValue && pendingTarget.Value == startedItemId)
        {
            lock (context.Gate)
            {
                context.CurrentSourceItemId = startedItemId;
                context.CurrentPlaySessionId = eventArgs.PlaySessionId ?? string.Empty;
                context.PendingTargetItemId = null;
                context.ReplacingPlaySessionId = string.Empty;
            }

            _logger.LogDebug(
                "Virtual TV accepted source item {ItemId} for session {SessionId}, generation {Generation}.",
                startedItemId,
                sessionId,
                context.Generation);
            return;
        }

        if (currentItem.HasValue && currentItem.Value == startedItemId)
        {
            // Normal VOD restart after subtitle/audio track change or a manual seek.
            lock (context.Gate)
            {
                context.CurrentPlaySessionId = eventArgs.PlaySessionId ?? string.Empty;
            }

            return;
        }

        if (pendingTarget.HasValue)
        {
            // Most important race guard in 1.10.4: a late start from the old channel/item cannot
            // terminate or overwrite the tune that is currently waiting for its real episode.
            _logger.LogDebug(
                "Virtual TV ignored stale source start {ItemId}; session {SessionId} generation {Generation} is waiting for {PendingItemId}.",
                startedItemId,
                sessionId,
                context.Generation,
                pendingTarget.Value);
            return;
        }

        // No handoff is pending and the user opened unrelated media: the Virtual TV session ended.
        EndSession(sessionId, context.Generation, "unrelated playback started");
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
            EndSession(context.SessionId, context.Generation, "invalid dynamic series block");
            return;
        }

        var channel = GetChannelConfiguration(context.ChannelId);
        if (channel is null || context.UserId == Guid.Empty)
        {
            EndSession(context.SessionId, context.Generation, "watched-dependent series has no active user");
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
            _logger.LogWarning(ex, "Virtual TV could not resolve a dynamic episode on {ChannelName}.", context.ChannelName);
            EndSession(context.SessionId, context.Generation, "dynamic episode resolution failed");
            return;
        }

        PreparePendingCommand(context, entry, resolution.ItemId);

        await SendSinglePlayNowAsync(
            context,
            resolution.ItemId,
            0,
            reason + "; " + resolution.SelectionReason).ConfigureAwait(false);
    }

    private async Task PlayDynamicMovieEntryAsync(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        string reason)
    {
        if (!entry.IsDynamicBlock
            || !string.Equals(entry.DynamicKind, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            EndSession(context.SessionId, context.Generation, "invalid dynamic movie block");
            return;
        }

        var channel = GetChannelConfiguration(context.ChannelId);
        if (channel is null || context.UserId == Guid.Empty)
        {
            EndSession(context.SessionId, context.Generation, "watched-dependent movie has no active user");
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
            _logger.LogWarning(ex, "Virtual TV could not resolve a dynamic movie on {ChannelName}.", context.ChannelName);
            EndSession(context.SessionId, context.Generation, "dynamic movie resolution failed");
            return;
        }

        PreparePendingCommand(context, entry, resolution.ItemId);

        await SendSinglePlayNowAsync(
            context,
            resolution.ItemId,
            resolution.StartPositionTicks,
            reason + "; " + resolution.SelectionReason).ConfigureAwait(false);
    }

    private async Task PlayConcreteEntryAsync(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        DateTime nowUtc,
        string reason)
    {
        if (!Guid.TryParse(entry.SourceItemId, out var sourceItemId))
        {
            await PlayTraditionalFallbackAsync(
                context,
                entry,
                null,
                "scheduled source id is invalid").ConfigureAwait(false);
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

        if (!context.TracksJellyfinState && context.UserId != Guid.Empty)
        {
            _stateProtection.BeginProtection(context.SessionId, new[] { sourceItemId }, context.UserId);
        }

        var targetTicks = CalculateConcreteTargetTicks(entry, sourceItem.RunTimeTicks, nowUtc);
        PreparePendingCommand(context, entry, sourceItemId);

        await SendSinglePlayNowAsync(
            context,
            sourceItemId,
            targetTicks,
            reason).ConfigureAwait(false);
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
            EndSession(context.SessionId, context.Generation, "fallback lost channel configuration");
            return;
        }

        var fallback = _runtimeFallback.ResolveTraditionalFallback(channel, entry, failedItemId);
        if (fallback is null)
        {
            EndSession(context.SessionId, context.Generation, "no eligible fallback content");
            return;
        }

        if (!context.TracksJellyfinState && context.UserId != Guid.Empty)
        {
            _stateProtection.BeginProtection(context.SessionId, new[] { fallback.Id }, context.UserId);
        }

        PreparePendingCommand(context, entry, fallback.Id);

        await SendSinglePlayNowAsync(
            context,
            fallback.Id,
            0,
            reason + "; local fallback").ConfigureAwait(false);
    }

    private void PreparePendingCommand(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        Guid targetItemId)
    {
        lock (context.Gate)
        {
            context.ReplacingPlaySessionId = context.CurrentPlaySessionId;
            context.PendingTargetItemId = targetItemId;
            context.CurrentEntryId = entry.Id;
        }
    }

    private async Task SendSinglePlayNowAsync(
        SessionContext context,
        Guid itemId,
        long startPositionTicks,
        string reason)
    {
        var gate = _commandGates.GetOrAdd(context.SessionId, _ => new SemaphoreSlim(1, 1));

        try
        {
            await gate.WaitAsync(context.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!IsCurrent(context))
            {
                return;
            }

            var request = new PlayRequest
            {
                ItemIds = new[] { itemId },
                StartPositionTicks = Math.Max(0, startPositionTicks),
                StartIndex = 0,
                PlayCommand = PlayCommand.PlayNow
            };

            _logger.LogInformation(
                "Virtual TV PlayNow {Generation}: session {SessionId}, channel {ChannelName}, item {ItemId}, start {StartSeconds:F1}s, reason {Reason}.",
                context.Generation,
                context.SessionId,
                context.ChannelName,
                itemId,
                TimeSpan.FromTicks(Math.Max(0, startPositionTicks)).TotalSeconds,
                reason);

            await _sessionManager.SendPlayCommand(
                context.SessionId,
                context.SessionId,
                request,
                context.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
            // A newer tune superseded this request.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Virtual TV PlayNow failed for session {SessionId}, generation {Generation}.",
                context.SessionId,
                context.Generation);

            EndSession(context.SessionId, context.Generation, "PlayNow failed");
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsCurrent(SessionContext context)
        => _sessions.TryGetValue(context.SessionId, out var current)
            && ReferenceEquals(current, context)
            && current.Generation == context.Generation
            && !context.Token.IsCancellationRequested;

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

    private static bool IsAtPhysicalEnd(PlaybackStopEventArgs eventArgs)
    {
        if (!eventArgs.Item.RunTimeTicks.HasValue || eventArgs.Item.RunTimeTicks.Value <= 0)
        {
            return eventArgs.PlayedToCompletion;
        }

        var positionTicks = Math.Max(0, eventArgs.PlaybackPositionTicks ?? 0);
        return eventArgs.Item.RunTimeTicks.Value - positionTicks <= PhysicalEndTolerance.Ticks;
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

    private void EndSession(string sessionId, long generation, string reason)
    {
        if (!_sessions.TryGetValue(sessionId, out var context)
            || context.Generation != generation
            || !_sessions.TryRemove(sessionId, out context))
        {
            return;
        }

        context.Cancel();

        if (!context.TracksJellyfinState)
        {
            _stateProtection.CompleteProtection(sessionId);
        }

        _logger.LogInformation(
            "Virtual TV ended session {SessionId}, generation {Generation}, channel {ChannelName}: {Reason}.",
            sessionId,
            generation,
            context.ChannelName,
            reason);
    }

    private sealed class SessionContext
    {
        private readonly CancellationTokenSource _lifetime = new();

        public SessionContext(
            string sessionId,
            string channelId,
            Guid userId,
            string channelName,
            string channelType,
            string contentMode,
            string channelPlaySessionId,
            long generation)
        {
            SessionId = sessionId;
            ChannelId = channelId;
            UserId = userId;
            ChannelName = channelName;
            ChannelType = channelType;
            ContentMode = contentMode;
            ChannelPlaySessionId = channelPlaySessionId;
            Generation = generation;
            IsDynamicUnwatched = VirtualTvModePolicy.IsDynamicUnwatched(contentMode);
            IsDynamicMovie = IsDynamicUnwatched
                && string.Equals(channelType, "Movies", StringComparison.OrdinalIgnoreCase);
            TracksJellyfinState = VirtualTvModePolicy.TracksJellyfinState(contentMode);
        }

        public object Gate { get; } = new();

        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

        public string SessionId { get; }

        public string ChannelId { get; }

        public Guid UserId { get; }

        public string ChannelName { get; }

        public string ChannelType { get; }

        public string ContentMode { get; }

        public string ChannelPlaySessionId { get; }

        public long Generation { get; }

        public bool IsDynamicUnwatched { get; }

        public bool IsDynamicMovie { get; }

        public bool TracksJellyfinState { get; }

        public string CurrentEntryId { get; set; } = string.Empty;

        public Guid? CurrentSourceItemId { get; set; }

        public Guid? LastCompletedItemId { get; set; }

        public string CurrentPlaySessionId { get; set; } = string.Empty;

        public Guid? PendingTargetItemId { get; set; }

        public string ReplacingPlaySessionId { get; set; } = string.Empty;

        public CancellationToken Token => _lifetime.Token;

        public void Cancel()
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _lifetime.Cancel();
            }
        }
    }
}
