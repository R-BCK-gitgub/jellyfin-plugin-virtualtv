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
/// Keeps traditional Virtual TV playback inside the native Live TV transport on LG webOS.
///
/// Jellyfin Web can tolerate a VOD PlayNow that starts mid-file, but LG webOS can freeze the
/// video decoder while audio/subtitles continue. The Live TV service therefore rebases the
/// source media so stream position 00:00 represents the wall-clock Live point. This manager
/// prevents the normal VOD handoff on LG and retunes the channel at natural source EOF.
/// </summary>
public sealed class LgWebOsLivePlaybackManager
{
    private static readonly TimeSpan NaturalEndTolerance = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetuneDelay = TimeSpan.FromMilliseconds(250);

    private readonly ISessionManager _sessionManager;
    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly ILogger<LgWebOsLivePlaybackManager> _logger;
    private readonly ConcurrentDictionary<string, LgSessionContext> _sessions = new(StringComparer.Ordinal);

    public LgWebOsLivePlaybackManager(
        ISessionManager sessionManager,
        VirtualTvScheduleStore scheduleStore,
        ILogger<LgWebOsLivePlaybackManager> logger)
    {
        _sessionManager = sessionManager;
        _scheduleStore = scheduleStore;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when this playback start belongs to an LG webOS traditional Virtual TV
    /// channel and has therefore been claimed by the native rebased transport.
    /// </summary>
    public Task<bool> TryHandlePlaybackStartAsync(PlaybackStartEventArgs eventArgs)
    {
        if (eventArgs.Session is null
            || string.IsNullOrWhiteSpace(eventArgs.Session.Id)
            || eventArgs.Item is not LiveTvChannel channel
            || !string.Equals(channel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase)
            || !VirtualTvLiveTvService.TryGetConfigurationChannelId(channel.ExternalId, out var channelId)
            || !IsLgWebOsSession(eventArgs.Session))
        {
            return Task.FromResult(false);
        }

        var configuration = Plugin.Instance?.Configuration.Channels.FirstOrDefault(
            candidate => string.Equals(candidate.Id, channelId, StringComparison.OrdinalIgnoreCase));
        if (configuration is null)
        {
            return Task.FromResult(false);
        }

        var contentMode = string.Equals(configuration.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
            ? (string.Equals(configuration.ContentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase)
                ? VirtualTvModePolicy.RandomUnwatched
                : VirtualTvModePolicy.Random)
            : VirtualTvModePolicy.NormalizeContentMode(configuration.ContentMode);

        // Watched-dependent series already start at 00:00 and must use the normal Jellyfin
        // item so watched/resume state can be written intentionally. Random Unwatched movies
        // also remain on the existing VOD path because their personal Resume is meaningful.
        if (VirtualTvModePolicy.IsDynamicUnwatched(contentMode))
        {
            return Task.FromResult(false);
        }

        var nowUtc = DateTime.UtcNow;
        var entry = FindActiveEntry(channelId, nowUtc);
        if (entry is null)
        {
            _logger.LogWarning(
                "Virtual TV LG webOS transport could not claim session {SessionId}: no active programme for {ChannelName}.",
                eventArgs.Session.Id,
                channel.Name);
            return Task.FromResult(false);
        }

        var remaining = entry.GetEndUtc() - nowUtc;
        var expectedRemainingTicks = Math.Max(TimeSpan.TicksPerSecond, remaining.Ticks);

        _sessions[eventArgs.Session.Id] = new LgSessionContext(
            channelId,
            channel.Id,
            channel.Name,
            entry.Id,
            entry.SourceItemId,
            eventArgs.PlaySessionId ?? string.Empty,
            expectedRemainingTicks);

        _logger.LogInformation(
            "Virtual TV LG webOS native transport active: session {SessionId}, device {DeviceName}, client {Client}, channel {ChannelName}, programme {ProgramName}, rebased stream starts at player 00:00 with {RemainingSeconds:F1}s remaining.",
            eventArgs.Session.Id,
            eventArgs.Session.DeviceName,
            eventArgs.Session.Client,
            channel.Name,
            string.IsNullOrWhiteSpace(entry.SeriesName) ? entry.Name : entry.SeriesName + " - " + entry.Name,
            TimeSpan.FromTicks(expectedRemainingTicks).TotalSeconds);

        return Task.FromResult(true);
    }

    /// <summary>
    /// Returns true when the stop event belongs to a claimed LG webOS native transport.
    /// Natural EOF retunes the same Live TV channel so the next wall-clock programme is again
    /// rebased to a clean 00:00 stream. Manual stops simply end the managed LG context.
    /// </summary>
    public async Task<bool> TryHandlePlaybackStopAsync(PlaybackStopEventArgs eventArgs)
    {
        if (eventArgs.Session is null
            || string.IsNullOrWhiteSpace(eventArgs.Session.Id)
            || !_sessions.TryGetValue(eventArgs.Session.Id, out var context))
        {
            return false;
        }

        if (eventArgs.Item is not LiveTvChannel)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(context.PlaySessionId)
            && !string.IsNullOrWhiteSpace(eventArgs.PlaySessionId)
            && !string.Equals(context.PlaySessionId, eventArgs.PlaySessionId, StringComparison.Ordinal))
        {
            // Track changes can replace the opened Live TV play session. Ignore a late stop
            // belonging to the superseded stream if a newer channel start already refreshed
            // this context.
            return true;
        }

        var positionTicks = Math.Max(0, eventArgs.PlaybackPositionTicks ?? 0);
        var naturalEnd = eventArgs.PlayedToCompletion
            || positionTicks >= Math.Max(0, context.ExpectedRemainingTicks - NaturalEndTolerance.Ticks);

        if (!naturalEnd)
        {
            _sessions.TryRemove(eventArgs.Session.Id, out _);
            _logger.LogInformation(
                "Virtual TV LG webOS native transport ended manually: session {SessionId}, channel {ChannelName}, position {PositionSeconds:F1}s.",
                eventArgs.Session.Id,
                context.ChannelName,
                TimeSpan.FromTicks(positionTicks).TotalSeconds);
            return true;
        }

        var nowUtc = DateTime.UtcNow;
        var liveEntry = FindActiveEntry(context.ChannelId, nowUtc);
        if (liveEntry is null)
        {
            _sessions.TryRemove(eventArgs.Session.Id, out _);
            _logger.LogInformation(
                "Virtual TV LG webOS transport reached EOF while channel {ChannelName} is Off Air or has no active programme.",
                context.ChannelName);
            return true;
        }

        try
        {
            await Task.Delay(RetuneDelay).ConfigureAwait(false);

            var command = new PlayRequest
            {
                ItemIds = [context.ChannelItemId],
                StartPositionTicks = 0,
                StartIndex = 0,
                PlayCommand = PlayCommand.PlayNow
            };

            _logger.LogInformation(
                "Virtual TV LG webOS natural EOF: retuning channel {ChannelName} on session {SessionId}; next programme {ProgramName} will open as a newly rebased 00:00 Live TV stream.",
                context.ChannelName,
                eventArgs.Session.Id,
                string.IsNullOrWhiteSpace(liveEntry.SeriesName) ? liveEntry.Name : liveEntry.SeriesName + " - " + liveEntry.Name);

            await _sessionManager.SendPlayCommand(
                eventArgs.Session.Id,
                eventArgs.Session.Id,
                command,
                CancellationToken.None).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(eventArgs.Session.Id, out _);
            _logger.LogError(
                ex,
                "Virtual TV LG webOS retune failed for session {SessionId}, channel {ChannelName}.",
                eventArgs.Session.Id,
                context.ChannelName);
            return true;
        }
    }

    internal static bool IsLgWebOsSession(SessionInfo session)
    {
        static bool Contains(string? value, string term)
            => !string.IsNullOrWhiteSpace(value)
                && value.Contains(term, StringComparison.OrdinalIgnoreCase);

        // Jellyfin Web identifies the official LG webOS client as DeviceName "LG Smart TV".
        // Keep additional webOS/LG probes for older app/browser wrappers.
        return Contains(session.DeviceName, "LG Smart TV")
            || Contains(session.DeviceName, "webOS")
            || Contains(session.DeviceType, "webOS")
            || Contains(session.Client, "webOS");
    }

    private VirtualTvScheduleEntry? FindActiveEntry(string channelId, DateTime nowUtc)
        => _scheduleStore.Load(channelId)
            .OrderBy(entry => entry.GetStartUtc())
            .FirstOrDefault(entry =>
                !entry.IsOffAir
                && !entry.IsContentUnavailable
                && !entry.IsScheduleUnavailable
                && entry.GetStartUtc() <= nowUtc
                && entry.GetEndUtc() > nowUtc);

    private sealed record LgSessionContext(
        string ChannelId,
        Guid ChannelItemId,
        string ChannelName,
        string EntryId,
        string SourceItemId,
        string PlaySessionId,
        long ExpectedRemainingTicks);
}
