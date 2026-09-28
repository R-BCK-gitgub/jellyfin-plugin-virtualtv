using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VirtualTV.Api;

[ApiController]
[Route("VirtualTV/Channels")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ChannelController : ControllerBase
{
    private const string LegacyArchitectureChannelId = "virtualtv-architecture-test";

    private readonly VirtualTvScheduleGenerator _generator;
    private readonly VirtualTvScheduleStore _store;
    private readonly IGuideManager _guideManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly ILibraryManager _libraryManager;

    public ChannelController(
        VirtualTvScheduleGenerator generator,
        VirtualTvScheduleStore store,
        IGuideManager guideManager,
        ILiveTvManager liveTvManager,
        ILibraryManager libraryManager)
    {
        _generator = generator;
        _store = store;
        _guideManager = guideManager;
        _liveTvManager = liveTvManager;
        _libraryManager = libraryManager;
    }

    [HttpPost("{channelId}/GenerateSchedule")]
    public async Task<IActionResult> GenerateSchedule(string channelId, CancellationToken cancellationToken)
    {
        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        var channel = plugin?.Configuration.Channels.FirstOrDefault(
            item => string.Equals(item.Id, channelId, StringComparison.OrdinalIgnoreCase));

        if (plugin is null || channel is null)
        {
            return NotFound("Virtual TV channel not found.");
        }

        try
        {
            var entries = _generator.Generate(channel);
            plugin.SaveConfiguration();
            await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);

            return Ok(new
            {
                Count = entries.Count,
                GeneratedUtc = channel.ScheduleGeneratedUtc,
                EndUtc = channel.ScheduleEndUtc
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("RefreshGuide")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RefreshGuide(CancellationToken cancellationToken)
    {
        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes the one-off channel 9999 used by the retired playback architecture test.
    /// Safe to call repeatedly.
    /// </summary>
    [HttpPost("CleanupLegacyTestChannel")]
    public async Task<IActionResult> CleanupLegacyTestChannel(CancellationToken cancellationToken)
    {
        var channels = _liveTvManager.GetInternalChannels(
            new LiveTvChannelQuery(),
            new DtoOptions(),
            cancellationToken);

        var legacyChannels = channels.Items
            .OfType<LiveTvChannel>()
            .Where(channel =>
                string.Equals(channel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(channel.ExternalId, LegacyArchitectureChannelId, StringComparison.Ordinal))
            .ToList();

        foreach (var channel in legacyChannels)
        {
            _libraryManager.DeleteItem(
                channel,
                new DeleteOptions
                {
                    DeleteFileLocation = false,
                    DeleteFromExternalProvider = false
                },
                false);
        }

        if (legacyChannels.Count > 0)
        {
            await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        }

        return Ok(new { Removed = legacyChannels.Count });
    }

    [HttpDelete("{channelId}/Schedule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSchedule(string channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(channelId, out _))
        {
            return BadRequest("Invalid channel id.");
        }

        _store.Delete(channelId);
        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
