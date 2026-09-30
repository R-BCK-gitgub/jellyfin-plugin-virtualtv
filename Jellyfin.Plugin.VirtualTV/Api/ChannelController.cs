using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
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

    [HttpGet("ContentCoverage")]
    public IActionResult GetContentCoverage()
    {
        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        if (plugin is null)
        {
            return Ok(new { GeneratedUtc = DateTime.UtcNow, Items = Array.Empty<object>() });
        }

        var channels = plugin.Configuration.Channels
            .OrderBy(channel => channel.Number)
            .ToArray();

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
            IsVirtualItem = false
        })
        .Where(item => item.GetBaseItemKind() is BaseItemKind.Series or BaseItemKind.Movie)
        .GroupBy(item => item.Id)
        .Select(group => group.First())
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .Select(item =>
        {
            var assignments = channels
                .Where(channel => channel.SelectedItemIds.Any(raw =>
                    Guid.TryParse(raw, out var selectedId) && selectedId == item.Id))
                .Select(channel => new CoverageChannel(
                    channel.Id,
                    channel.Number,
                    channel.Name))
                .ToArray();

            var library = item.GetTopParent();
            return new CoverageItem(
                item.Id.ToString("N"),
                item.Name ?? string.Empty,
                item.GetBaseItemKind() == BaseItemKind.Movie ? "Movie" : "Series",
                item.ProductionYear,
                library?.Id.ToString("N") ?? string.Empty,
                library?.Name ?? "Unknown library",
                assignments,
                assignments.Length == 0 ? "Unassigned" : assignments.Length > 1 ? "Multiple" : "Assigned",
                false);
        })
        .ToList();

        var existingIds = items
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in channels)
        {
            foreach (var rawId in channel.SelectedItemIds)
            {
                if (string.IsNullOrWhiteSpace(rawId) || existingIds.Contains(rawId.Replace("-", string.Empty, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!Guid.TryParse(rawId, out var missingId) || _libraryManager.GetItemById(missingId) is not null)
                {
                    continue;
                }

                var normalized = missingId.ToString("N");
                if (items.Any(item => string.Equals(item.Id, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                items.Add(new CoverageItem(
                    normalized,
                    "Missing library item",
                    channel.ChannelType,
                    null,
                    string.Empty,
                    "Unavailable",
                    [new CoverageChannel(channel.Id, channel.Number, channel.Name)],
                    "Missing",
                    true));
            }
        }

        return Ok(new
        {
            GeneratedUtc = DateTime.UtcNow,
            Items = items
                .OrderBy(item => item.LibraryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        });
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
            await _visibility.ApplyAsync(cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Virtual TV requested a Guide/visibility refresh, but Jellyfin could not complete it.");
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

    private sealed record CoverageChannel(string Id, int Number, string Name);

    private sealed record CoverageItem(
        string Id,
        string Title,
        string Type,
        int? ProductionYear,
        string LibraryId,
        string LibraryName,
        IReadOnlyList<CoverageChannel> Channels,
        string Status,
        bool Missing);

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
            await _visibility.ApplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The schedule reset itself succeeded. Do not report the configuration save as failed
            // merely because Jellyfin could not refresh the Guide/visibility at that exact moment.
            _logger.LogWarning(ex, "Virtual TV schedule was reset for channel {ChannelId}, but Guide/visibility refresh failed.", channelId);
        }

        return NoContent();
    }
}
