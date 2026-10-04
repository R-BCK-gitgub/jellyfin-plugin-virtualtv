using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Deferred Standard TV Play-from-Beginning handoff for Android clients.
///
/// The native Record endpoint must finish before Jellyfin Android changes player state. Android TV
/// also refreshes Live TV state immediately after createTimer succeeds. A single fire-and-forget
/// Play message is therefore not reliable: resolve the physical device, send Play, wait for Jellyfin
/// to report the concrete library item as NowPlaying, and re-resolve/retry if the client refreshed
/// its session/controller during the handoff.
/// </summary>
public sealed class VirtualTvPlaybackCommandDispatcher : BackgroundService
{
    private static readonly TimeSpan AndroidRecordResponseSettleDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan AndroidControlReadyTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AndroidControlReadyPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan AndroidPlaybackAckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AndroidPlaybackAckPollInterval = TimeSpan.FromMilliseconds(200);
    private const int AndroidPlayMaxAttempts = 3;

    private readonly Channel<StandardTvPlayFromBeginningRequest> _queue =
        Channel.CreateUnbounded<StandardTvPlayFromBeginningRequest>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<VirtualTvPlaybackCommandDispatcher> _logger;

    public VirtualTvPlaybackCommandDispatcher(
        ISessionManager sessionManager,
        ILogger<VirtualTvPlaybackCommandDispatcher> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public void Enqueue(StandardTvPlayFromBeginningRequest request)
    {
        if (!_queue.Writer.TryWrite(request))
        {
            throw new InvalidOperationException("Virtual TV Android playback dispatcher is not accepting commands.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await DispatchAndroidPlayFromBeginningAsync(request, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Virtual TV Android Play from Beginning failed for channel {ChannelName}, item {ItemId}, client {Client}, device {DeviceName} / {DeviceId}.",
                        request.ChannelName,
                        request.ItemId,
                        request.ClientName,
                        request.DeviceName,
                        request.DeviceId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal Jellyfin shutdown.
        }
    }

    private async Task DispatchAndroidPlayFromBeginningAsync(
        StandardTvPlayFromBeginningRequest request,
        CancellationToken cancellationToken)
    {
        var isAndroidMobile = VirtualTvClientPolicy.IsAndroidMobile(request.ClientName);
        var isAndroidTv = VirtualTvClientPolicy.IsAndroidTv(request.ClientName);
        if (!isAndroidMobile && !isAndroidTv)
        {
            _logger.LogWarning(
                "Virtual TV ignored deferred Play from Beginning for non-Android client {Client}.",
                request.ClientName);
            return;
        }

        // The queue is intentionally asynchronous so CreateTimer/CreateSeriesTimer can return first.
        // Android TV then runs updateTvProgramInfo/TvManager.forceReload; Android Web can close its
        // Record spinner. Start playback only after that response/refresh window has had time to begin.
        await Task.Delay(AndroidRecordResponseSettleDelay, cancellationToken).ConfigureAwait(false);

        if (IsPlaybackAcknowledged(request))
        {
            _logger.LogInformation(
                "Virtual TV Android Play from Beginning item {ItemId} was already active after the Record response settled.",
                request.ItemId);
            return;
        }

        var playRequest = new PlayRequest
        {
            ItemIds = new[] { request.ItemId },
            StartPositionTicks = 0L,
            StartIndex = 0,
            PlayCommand = PlayCommand.PlayNow,
            ControllingUserId = request.UserId
        };

        for (var attempt = 1; attempt <= AndroidPlayMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-resolve on every attempt. Android TV can refresh its session after createTimer and
            // Android mobile can move control between WebView/native-player companion sessions.
            var targetSession = await WaitForAndroidControllableSessionAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (targetSession is null)
            {
                _logger.LogError(
                    "Virtual TV could not find the active Android media-control session for channel {ChannelName}, item {ItemId}, attempt {Attempt}/{MaxAttempts}. Android sessions: {Sessions}",
                    request.ChannelName,
                    request.ItemId,
                    attempt,
                    AndroidPlayMaxAttempts,
                    DescribeAndroidSessions(request));
                return;
            }

            var controllers = targetSession.SessionControllers
                .Where(controller => controller.IsSessionActive && controller.SupportsMediaControl)
                .ToArray();
            if (controllers.Length == 0)
            {
                _logger.LogWarning(
                    "Virtual TV Android Play from Beginning attempt {Attempt}/{MaxAttempts}: session {SessionId} has no active media-control controller; re-resolving.",
                    attempt,
                    AndroidPlayMaxAttempts,
                    targetSession.Id);
                continue;
            }

            _logger.LogInformation(
                "Virtual TV Standard TV Android Play from Beginning attempt {Attempt}/{MaxAttempts}: {ClientAction} on channel {ChannelName}, item {ItemId}, source session {SourceSessionId}, control session {ControlSessionId}, client {Client}, device {DeviceName} / {DeviceId}.",
                attempt,
                AndroidPlayMaxAttempts,
                request.ClientAction,
                request.ChannelName,
                request.ItemId,
                request.SourceSessionId,
                targetSession.Id,
                request.ClientName,
                request.DeviceName,
                request.DeviceId);

            await SendRawPlayMessageAsync(controllers, playRequest, cancellationToken).ConfigureAwait(false);

            if (await WaitForPlaybackAcknowledgementAsync(request, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Virtual TV Standard TV Android Play from Beginning confirmed: item {ItemId} is now playing from 00:00 after attempt {Attempt}/{MaxAttempts}. No DVR timer was stored.",
                    request.ItemId,
                    attempt,
                    AndroidPlayMaxAttempts);
                return;
            }

            if (attempt < AndroidPlayMaxAttempts)
            {
                _logger.LogWarning(
                    "Virtual TV Android Play from Beginning item {ItemId} was not acknowledged within {TimeoutSeconds:F1}s after attempt {Attempt}/{MaxAttempts}; re-resolving the Android session/controller and retrying.",
                    request.ItemId,
                    AndroidPlaybackAckTimeout.TotalSeconds,
                    attempt,
                    AndroidPlayMaxAttempts);
            }
        }

        _logger.LogError(
            "Virtual TV Android Play from Beginning failed after {MaxAttempts} attempts for channel {ChannelName}, item {ItemId}, client {Client}, device {DeviceName} / {DeviceId}. Android sessions: {Sessions}",
            AndroidPlayMaxAttempts,
            request.ChannelName,
            request.ItemId,
            request.ClientName,
            request.DeviceName,
            request.DeviceId,
            DescribeAndroidSessions(request));
    }

    private async Task<SessionInfo?> WaitForAndroidControllableSessionAsync(
        StandardTvPlayFromBeginningRequest request,
        CancellationToken cancellationToken)
    {
        var deadlineUtc = DateTime.UtcNow + AndroidControlReadyTimeout;

        while (!cancellationToken.IsCancellationRequested)
        {
            var session = FindAndroidControllableSession(request);
            if (session is not null)
            {
                return session;
            }

            if (DateTime.UtcNow >= deadlineUtc)
            {
                return null;
            }

            await Task.Delay(AndroidControlReadyPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private SessionInfo? FindAndroidControllableSession(StandardTvPlayFromBeginningRequest request)
    {
        var candidates = GetAndroidClientSessions(request)
            .Where(session =>
                session.IsActive
                && session.SessionControllers.Any(
                    controller => controller.IsSessionActive && controller.SupportsMediaControl))
            .OrderByDescending(session => session.LastPlaybackCheckIn)
            .ThenByDescending(session => session.LastActivityDate)
            .ToList();

        // Match the proven Personalized TV strategy: the original session is safest when it survived
        // the Record refresh and is still controllable.
        if (!string.IsNullOrWhiteSpace(request.SourceSessionId))
        {
            var sameSession = candidates.FirstOrDefault(session =>
                string.Equals(session.Id, request.SourceSessionId, StringComparison.Ordinal));
            if (sameSession is not null)
            {
                return sameSession;
            }
        }

        var exactDevice = candidates.FirstOrDefault(session =>
            string.Equals(session.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (exactDevice is not null)
        {
            return exactDevice;
        }

        if (VirtualTvClientPolicy.IsAndroidMobile(request.ClientName))
        {
            var companionMatches = candidates
                .Where(session => AreAndroidCompanionDeviceIds(request.DeviceId, session.DeviceId, request.UserId))
                .ToList();
            if (companionMatches.Count == 1)
            {
                return companionMatches[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(request.DeviceName))
        {
            var nameMatches = candidates
                .Where(session => string.Equals(
                    session.DeviceName,
                    request.DeviceName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (nameMatches.Count == 1)
            {
                return nameMatches[0];
            }
        }

        return null;
    }

    private async Task<bool> WaitForPlaybackAcknowledgementAsync(
        StandardTvPlayFromBeginningRequest request,
        CancellationToken cancellationToken)
    {
        var deadlineUtc = DateTime.UtcNow + AndroidPlaybackAckTimeout;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsPlaybackAcknowledged(request))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadlineUtc)
            {
                return false;
            }

            await Task.Delay(AndroidPlaybackAckPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private bool IsPlaybackAcknowledged(StandardTvPlayFromBeginningRequest request)
    {
        var playingCandidates = GetAndroidClientSessions(request)
            .Where(session => session.NowPlayingItem?.Id == request.ItemId)
            .ToList();

        if (playingCandidates.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.SourceSessionId)
            && playingCandidates.Any(session =>
                string.Equals(session.Id, request.SourceSessionId, StringComparison.Ordinal)))
        {
            return true;
        }

        if (playingCandidates.Any(session =>
            string.Equals(session.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (VirtualTvClientPolicy.IsAndroidMobile(request.ClientName)
            && playingCandidates.Any(session =>
                AreAndroidCompanionDeviceIds(request.DeviceId, session.DeviceId, request.UserId)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(request.DeviceName)
            && playingCandidates.Count(session => string.Equals(
                session.DeviceName,
                request.DeviceName,
                StringComparison.OrdinalIgnoreCase)) == 1;
    }

    private IEnumerable<SessionInfo> GetAndroidClientSessions(StandardTvPlayFromBeginningRequest request)
    {
        var isAndroidMobile = VirtualTvClientPolicy.IsAndroidMobile(request.ClientName);

        return _sessionManager.Sessions.Where(session =>
            session.UserId == request.UserId
            && (isAndroidMobile
                ? VirtualTvClientPolicy.IsAndroidMobile(session.Client)
                : VirtualTvClientPolicy.IsAndroidTv(session.Client)));
    }

    private static bool AreAndroidCompanionDeviceIds(
        string playbackDeviceId,
        string candidateDeviceId,
        Guid userId)
    {
        if (string.Equals(playbackDeviceId, candidateDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(playbackDeviceId)
            || string.IsNullOrWhiteSpace(candidateDeviceId)
            || userId == Guid.Empty)
        {
            return false;
        }

        var userIdDashed = userId.ToString("D");
        var userIdCompact = userId.ToString("N");

        return string.Equals(playbackDeviceId, candidateDeviceId + userIdDashed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidateDeviceId, playbackDeviceId + userIdDashed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(playbackDeviceId, candidateDeviceId + userIdCompact, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidateDeviceId, playbackDeviceId + userIdCompact, StringComparison.OrdinalIgnoreCase);
    }

    private string DescribeAndroidSessions(StandardTvPlayFromBeginningRequest request)
    {
        var sessions = GetAndroidClientSessions(request)
            .Select(session =>
                $"session={session.Id}, device={session.DeviceName}, deviceId={session.DeviceId}, active={session.IsActive}, nowPlaying={session.NowPlayingItem?.Id}, controllers={session.SessionControllers.Count}, activeMediaControllers={session.SessionControllers.Count(controller => controller.IsSessionActive && controller.SupportsMediaControl)}")
            .ToArray();

        return sessions.Length == 0 ? "none" : string.Join(" | ", sessions);
    }

    private static async Task SendRawPlayMessageAsync(
        IEnumerable<ISessionController> controllers,
        PlayRequest request,
        CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        foreach (var controller in controllers)
        {
            await controller.SendMessage(
                SessionMessageType.Play,
                messageId,
                request,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed record StandardTvPlayFromBeginningRequest(
    Guid ItemId,
    Guid UserId,
    string DeviceId,
    string DeviceName,
    string SourceSessionId,
    string ClientName,
    string ChannelName,
    string ClientAction);
