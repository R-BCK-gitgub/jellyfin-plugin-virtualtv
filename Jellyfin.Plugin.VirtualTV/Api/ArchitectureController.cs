using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Session;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchitectureController"/> class.
    /// </summary>
    /// <param name="sessionManager">Jellyfin session manager.</param>
    public ArchitectureController(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
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
