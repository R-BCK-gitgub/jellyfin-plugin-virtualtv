using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VirtualTV.Api;

[ApiController]
[Route("VirtualTV/Channels")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ChannelController : ControllerBase
{
    private readonly VirtualTvScheduleGenerator _generator;
    private readonly VirtualTvScheduleStore _store;
    private readonly IGuideManager _guideManager;

    public ChannelController(VirtualTvScheduleGenerator generator, VirtualTvScheduleStore store, IGuideManager guideManager)
    {
        _generator = generator;
        _store = store;
        _guideManager = guideManager;
    }

    [HttpPost("{channelId}/GenerateSchedule")]
    public async Task<IActionResult> GenerateSchedule(string channelId, CancellationToken cancellationToken)
    {
        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        var channel = plugin?.Configuration.Channels.FirstOrDefault(item => string.Equals(item.Id, channelId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null || channel is null) return NotFound("Virtual TV channel not found.");

        try
        {
            var entries = _generator.Generate(channel);
            plugin.SaveConfiguration();
            await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            return Ok(new { Count = entries.Count, GeneratedUtc = channel.ScheduleGeneratedUtc, EndUtc = channel.ScheduleEndUtc });
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

    [HttpDelete("{channelId}/Schedule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSchedule(string channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(channelId, out _)) return BadRequest("Invalid channel id.");
        _store.Delete(channelId);
        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
