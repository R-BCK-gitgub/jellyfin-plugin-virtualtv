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
/// A deferred Standard TV Play-from-Beginning handoff for Android clients.
///
/// Jellyfin for Android and Jellyfin for Android TV may expose multiple sessions/controllers for the
/// same physical device. Keeping the Record HTTP request open while changing player state can leave
/// the Web player loading overlay waiting forever. This dispatcher lets the Record endpoint return
/// its synthetic timer result immediately, then resolves the active media-control controller and
/// sends exactly one Play message for the concrete library item.
/// </summary>
public sealed class VirtualTvPlaybackCommandDispatcher : BackgroundService
{
    private static readonly TimeSpan AndroidRecordResponseSettleDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan AndroidControlReadyTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AndroidControlReadyPollInterval = TimeSpan.FromMilliseconds(200);

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
                        "Virtual TV Android Play from Beginning failed for channel {ChannelName}, item {ItemId}, client {Client}, device {DeviceId}.",
                        request.ChannelName,
                        request.ItemId,
                        request.ClientName,
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

        // Give the create-timer HTTP request time to reach the client first. In Jellyfin Web this
        // closes the native Record loading overlay before the player receives its replacement Play.
        await Task.Delay(AndroidRecordResponseSettleDelay, cancellationToken).ConfigureAwait(false);

        var targetSession = await WaitForAndroidControllableSessionAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (targetSession is null)
        {
            _logger.LogError(
                "Virtual TV could not find the active Android media-control session for channel {ChannelName}, item {ItemId}, client {Client}, device {DeviceId}. Android sessions: {Sessions}",
                request.ChannelName,
                request.ItemId,
                request.ClientName,
                request.DeviceId,
                DescribeAndroidSessions(request));
            return;
        }

        var controllers = targetSession.SessionControllers
            .Where(controller => controller.IsSessionActive && controller.SupportsMediaControl)
            .ToArray();
        if (controllers.Length == 0)
        {
            _logger.LogError(
                "Virtual TV resolved Android session {SessionId} for Play from Beginning, but it has no active media-control controller.",
                targetSession.Id);
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

        await SendRawPlayMessageAsync(controllers, playRequest, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Virtual TV Standard TV Android Play from Beginning: {ClientAction} on channel {ChannelName} opened item {ItemId} from 00:00 through control session {SessionId}, client {Client}, device {DeviceId}. No DVR timer was stored.",
            request.ClientAction,
            request.ChannelName,
            request.ItemId,
            targetSession.Id,
            request.ClientName,
            request.DeviceId);
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
        var isAndroidMobile = VirtualTvClientPolicy.IsAndroidMobile(request.ClientName);

        var candidates = _sessionManager.Sessions
            .Where(session =>
                session.UserId == request.UserId
                && session.IsActive
                && (isAndroidMobile
                    ? VirtualTvClientPolicy.IsAndroidMobile(session.Client)
                    : VirtualTvClientPolicy.IsAndroidTv(session.Client))
                && session.SessionControllers.Any(
                    controller => controller.IsSessionActive && controller.SupportsMediaControl))
            .OrderByDescending(session => session.LastPlaybackCheckIn)
            .ThenByDescending(session => session.LastActivityDate)
            .ToList();

        var exactDevice = candidates.FirstOrDefault(session =>
            string.Equals(session.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (exactDevice is not null)
        {
            return exactDevice;
        }

        if (isAndroidMobile)
        {
            var companionDevice = candidates.FirstOrDefault(session =>
                AreAndroidCompanionDeviceIds(request.DeviceId, session.DeviceId, request.UserId));
            if (companionDevice is not null)
            {
                return companionDevice;
            }
        }

        // Defensive fallback matching the proven Personalized TV Android handoff: only use a
        // device-name match when it is unique for this user. Never choose an arbitrary session.
        var sourceDeviceName = _sessionManager.Sessions
            .Where(session =>
                session.UserId == request.UserId
                && string.Equals(session.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.LastPlaybackCheckIn)
            .ThenByDescending(session => session.LastActivityDate)
            .Select(session => session.DeviceName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        if (!string.IsNullOrWhiteSpace(sourceDeviceName))
        {
            var nameMatches = candidates
                .Where(session => string.Equals(
                    session.DeviceName,
                    sourceDeviceName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (nameMatches.Count == 1)
            {
                return nameMatches[0];
            }
        }

        return null;
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
        var isAndroidMobile = VirtualTvClientPolicy.IsAndroidMobile(request.ClientName);

        var sessions = _sessionManager.Sessions
            .Where(session =>
                session.UserId == request.UserId
                && (isAndroidMobile
                    ? VirtualTvClientPolicy.IsAndroidMobile(session.Client)
                    : VirtualTvClientPolicy.IsAndroidTv(session.Client)))
            .Select(session =>
                $"session={session.Id}, device={session.DeviceName}, deviceId={session.DeviceId}, active={session.IsActive}, controllers={session.SessionControllers.Count}, activeMediaControllers={session.SessionControllers.Count(controller => controller.IsSessionActive && controller.SupportsMediaControl)}")
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
    string ClientName,
    string ChannelName,
    string ClientAction);
