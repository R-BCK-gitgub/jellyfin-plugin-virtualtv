using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Library;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Virtual TV playback is intentionally a two-stage loop:
///
///   Live TV bootstrap ("Loading Virtual TV...") -> one resolved VOD item -> Live TV bootstrap -> ...
///
/// The Live TV layer never plays scheduled library media. It is only a neutral 10-second loading
/// surface. PlaybackStart only arms the bootstrap; after the first real PlaybackProgress report,
/// a fixed 1.5-second buffer runs before the coordinator resolves the schedule/rules and sends one PlayNow
/// for exactly one concrete episode/movie. At physical EOF it always returns to the Live TV
/// bootstrap first; only after that bootstrap reports progress and then runs for 1.5 more seconds
/// does it resolve the next VOD.
///
/// This keeps client state transitions explicit and serial, avoids VOD-to-VOD auto-next races, and
/// gives webOS/Android TV time to tear down one player before the next request is issued.
/// Concrete VOD items are sent as raw one-item Play messages so Jellyfin cannot expand an episode
/// into the rest of its series; Virtual TV never exposes a native Next Up item.
/// </summary>
public sealed class LiveTvPlaybackCoordinator
{
    private static readonly TimeSpan BootstrapBuffer = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan VodSeekSettleBuffer = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VodTeardownBuffer = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CommandTransitCompensation = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PhysicalEndTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DuplicateTuneFallbackWindow = TimeSpan.FromSeconds(2);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
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
        IUserManager userManager,
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
        _userManager = userManager;
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

        if (eventArgs.Item is LiveTvChannel liveChannel
            && string.Equals(liveChannel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase)
            && VirtualTvLiveTvService.TryGetConfigurationChannelId(liveChannel.ExternalId, out var configurationChannelId))
        {
            var channel = GetChannelConfiguration(configurationChannelId);
            if (channel is null || VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience))
            {
                return;
            }

            await HandleBootstrapStartAsync(eventArgs, liveChannel, configurationChannelId).ConfigureAwait(false);
            return;
        }

        HandleVodStart(eventArgs);
    }

    public Task HandlePlaybackProgressAsync(PlaybackProgressEventArgs eventArgs)
    {
        if (eventArgs.Session is null
            || string.IsNullOrWhiteSpace(eventArgs.Session.Id)
            || eventArgs.Item is null)
        {
            return Task.CompletedTask;
        }

        if (eventArgs.Item is LiveTvChannel liveChannel)
        {
            if (!string.Equals(liveChannel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase)
                || !VirtualTvLiveTvService.TryGetConfigurationChannelId(liveChannel.ExternalId, out var configurationChannelId))
            {
                return Task.CompletedTask;
            }

            var channel = GetChannelConfiguration(configurationChannelId);
            if (channel is null || VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience))
            {
                return Task.CompletedTask;
            }

            return HandleBootstrapProgress(eventArgs, liveChannel, configurationChannelId);
        }

        return HandleVodProgress(eventArgs);
    }

    private Task HandleBootstrapProgress(
        PlaybackProgressEventArgs eventArgs,
        LiveTvChannel liveChannel,
        string configurationChannelId)
    {
        var sessionId = eventArgs.Session!.Id;
        if (!_sessions.TryGetValue(sessionId, out var context)
            || context.LiveChannelItemId != liveChannel.Id
            || !string.Equals(context.ChannelId, configurationChannelId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        lock (context.Gate)
        {
            if (context.Phase != PlaybackPhase.BootstrapWaitingForProgress)
            {
                return Task.CompletedTask;
            }

            context.Phase = PlaybackPhase.BootstrapBuffering;
            context.BootstrapFirstProgressUtc = DateTime.UtcNow;
            context.BootstrapFirstProgressTicks = Math.Max(0, eventArgs.PlaybackPositionTicks ?? 0);
        }

        _logger.LogInformation(
            "Virtual TV loading playback confirmed for session {SessionId}, generation {Generation}, channel {ChannelName} at {PositionSeconds:F2}s. Starting 1.5-second buffer now.",
            sessionId,
            context.Generation,
            context.ChannelName,
            TimeSpan.FromTicks(context.BootstrapFirstProgressTicks).TotalSeconds);

        _ = CompleteBootstrapBufferAsync(context);
        return Task.CompletedTask;
    }

    private Task HandleVodProgress(PlaybackProgressEventArgs eventArgs)
    {
        var sessionId = eventArgs.Session!.Id;
        if (!_sessions.TryGetValue(sessionId, out var context))
        {
            return Task.CompletedTask;
        }

        var itemId = eventArgs.Item!.Id;
        long seekTicks;

        lock (context.Gate)
        {
            if (context.Phase != PlaybackPhase.Vod
                || !context.CurrentVodItemId.HasValue
                || context.CurrentVodItemId.Value != itemId
                || context.PendingSeekTicks <= 0
                || context.SeekScheduled)
            {
                return Task.CompletedTask;
            }

            context.SeekScheduled = true;
            seekTicks = context.PendingSeekTicks;
        }

        _logger.LogInformation(
            "Virtual TV VOD playback confirmed for session {SessionId}, generation {Generation}, item {ItemId}; arming one safe seek to {SeekSeconds:F1}s.",
            sessionId,
            context.Generation,
            itemId,
            TimeSpan.FromTicks(seekTicks).TotalSeconds);

        _ = CompleteVodSeekAsync(context, itemId, seekTicks);
        return Task.CompletedTask;
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

        if (eventArgs.Item is LiveTvChannel)
        {
            HandleBootstrapStop(eventArgs, context);
            return;
        }

        Guid? currentItem;
        string replacingPlaySession;
        lock (context.Gate)
        {
            currentItem = context.CurrentVodItemId;
            replacingPlaySession = context.ReplacingPlaySessionId;
        }

        if (!string.IsNullOrWhiteSpace(replacingPlaySession)
            && string.Equals(replacingPlaySession, eventArgs.PlaySessionId, StringComparison.Ordinal))
        {
            lock (context.Gate)
            {
                context.ReplacingPlaySessionId = string.Empty;
            }

            return;
        }

        if (!currentItem.HasValue || currentItem.Value != eventArgs.Item.Id)
        {
            _logger.LogDebug(
                "Virtual TV ignored stale VOD stop for item {ItemId} in session {SessionId}, generation {Generation}.",
                eventArgs.Item.Id,
                sessionId,
                context.Generation);
            return;
        }

        if (!IsAtPhysicalEnd(eventArgs))
        {
            EndSession(sessionId, context.Generation, "manual VOD stop");
            return;
        }

        lock (context.Gate)
        {
            context.LastCompletedItemId = eventArgs.Item.Id;
        }

        // Physical EOF never opens another VOD directly. Always return to the neutral Live TV
        // loading source first, then let that bootstrap start event trigger a fresh resolution.
        await ReturnToBootstrapAsync(context, "physical VOD EOF").ConfigureAwait(false);
    }

    private Task HandleBootstrapStartAsync(
        PlaybackStartEventArgs eventArgs,
        LiveTvChannel liveChannel,
        string configurationChannelId)
    {
        var sessionId = eventArgs.Session!.Id;

        if (_sessions.TryGetValue(sessionId, out var existing)
            && existing.LiveChannelItemId == liveChannel.Id
            && existing.Phase == PlaybackPhase.AwaitingBootstrap)
        {
            lock (existing.Gate)
            {
                existing.Phase = PlaybackPhase.BootstrapWaitingForProgress;
                existing.BootstrapPlaySessionId = eventArgs.PlaySessionId ?? string.Empty;
                existing.ReplacingPlaySessionId = string.Empty;
                existing.CurrentVodItemId = null;
                existing.PendingVodItemId = null;
            }

            _logger.LogInformation(
                "Virtual TV bootstrap restarted for session {SessionId}, generation {Generation}, channel {ChannelName}; waiting for real playback progress before starting the 1.5-second buffer.",
                sessionId,
                existing.Generation,
                existing.ChannelName);

            return Task.CompletedTask;
        }

        if (_sessions.TryGetValue(sessionId, out existing))
        {
            var samePlaySession = !string.IsNullOrWhiteSpace(eventArgs.PlaySessionId)
                && string.Equals(existing.BootstrapPlaySessionId, eventArgs.PlaySessionId, StringComparison.Ordinal);

            var fallbackDuplicate = string.IsNullOrWhiteSpace(eventArgs.PlaySessionId)
                && existing.LiveChannelItemId == liveChannel.Id
                && existing.Phase is PlaybackPhase.BootstrapWaitingForProgress or PlaybackPhase.BootstrapBuffering
                && DateTime.UtcNow - existing.CreatedUtc < DuplicateTuneFallbackWindow;

            if (samePlaySession || fallbackDuplicate)
            {
                _logger.LogDebug(
                    "Virtual TV ignored duplicate bootstrap start for session {SessionId}, generation {Generation}.",
                    sessionId,
                    existing.Generation);
                return Task.CompletedTask;
            }
        }

        var channelConfiguration = GetChannelConfiguration(configurationChannelId);
        if (channelConfiguration is null)
        {
            return Task.CompletedTask;
        }

        var playbackUserId = ResolvePlaybackUserId(eventArgs);
        if (playbackUserId != Guid.Empty && !_visibility.IsVisibleToUser(channelConfiguration, playbackUserId))
        {
            return Task.CompletedTask;
        }

        var contentMode = string.Equals(channelConfiguration.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
            ? (string.Equals(channelConfiguration.ContentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase)
                ? VirtualTvModePolicy.RandomUnwatched
                : VirtualTvModePolicy.Random)
            : VirtualTvModePolicy.NormalizeContentMode(channelConfiguration.ContentMode);

        var tracksState = VirtualTvModePolicy.TracksJellyfinState(contentMode);

        if (_sessions.TryRemove(sessionId, out var previous))
        {
            previous.Cancel();

            if (!previous.TracksJellyfinState && tracksState)
            {
                _stateProtection.CompleteProtection(sessionId);
            }
        }

        var context = new SessionContext(
            sessionId,
            configurationChannelId,
            playbackUserId,
            liveChannel.Name,
            channelConfiguration.ChannelType,
            contentMode,
            liveChannel.Id,
            eventArgs.PlaySessionId ?? string.Empty,
            Interlocked.Increment(ref _nextGeneration));

        _sessions[sessionId] = context;

        _logger.LogInformation(
            "Virtual TV tune {Generation}: session {SessionId}, channel {ChannelName}, phase BootstrapWaitingForProgress, mode {Mode}. No VOD command will be sent until the loading source reports playback progress.",
            context.Generation,
            sessionId,
            context.ChannelName,
            context.ContentMode);

        return Task.CompletedTask;
    }

    private void HandleBootstrapStop(PlaybackStopEventArgs eventArgs, SessionContext context)
    {
        string replacingPlaySession;
        PlaybackPhase phase;

        lock (context.Gate)
        {
            replacingPlaySession = context.ReplacingPlaySessionId;
            phase = context.Phase;
        }

        if (!string.IsNullOrWhiteSpace(replacingPlaySession)
            && string.Equals(replacingPlaySession, eventArgs.PlaySessionId, StringComparison.Ordinal))
        {
            lock (context.Gate)
            {
                context.ReplacingPlaySessionId = string.Empty;
            }

            return;
        }

        // Bootstrap stops after a VOD PlayNow or while returning from VOD are expected.
        if (phase is PlaybackPhase.AwaitingVod or PlaybackPhase.Vod or PlaybackPhase.AwaitingBootstrap)
        {
            return;
        }

        // While waiting for/inside the confirmed loading buffer, a non-complete stop means the
        // user actually left the channel. Cancel the detached buffer so playback is not resurrected.
        // A completed loading clip is harmless; if it ever happens, keep the pending buffer alive.
        if (phase is PlaybackPhase.BootstrapWaitingForProgress or PlaybackPhase.BootstrapBuffering)
        {
            if (eventArgs.PlayedToCompletion)
            {
                return;
            }

            EndSession(context.SessionId, context.Generation, "loading playback stopped before VOD handoff");
            return;
        }

        EndSession(context.SessionId, context.Generation, "manual bootstrap stop");
    }

    private void HandleVodStart(PlaybackStartEventArgs eventArgs)
    {
        var sessionId = eventArgs.Session!.Id;
        if (!_sessions.TryGetValue(sessionId, out var context))
        {
            return;
        }

        var startedItemId = eventArgs.Item!.Id;
        Guid? pendingVod;
        Guid? currentVod;
        PlaybackPhase phase;

        lock (context.Gate)
        {
            pendingVod = context.PendingVodItemId;
            currentVod = context.CurrentVodItemId;
            phase = context.Phase;
        }

        if (phase == PlaybackPhase.AwaitingVod
            && pendingVod.HasValue
            && pendingVod.Value == startedItemId)
        {
            lock (context.Gate)
            {
                context.Phase = PlaybackPhase.Vod;
                context.CurrentVodItemId = startedItemId;
                context.CurrentVodPlaySessionId = eventArgs.PlaySessionId ?? string.Empty;
                context.PendingVodItemId = null;
                context.ReplacingPlaySessionId = string.Empty;
            }

            _logger.LogInformation(
                "Virtual TV VOD accepted: session {SessionId}, generation {Generation}, item {ItemId}.",
                sessionId,
                context.Generation,
                startedItemId);
            return;
        }

        if (phase == PlaybackPhase.Vod && currentVod.HasValue && currentVod.Value == startedItemId)
        {
            // Normal VOD player restart after subtitle/audio changes or a user seek.
            lock (context.Gate)
            {
                context.CurrentVodPlaySessionId = eventArgs.PlaySessionId ?? string.Empty;
            }

            return;
        }

        if (pendingVod.HasValue)
        {
            _logger.LogDebug(
                "Virtual TV ignored stale VOD start {ItemId}; session {SessionId} generation {Generation} is waiting for {PendingItemId}.",
                startedItemId,
                sessionId,
                context.Generation,
                pendingVod.Value);
            return;
        }

        EndSession(sessionId, context.Generation, "unrelated playback started");
    }

    private async Task CompleteBootstrapBufferAsync(SessionContext context)
    {
        try
        {
            // The timer starts only after the client has reported real progress for the loading
            // source. This guarantees a visible/stable loading phase instead of counting while
            // webOS/Android TV is still constructing the Live TV player.
            await Task.Delay(BootstrapBuffer, context.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Virtual TV bootstrap buffer failed for session {SessionId}, generation {Generation}.",
                context.SessionId,
                context.Generation);
            return;
        }

        if (!IsCurrent(context))
        {
            return;
        }

        lock (context.Gate)
        {
            if (context.Phase != PlaybackPhase.BootstrapBuffering)
            {
                return;
            }
        }

        // Resolve only after five confirmed seconds of loading playback. The wall clock at this
        // point is authoritative, so channel changes and EOF transitions cannot carry an old
        // schedule decision into the VOD handoff.
        var nowUtc = DateTime.UtcNow;
        var liveEntry = FindActiveEntry(LoadSchedule(context.ChannelId), nowUtc);
        if (liveEntry is null)
        {
            EndSession(context.SessionId, context.Generation, "no active schedule entry after confirmed bootstrap buffer");
            return;
        }

        await PlayScheduledEntryAsync(
            context,
            liveEntry,
            nowUtc,
            "1.5 seconds after confirmed loading playback").ConfigureAwait(false);
    }

    private async Task ReturnToBootstrapAsync(SessionContext context, string reason)
    {
        string currentVodPlaySession;

        lock (context.Gate)
        {
            if (context.Phase != PlaybackPhase.Vod)
            {
                return;
            }

            currentVodPlaySession = context.CurrentVodPlaySessionId;
            context.Phase = PlaybackPhase.AwaitingBootstrap;
            context.ReplacingPlaySessionId = currentVodPlaySession;
            context.PendingVodItemId = null;
            context.PendingSeekTicks = 0;
            context.SeekScheduled = false;
        }

        _logger.LogInformation(
            "Virtual TV VOD finished: session {SessionId}, generation {Generation}, channel {ChannelName}. Waiting {BufferMs}ms for the normal Jellyfin VOD player to close before reopening the neutral Live TV bootstrap.",
            context.SessionId,
            context.Generation,
            context.ChannelName,
            VodTeardownBuffer.TotalMilliseconds);

        try
        {
            await Task.Delay(VodTeardownBuffer, context.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!IsCurrent(context))
        {
            return;
        }

        await SendBootstrapPlayNowAsync(
            context,
            reason + "; reopen neutral Live TV bootstrap after VOD teardown").ConfigureAwait(false);
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

        PrepareVodHandoff(context, entry, resolution.ItemId, resolution.StartPositionTicks);

        await SendIsolatedVodPlayNowAsync(
            context,
            resolution.ItemId,
            resolution.StartPositionTicks,
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

        PrepareVodHandoff(context, entry, resolution.ItemId, resolution.StartPositionTicks);

        await SendIsolatedVodPlayNowAsync(
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
        PrepareVodHandoff(context, entry, sourceItemId, targetTicks);

        await SendIsolatedVodPlayNowAsync(
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

        PrepareVodHandoff(context, entry, fallback.Id, 0);

        await SendIsolatedVodPlayNowAsync(
            context,
            fallback.Id,
            0,
            reason + "; local fallback").ConfigureAwait(false);
    }

    private void PrepareVodHandoff(
        SessionContext context,
        VirtualTvScheduleEntry entry,
        Guid targetItemId,
        long requestedStartTicks)
    {
        lock (context.Gate)
        {
            if (context.Phase != PlaybackPhase.BootstrapBuffering)
            {
                return;
            }

            context.Phase = PlaybackPhase.AwaitingVod;
            context.ReplacingPlaySessionId = context.BootstrapPlaySessionId;
            context.PendingVodItemId = targetItemId;
            context.PendingSeekTicks = Math.Max(0, requestedStartTicks);
            context.SeekScheduled = false;
            context.CurrentEntryId = entry.Id;
        }
    }

    /// <summary>
    /// Sends one concrete VOD item directly to the controllers of the exact Jellyfin session.
    ///
    /// This deliberately bypasses ISessionManager.SendPlayCommand for episodes. Jellyfin's server
    /// expands a single Episode PlayNow into the remainder of the series whenever the user's
    /// "Play next episode automatically" setting is enabled. That server-side expansion creates
    /// Next Up / SxxExx queue items that are wrong for Virtual TV.
    ///
    /// Sending the already-resolved concrete item as a raw SessionMessageType.Play message keeps
    /// the client playlist to exactly one item. At EOF the normal VOD player therefore has no next
    /// item to autoplay; it closes, and Virtual TV explicitly reopens the neutral Live TV bootstrap.
    /// </summary>
    private async Task SendIsolatedVodPlayNowAsync(
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

            var item = _libraryManager.GetItemById(itemId);
            if (item is null || item.IsFolder)
            {
                EndSession(context.SessionId, context.Generation, "isolated VOD target is not a concrete playable item");
                return;
            }

            if (context.UserId != Guid.Empty)
            {
                var user = _userManager.GetUserById(context.UserId);
                if (user is null || item.GetPlayAccess(user) != PlayAccess.Full)
                {
                    EndSession(context.SessionId, context.Generation, "active user does not have full play access to isolated VOD target");
                    return;
                }
            }

            var targetSession = _sessionManager.Sessions.FirstOrDefault(
                session => string.Equals(session.Id, context.SessionId, StringComparison.Ordinal));

            if (targetSession is null || targetSession.SessionControllers.Count == 0)
            {
                EndSession(context.SessionId, context.Generation, "target session has no active controller for isolated VOD PlayNow");
                return;
            }

            // Always establish the VOD player at 00:00. Direct non-zero StartPositionTicks can
            // leave webOS with working audio/subtitles but a frozen video frame. If a scheduler
            // offset or Resume is required, a single guarded seek is sent only after real VOD
            // PlaybackProgress confirms the decoder is alive.
            var requestedStartTicks = Math.Max(0, startPositionTicks);
            var isolatedStartTicks = 1L;

            var request = new PlayRequest
            {
                ItemIds = new[] { itemId },
                StartPositionTicks = isolatedStartTicks,
                StartIndex = 0,
                PlayCommand = PlayCommand.PlayNow,
                ControllingUserId = context.UserId
            };

            _logger.LogInformation(
                "Virtual TV isolated VOD PlayNow {Generation}: session {SessionId}, item {ItemId}, queue length 1, requested target {StartSeconds:F1}s; player opens at 00:00, reason {Reason}.",
                context.Generation,
                context.SessionId,
                itemId,
                TimeSpan.FromTicks(requestedStartTicks).TotalSeconds,
                reason);

            var messageId = Guid.NewGuid();
            foreach (var controller in targetSession.SessionControllers)
            {
                await controller.SendMessage(
                    SessionMessageType.Play,
                    messageId,
                    request,
                    context.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
            // A newer tune superseded this request.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Virtual TV isolated VOD PlayNow failed for session {SessionId}, generation {Generation}.",
                context.SessionId,
                context.Generation);

            EndSession(context.SessionId, context.Generation, "isolated VOD PlayNow failed");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reopens the Virtual TV channel itself. Unlike episode playback this may use Jellyfin's
    /// normal SendPlayCommand path because a LiveTvChannel cannot trigger episode auto-expansion.
    /// The channel always resolves to the universal embedded Loading Virtual TV source.
    /// </summary>
    private async Task CompleteVodSeekAsync(
        SessionContext context,
        Guid itemId,
        long seekTicks)
    {
        try
        {
            await Task.Delay(VodSeekSettleBuffer, context.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!IsCurrent(context))
        {
            return;
        }

        lock (context.Gate)
        {
            if (context.Phase != PlaybackPhase.Vod
                || context.CurrentVodItemId != itemId
                || context.PendingSeekTicks != seekTicks
                || !context.SeekScheduled)
            {
                return;
            }
        }

        try
        {
            await _sessionManager.SendPlaystateCommand(
                context.SessionId,
                context.SessionId,
                new PlaystateRequest
                {
                    Command = PlaystateCommand.Seek,
                    SeekPositionTicks = seekTicks
                },
                context.Token).ConfigureAwait(false);

            lock (context.Gate)
            {
                if (context.CurrentVodItemId == itemId)
                {
                    context.PendingSeekTicks = 0;
                }
            }

            _logger.LogInformation(
                "Virtual TV completed safe VOD seek for session {SessionId}, generation {Generation}, item {ItemId} to {SeekSeconds:F1}s.",
                context.SessionId,
                context.Generation,
                itemId,
                TimeSpan.FromTicks(seekTicks).TotalSeconds);
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Virtual TV safe VOD seek failed for session {SessionId}, generation {Generation}, item {ItemId}. Playback remains at 00:00.",
                context.SessionId,
                context.Generation,
                itemId);
        }
    }

    private async Task SendBootstrapPlayNowAsync(
        SessionContext context,
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
                ItemIds = new[] { context.LiveChannelItemId },
                StartPositionTicks = 0,
                StartIndex = 0,
                PlayCommand = PlayCommand.PlayNow
            };

            _logger.LogInformation(
                "Virtual TV bootstrap PlayNow {Generation}: session {SessionId}, channel {ChannelName}, reason {Reason}.",
                context.Generation,
                context.SessionId,
                context.ChannelName,
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
                "Virtual TV bootstrap PlayNow failed for session {SessionId}, generation {Generation}.",
                context.SessionId,
                context.Generation);

            EndSession(context.SessionId, context.Generation, "bootstrap PlayNow failed");
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

    private enum PlaybackPhase
    {
        BootstrapWaitingForProgress,
        BootstrapBuffering,
        AwaitingVod,
        Vod,
        AwaitingBootstrap
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
            Guid liveChannelItemId,
            string bootstrapPlaySessionId,
            long generation)
        {
            SessionId = sessionId;
            ChannelId = channelId;
            UserId = userId;
            ChannelName = channelName;
            ChannelType = channelType;
            ContentMode = contentMode;
            LiveChannelItemId = liveChannelItemId;
            BootstrapPlaySessionId = bootstrapPlaySessionId;
            Generation = generation;
            Phase = PlaybackPhase.BootstrapWaitingForProgress;
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

        public Guid LiveChannelItemId { get; }

        public long Generation { get; }

        public bool IsDynamicUnwatched { get; }

        public bool IsDynamicMovie { get; }

        public bool TracksJellyfinState { get; }

        public PlaybackPhase Phase { get; set; }

        public string BootstrapPlaySessionId { get; set; }

        public DateTime? BootstrapFirstProgressUtc { get; set; }

        public long BootstrapFirstProgressTicks { get; set; }

        public string CurrentEntryId { get; set; } = string.Empty;

        public Guid? PendingVodItemId { get; set; }

        public Guid? CurrentVodItemId { get; set; }

        public long PendingSeekTicks { get; set; }

        public bool SeekScheduled { get; set; }

        public Guid? LastCompletedItemId { get; set; }

        public string CurrentVodPlaySessionId { get; set; } = string.Empty;

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
