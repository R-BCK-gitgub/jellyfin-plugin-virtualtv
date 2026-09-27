using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VirtualTV.Api;

/// <summary>
/// Temporary architecture-validation endpoints used to prove stock Jellyfin session playback at a specific offset.
/// </summary>
[ApiController]
[Route("VirtualTV/Architecture")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ArchitectureController : ControllerBase
{
    private readonly ISessionManager _sessionManager;
    private readonly PlaybackStateProtectionManager _stateProtection;
    private readonly IGuideManager _guideManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchitectureController"/> class.
    /// </summary>
    /// <param name="sessionManager">Jellyfin session manager.</param>
    /// <param name="stateProtection">Temporary state-protection service.</param>
    /// <param name="guideManager">Jellyfin Live TV guide manager.</param>
    /// <param name="liveTvManager">Jellyfin Live TV manager.</param>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    public ArchitectureController(
        ISessionManager sessionManager,
        PlaybackStateProtectionManager stateProtection,
        IGuideManager guideManager,
        ILiveTvManager liveTvManager,
        ILibraryManager libraryManager)
    {
        _sessionManager = sessionManager;
        _stateProtection = stateProtection;
        _guideManager = guideManager;
        _liveTvManager = liveTvManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Sends a PlayNow command to an existing Jellyfin session using the requested start position.
    /// </summary>
    /// <param name="request">Playback validation request.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>No content when the command was accepted.</returns>
    [HttpPost("PlaybackTest")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PlaybackTest(
        [FromBody] PlaybackTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest("SessionId is required.");
        }

        if (!Guid.TryParse(request.ItemId, out var itemId))
        {
            return BadRequest("ItemId must be a valid Jellyfin item id.");
        }

        if (request.StartPositionTicks < 0)
        {
            return BadRequest("StartPositionTicks cannot be negative.");
        }

        var command = new PlayRequest
        {
            ItemIds = [itemId],
            StartPositionTicks = request.StartPositionTicks,
            PlayCommand = PlayCommand.PlayNow
        };

        // For this architectural proof the target session controls itself. The final runtime
        // flow will pass the actual controlling session context when resolving a Virtual TV tune.
        await _sessionManager.SendPlayCommand(
            request.SessionId,
            request.SessionId,
            command,
            cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Starts playback at an exact offset while preserving the item's pre-test Watched/Resume state.
    /// </summary>
    /// <param name="request">Playback validation request.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>No content when the command was accepted.</returns>
    [HttpPost("ProtectedPlaybackTest")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProtectedPlaybackTest(
        [FromBody] PlaybackTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest("SessionId is required.");
        }

        if (!Guid.TryParse(request.ItemId, out var itemId))
        {
            return BadRequest("ItemId must be a valid Jellyfin item id.");
        }

        if (request.StartPositionTicks < 0)
        {
            return BadRequest("StartPositionTicks cannot be negative.");
        }

        var targetSession = _sessionManager.Sessions.FirstOrDefault(
            session => string.Equals(session.Id, request.SessionId, StringComparison.Ordinal));

        if (targetSession is null || targetSession.UserId == Guid.Empty)
        {
            return BadRequest("The target session is no longer active or has no authenticated user.");
        }

        if (!_stateProtection.BeginProtection(request.SessionId, itemId, targetSession.UserId))
        {
            return BadRequest("The existing Jellyfin user state could not be captured for this item.");
        }

        var command = new PlayRequest
        {
            ItemIds = [itemId],
            StartPositionTicks = request.StartPositionTicks,
            PlayCommand = PlayCommand.PlayNow
        };

        try
        {
            await _sessionManager.SendPlayCommand(
                request.SessionId,
                request.SessionId,
                command,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _stateProtection.CancelProtection(request.SessionId);
            throw;
        }

        return NoContent();
    }

    /// <summary>
    /// Configures and materializes a temporary native Jellyfin Live TV channel backed by an existing library item.
    /// </summary>
    /// <param name="request">Live TV architecture-test setup request.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Information about the materialized channel.</returns>
    [HttpPost("PrepareLiveTvTest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PrepareLiveTvTest(
        [FromBody] LiveTvTestSetupRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.ItemId, out var itemId))
        {
            return BadRequest("ItemId must be a valid Jellyfin item id.");
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return BadRequest("The selected Jellyfin item no longer exists.");
        }

        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Virtual TV is not initialized.");
        }

        plugin.Configuration.ArchitectureLiveTvTestItemId = itemId.ToString("N");
        plugin.Configuration.ArchitectureLiveTvTestEnabled = true;
        plugin.SaveConfiguration();

        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);

        var channel = FindArchitectureLiveTvChannel(cancellationToken);
        if (channel is null)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                "Jellyfin refreshed the guide but the Virtual TV test channel was not materialized.");
        }

        return Ok(new
        {
            ChannelId = channel.Id.ToString("N"),
            ChannelName = channel.Name,
            ChannelNumber = channel.Number,
            SourceItemName = item.Name
        });
    }

    /// <summary>
    /// Plays the temporary native Jellyfin Live TV channel at an explicit offset.
    /// The underlying library item supplies media bytes but is not the now-playing item.
    /// </summary>
    /// <param name="request">Live TV playback-test request.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>No content when the command was accepted.</returns>
    [HttpPost("LiveTvPlaybackTest")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LiveTvPlaybackTest(
        [FromBody] LiveTvPlaybackTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest("SessionId is required.");
        }

        if (request.StartPositionTicks < 0)
        {
            return BadRequest("StartPositionTicks cannot be negative.");
        }

        var channel = FindArchitectureLiveTvChannel(cancellationToken);
        if (channel is null)
        {
            return BadRequest("Prepare the Virtual TV Live TV architecture test channel first.");
        }

        var command = new PlayRequest
        {
            ItemIds = [channel.Id],
            StartPositionTicks = 0,
            PlayCommand = PlayCommand.PlayNow
        };

        await _sessionManager.SendPlayCommand(
            request.SessionId,
            request.SessionId,
            command,
            cancellationToken).ConfigureAwait(false);

        if (request.StartPositionTicks > 0)
        {
            var channelStarted = false;

            // Jellyfin clients report the Live TV channel back to the server after the player has opened it.
            // Wait for that report before sending the seek; sending it together with PlayNow is ignored by
            // some Live TV clients (including the LG webOS client validated for this plugin).
            for (var attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var session = _sessionManager.Sessions.FirstOrDefault(
                    item => string.Equals(item.Id, request.SessionId, StringComparison.Ordinal));

                if (session?.NowPlayingItem?.Id == channel.Id)
                {
                    channelStarted = true;
                    break;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            if (!channelStarted)
            {
                return StatusCode(
                    StatusCodes.Status504GatewayTimeout,
                    "The Live TV channel started command was sent, but the target session did not report playback in time.");
            }

            // Give the local player a short moment to attach the opened media source before seeking.
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            await _sessionManager.SendPlaystateCommand(
                request.SessionId,
                request.SessionId,
                new PlaystateRequest
                {
                    Command = PlaystateCommand.Seek,
                    SeekPositionTicks = request.StartPositionTicks
                },
                cancellationToken).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Removes the temporary Live TV architecture-test channel and refreshes Jellyfin's guide.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>No content when cleanup is complete.</returns>
    [HttpPost("RemoveLiveTvTest")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveLiveTvTest(CancellationToken cancellationToken)
    {
        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        if (plugin is not null)
        {
            plugin.Configuration.ArchitectureLiveTvTestEnabled = false;
            plugin.Configuration.ArchitectureLiveTvTestItemId = string.Empty;
            plugin.SaveConfiguration();
        }

        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private LiveTvChannel? FindArchitectureLiveTvChannel(CancellationToken cancellationToken)
    {
        var channels = _liveTvManager.GetInternalChannels(
            new LiveTvChannelQuery(),
            new DtoOptions(),
            cancellationToken);

        return channels.Items
            .OfType<LiveTvChannel>()
            .FirstOrDefault(channel =>
                string.Equals(
                    channel.ServiceName,
                    VirtualTvLiveTvService.ServiceName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    channel.ExternalId,
                    VirtualTvLiveTvService.ArchitectureTestChannelId,
                    StringComparison.Ordinal));
    }

}

/// <summary>
/// Request used by the temporary playback architecture validation.
/// </summary>
public sealed class PlaybackTestRequest
{
    /// <summary>
    /// Gets or sets the target Jellyfin session id.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin media item id.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the requested playback offset in Jellyfin ticks.
    /// </summary>
    public long StartPositionTicks { get; set; }
}


/// <summary>
/// Request used to prepare the temporary native Live TV architecture test channel.
/// </summary>
public sealed class LiveTvTestSetupRequest
{
    /// <summary>
    /// Gets or sets the existing Jellyfin media item used as the channel source.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;
}

/// <summary>
/// Request used to start the temporary native Live TV channel on a Jellyfin session.
/// </summary>
public sealed class LiveTvPlaybackTestRequest
{
    /// <summary>
    /// Gets or sets the target Jellyfin session id.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the requested playback offset in Jellyfin ticks.
    /// </summary>
    public long StartPositionTicks { get; set; }
}
