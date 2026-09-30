using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
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
using Microsoft.Extensions.Logging;

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
    private readonly IUserManager _userManager;
    private readonly VirtualTvContentCatalog _catalog;
    private readonly VirtualTvVisibilityManager _visibility;
    private readonly ILogger<ChannelController> _logger;

    public ChannelController(
        VirtualTvScheduleGenerator generator,
        VirtualTvScheduleStore store,
        IGuideManager guideManager,
        ILiveTvManager liveTvManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        VirtualTvContentCatalog catalog,
        VirtualTvVisibilityManager visibility,
        ILogger<ChannelController> logger)
    {
        _generator = generator;
        _store = store;
        _guideManager = guideManager;
        _liveTvManager = liveTvManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _catalog = catalog;
        _visibility = visibility;
        _logger = logger;
    }

    [HttpPost("{channelId}/GenerateSchedule")]
    public async Task<IActionResult> GenerateSchedule(string channelId, CancellationToken cancellationToken)
    {
        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        var channel = plugin?.Configuration.Channels.FirstOrDefault(
            item => string.Equals(item.Id, channelId, StringComparison.OrdinalIgnoreCase));

        if (plugin is null || channel is null)
        {
            return NotFound(new
            {
                Stage = "lookup",
                Message = "Virtual TV channel not found."
            });
        }

        IReadOnlyList<VirtualTvScheduleEntry> entries;

        try
        {
            _logger.LogInformation(
                "Generating Virtual TV schedule for channel {ChannelName} ({ChannelId}).",
                channel.Name,
                channel.Id);

            entries = _generator.Generate(channel);
            plugin.SaveConfiguration();

            _logger.LogInformation(
                "Generated {Count} Virtual TV schedule entries for channel {ChannelName}; horizon ends {EndUtc}.",
                entries.Count,
                channel.Name,
                channel.ScheduleEndUtc);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Virtual TV schedule generation rejected for channel {ChannelName}.", channel.Name);
            return BadRequest(new
            {
                Stage = "generation",
                Message = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Virtual TV schedule generation failed for channel {ChannelName}.", channel.Name);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Stage = "generation",
                Message = "The schedule could not be generated."
            });
        }

        var guideRefreshed = true;
        string? warning = null;

        try
        {
            await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            await _visibility.ApplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            guideRefreshed = false;
            warning = "The schedule was generated, but Jellyfin could not refresh the Live TV Guide automatically.";
            _logger.LogWarning(ex, "Virtual TV schedule was generated for {ChannelName}, but Guide refresh failed.", channel.Name);
        }

        return Ok(new
        {
            Count = entries.Count,
            GeneratedUtc = channel.ScheduleGeneratedUtc,
            EndUtc = channel.ScheduleEndUtc,
            GuideRefreshed = guideRefreshed,
            Warning = warning
        });
    }

    [HttpGet("Users")]
    public IActionResult GetUsers()
    {
        var users = _userManager.GetUsers()
            .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .Select(user => new
            {
                Id = user.Id.ToString("N"),
                Name = user.Username,
                IsAdministrator = user.HasPermission(PermissionKind.IsAdministrator)
            })
            .ToArray();

        return Ok(users);
    }

    [HttpGet("Series/{seriesId}/Seasons")]
    public IActionResult GetSeriesSeasons(string seriesId)
    {
        if (!Guid.TryParse(seriesId, out var id))
        {
            return BadRequest(new { Message = "Invalid series id." });
        }

        return Ok(_catalog.GetAvailableSeasonNumbers(id));
    }

    [HttpPost("RefreshGuide")]
    public async Task<IActionResult> RefreshGuide(CancellationToken cancellationToken)
    {
        try
        {
            await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Virtual TV requested a Guide refresh, but Jellyfin could not complete it.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Stage = "guide",
                Message = "Jellyfin could not refresh the Live TV Guide."
            });
        }
    }

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
            try
            {
                await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Legacy Virtual TV test channel was removed, but Guide refresh failed.");
            }
        }

        return Ok(new { Removed = legacyChannels.Count });
    }

    [HttpDelete("{channelId}/Schedule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSchedule(string channelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return BadRequest(new
            {
                Stage = "schedule",
                Message = "Invalid channel id."
            });
        }

        _store.Delete(channelId);

        try
        {
            await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The schedule reset itself succeeded. Do not report the configuration save as failed
            // merely because Jellyfin could not refresh the Guide at that exact moment.
            _logger.LogWarning(ex, "Virtual TV schedule was reset for channel {ChannelId}, but Guide refresh failed.", channelId);
        }

        return NoContent();
    }
}
