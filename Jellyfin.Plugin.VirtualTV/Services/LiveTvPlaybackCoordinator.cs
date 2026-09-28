using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Aligns a newly opened Virtual TV channel with the wall-clock position of the
/// materialized programme that is currently on air.
/// </summary>
public sealed class LiveTvPlaybackCoordinator
{
    // A second seek is intentional. Some TV clients finish rebuilding the video element
    // after the first PlaybackStart notification. Reissuing the same wall-clock alignment
    // once the player is fully attached prevents an apparently-correct progress bar from
    // playing the underlying source file from 00:00.
    private static readonly TimeSpan FirstSeekDelay = TimeSpan.FromMilliseconds(850);
    private static readonly TimeSpan ConfirmationSeekDelay = TimeSpan.FromMilliseconds(1400);

    private readonly ISessionManager _sessionManager;
    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly ILogger<LiveTvPlaybackCoordinator> _logger;
    private readonly ConcurrentDictionary<string, long> _alignmentGeneration = new(StringComparer.Ordinal);

    public LiveTvPlaybackCoordinator(
        ISessionManager sessionManager,
        VirtualTvScheduleStore scheduleStore,
        ILogger<LiveTvPlaybackCoordinator> logger)
    {
        _sessionManager = sessionManager;
        _scheduleStore = scheduleStore;
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

        var activeEntry = FindActiveEntry(configurationChannelId, DateTime.UtcNow);
        if (activeEntry is null || activeEntry.IsOffAir)
        {
            _logger.LogWarning(
                "Virtual TV could not align session {SessionId}: no active materialized programme was found for channel {ChannelName}.",
                eventArgs.Session.Id,
                channel.Name);
            return;
        }

        var sessionId = eventArgs.Session.Id;
        var generation = _alignmentGeneration.AddOrUpdate(sessionId, 1, static (_, current) => current + 1);

        _logger.LogInformation(
            "Virtual TV starting wall-clock alignment for session {SessionId}: channel {ChannelName}, programme {ProgramName}, programme start {ProgramStartUtc}.",
            sessionId,
            channel.Name,
            activeEntry.Name,
            activeEntry.GetStartUtc());

        await Task.Delay(FirstSeekDelay).ConfigureAwait(false);
        if (!IsCurrentGeneration(sessionId, generation))
        {
            return;
        }

        await SendWallClockSeekAsync(sessionId, channel.Name, activeEntry, "initial").ConfigureAwait(false);

        await Task.Delay(ConfirmationSeekDelay).ConfigureAwait(false);
        if (!IsCurrentGeneration(sessionId, generation))
        {
            return;
        }

        await SendWallClockSeekAsync(sessionId, channel.Name, activeEntry, "confirmation").ConfigureAwait(false);
    }

    private VirtualTvScheduleEntry? FindActiveEntry(string channelId, DateTime nowUtc)
        => _scheduleStore.Load(channelId)
            .FirstOrDefault(entry =>
                !entry.IsOffAir
                && entry.GetStartUtc() <= nowUtc
                && entry.GetEndUtc() > nowUtc);

    private bool IsCurrentGeneration(string sessionId, long generation)
        => _alignmentGeneration.TryGetValue(sessionId, out var current) && current == generation;

    private async Task SendWallClockSeekAsync(
        string sessionId,
        string channelName,
        VirtualTvScheduleEntry entry,
        string attempt)
    {
        var nowUtc = DateTime.UtcNow;
        var targetTicks = Math.Max(0, (nowUtc - entry.GetStartUtc()).Ticks);

        // The source selected by VirtualTvLiveTvService is the full original file, so this
        // seek is intentionally programme-relative. Do not trust PlaybackStart.PositionTicks:
        // Jellyfin can report the EPG/live-channel position there even while the media source
        // itself is still rendering from the beginning of the episode.
        _logger.LogInformation(
            "Virtual TV {Attempt} wall-clock seek for session {SessionId}: channel {ChannelName}, programme {ProgramName}, target {TargetSeconds:F1}s.",
            attempt,
            sessionId,
            channelName,
            entry.Name,
            TimeSpan.FromTicks(targetTicks).TotalSeconds);

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
}
