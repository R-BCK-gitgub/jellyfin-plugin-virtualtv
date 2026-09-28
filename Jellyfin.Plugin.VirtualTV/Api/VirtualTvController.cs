using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.VirtualTV.Configuration;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VirtualTV.Api;

/// <summary>
/// Administrator API for Virtual TV preview channels.
/// </summary>
[ApiController]
[Route("VirtualTV")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class VirtualTvController : ControllerBase
{
    private static readonly HashSet<string> SeriesModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sequential", "Random", "NextUnwatched", "RandomUnwatched"
    };

    private static readonly HashSet<string> MovieModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Random", "RandomUnwatched"
    };

    private static readonly HashSet<string> SchedulingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "RepeatingSchedule", "RandomSchedule", "ManualOrder", "SmartSchedule"
    };

    private readonly VirtualTvScheduler _scheduler;
    private readonly IGuideManager _guideManager;
    private readonly IUserManager _userManager;

    public VirtualTvController(
        VirtualTvScheduler scheduler,
        IGuideManager guideManager,
        IUserManager userManager)
    {
        _scheduler = scheduler;
        _guideManager = guideManager;
        _userManager = userManager;
    }

    [HttpGet("Channels")]
    public IActionResult GetChannels()
    {
        var channels = Plugin.Instance?.Configuration.Channels
            .OrderBy(c => c.Number)
            .ToList() ?? new List<ChannelConfiguration>();

        return Ok(channels);
    }

    [HttpGet("Channels/{id}/Schedule")]
    public IActionResult GetSchedule(string id, [FromQuery] int days = 7)
    {
        var channel = Find(id);
        if (channel is null)
        {
            return NotFound();
        }

        days = Math.Clamp(days, 1, 30);
        var now = DateTime.UtcNow;
        var entries = _scheduler.GetEntries(channel, now.AddDays(-1), now.AddDays(days));
        return Ok(new
        {
            Channel = channel,
            Entries = entries,
            NowUtc = now.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    [HttpPost("Channels")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateChannel([FromBody] ChannelConfiguration request, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Virtual TV is not initialized.");
        }

        request.Id = Guid.NewGuid().ToString("N");
        request.CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        request.NeedsReconcile = true;
        var validation = NormalizeAndValidate(request, null);
        if (validation is not null)
        {
            return BadRequest(validation);
        }

        var ownerValidation = ValidateWatchedDependentOwner(request);
        if (ownerValidation is not null)
        {
            return BadRequest(ownerValidation);
        }

        ShiftNumberCollisions(plugin.Configuration.Channels, request.Number, null);
        plugin.Configuration.Channels.Add(request);
        plugin.SaveConfiguration();

        _scheduler.GenerateNew(request, DateTime.UtcNow, cancellationToken);
        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        return Ok(request);
    }

    [HttpPut("Channels/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateChannel(
        string id,
        [FromBody] ChannelConfiguration request,
        CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Virtual TV is not initialized.");
        }

        var existing = Find(id);
        if (existing is null)
        {
            return NotFound();
        }

        request.Id = existing.Id;
        request.CreatedUtc = existing.CreatedUtc;

        var validation = NormalizeAndValidate(request, existing);
        if (validation is not null)
        {
            return BadRequest(validation);
        }

        var ownerValidation = ValidateWatchedDependentOwner(request);
        if (ownerValidation is not null)
        {
            return BadRequest(ownerValidation);
        }

        var scheduleChanged = ScheduleFingerprint(existing) != ScheduleFingerprint(request);
        request.NeedsReconcile = scheduleChanged || existing.NeedsReconcile;

        ShiftNumberCollisions(plugin.Configuration.Channels, request.Number, existing.Id);
        var index = plugin.Configuration.Channels.FindIndex(c => string.Equals(c.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
        plugin.Configuration.Channels[index] = request;
        plugin.SaveConfiguration();

        // Name/number/visibility are reflected by a Guide refresh immediately; schedule content stays intact until Reconcile/Generate.
        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        return Ok(request);
    }

    [HttpDelete("Channels/{id}")]
    public async Task<IActionResult> DeleteChannel(string id, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Virtual TV is not initialized.");
        }

        var removed = plugin.Configuration.Channels.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return NotFound();
        }

        _scheduler.Delete(id);
        plugin.SaveConfiguration();
        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("Channels/{id}/Generate")]
    public async Task<IActionResult> Generate(string id, CancellationToken cancellationToken)
    {
        var channel = Find(id);
        if (channel is null)
        {
            return NotFound();
        }

        _scheduler.GenerateNew(channel, DateTime.UtcNow, cancellationToken);
        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        return Ok(new { Message = "Schedule generated.", ChannelId = id });
    }

    [HttpPost("Channels/{id}/Reconcile")]
    public async Task<IActionResult> Reconcile(string id, CancellationToken cancellationToken)
    {
        var channel = Find(id);
        if (channel is null)
        {
            return NotFound();
        }

        _scheduler.Reconcile(channel, DateTime.UtcNow, cancellationToken);
        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        return Ok(new { Message = "Schedule reconciled.", ChannelId = id });
    }

    [HttpGet("PreviewStatus")]
    public IActionResult PreviewStatus()
    {
        return Ok(new
        {
            Version = "1.0.12.test",
            Build = "1.0.12.9000",
            Purpose = "Final-shape preview based on the v1.0 functional specification.",
            Proven = new[]
            {
                "Native Jellyfin Live TV / Guide integration",
                "Stock Jellyfin player on LG webOS",
                "Wall-clock live entry using source rebasing",
                "Pause/resume and forward seeking",
                "Traditional playback does not use the source item's personal Resume",
                "Client-rendered PGS path preserved from v1.0.12"
            },
            TechnicalValidationStillRequired = new[]
            {
                "Rewind / Start Over to media before the instant a Live TV session was opened",
                "Explicit Go Live button in stock clients without a Jellyfin client fork",
                "Robust future-program click-to-play behavior across stock clients",
                "Native Jellyfin Watched/Resume updates for watched-dependent source items while the stock player is playing a Live TV channel wrapper",
                "Per-user Live TV channel visibility enforced by the stock Live TV pipeline"
            }
        });
    }

    private static ChannelConfiguration? Find(string id)
        => Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeAndValidate(ChannelConfiguration request, ChannelConfiguration? existing)
    {
        request.Name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Channel name is required.";
        }

        if (request.Number <= 0)
        {
            return "Channel number must be greater than zero.";
        }

        if (!string.Equals(request.ChannelType, "Series", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            return "Channel type must be Series or Movies.";
        }

        if (existing is not null
            && (!string.Equals(existing.ChannelType, request.ChannelType, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.ContentMode, request.ContentMode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.SchedulingMethod, request.SchedulingMethod, StringComparison.OrdinalIgnoreCase)))
        {
            return "Channel type, content mode and scheduling method are immutable after creation.";
        }

        var allowedModes = string.Equals(request.ChannelType, "Series", StringComparison.OrdinalIgnoreCase) ? SeriesModes : MovieModes;
        if (!allowedModes.Contains(request.ContentMode))
        {
            return "The selected content mode is not valid for this channel type.";
        }

        if (string.Equals(request.ChannelType, "Series", StringComparison.OrdinalIgnoreCase)
            && !SchedulingMethods.Contains(request.SchedulingMethod))
        {
            return "The selected series scheduling method is invalid.";
        }

        if (string.Equals(request.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            request.SchedulingMethod = "Movies";
        }

        request.SmartRotationMonths = request.SmartRotationMonths is 1 or 2 or 3 or 6 ? request.SmartRotationMonths : 2;
        var watchedDependent = string.Equals(request.ContentMode, "NextUnwatched", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.ContentMode, "RandomUnwatched", StringComparison.OrdinalIgnoreCase);

        if (watchedDependent)
        {
            if (!Guid.TryParse(request.OwnerUserId, out _))
            {
                return "Watched-dependent channels require an administrator owner.";
            }

            request.VisibleToAllUsers = false;
            request.VisibleUserIds = [request.OwnerUserId];
        }

        if (request.Content is null)
        {
            request.Content = new List<ChannelContentConfiguration>();
        }

        for (var i = 0; i < request.Content.Count; i++)
        {
            var item = request.Content[i];
            item.SortOrder = i;
            item.ConsecutiveEpisodes = Math.Clamp(item.ConsecutiveEpisodes, 1, 12);
        }

        if (string.Equals(request.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = request.Content.Where(c => string.Equals(c.ItemType, "Movie", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            request.Content = request.Content.Where(c => string.Equals(c.ItemType, "Series", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (string.Equals(request.ContentMode, "RandomUnwatched", StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            request.DynamicBlockMinutes = NormalizeBlock(request.DynamicBlockMinutes, true);
        }
        else if (watchedDependent)
        {
            request.DynamicBlockMinutes = NormalizeBlock(request.DynamicBlockMinutes, false);
        }

        request.OnAirStart = NormalizeTime(request.OnAirStart, "07:00");
        request.OffAirStart = NormalizeTime(request.OffAirStart, "02:00");
        request.VisibleUserIds ??= new List<string>();
        request.ManualSeriesOrder ??= new List<string>();
        request.RepeatingSeriesOrder ??= new List<string>();
        return null;
    }

    private static int NormalizeBlock(int value, bool movie)
    {
        var allowed = movie ? new[] { 15, 20, 30, 40, 45, 60, 90 } : new[] { 15, 20, 30, 40, 45, 60 };
        return allowed.Contains(value) ? value : (movie ? 90 : 30);
    }

    private static string NormalizeTime(string value, string fallback)
        => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : fallback;

    private static string ScheduleFingerprint(ChannelConfiguration channel)
    {
        var scheduleRelevant = new
        {
            channel.Is24Hours,
            channel.OnAirStart,
            channel.OffAirStart,
            channel.SmartRotationMonths,
            channel.DynamicBlockMinutes,
            channel.Content,
            channel.ManualSeriesOrder
        };

        return JsonSerializer.Serialize(scheduleRelevant);
    }

    private string? ValidateWatchedDependentOwner(ChannelConfiguration channel)
    {
        var watchedDependent = string.Equals(channel.ContentMode, "NextUnwatched", StringComparison.OrdinalIgnoreCase)
            || string.Equals(channel.ContentMode, "RandomUnwatched", StringComparison.OrdinalIgnoreCase);

        if (!watchedDependent)
        {
            return null;
        }

        if (!Guid.TryParse(channel.OwnerUserId, out var ownerId))
        {
            return "Watched-dependent channels require an administrator owner.";
        }

        var owner = _userManager.GetUserById(ownerId);
        if (owner is null || !owner.HasPermission(PermissionKind.IsAdministrator))
        {
            return "Watched-dependent channels can only belong to a Jellyfin administrator.";
        }

        channel.VisibleToAllUsers = false;
        channel.VisibleUserIds = [channel.OwnerUserId];
        return null;
    }

    private static void ShiftNumberCollisions(List<ChannelConfiguration> channels, int targetNumber, string? movingId)
    {
        var candidates = channels
            .Where(c => movingId is null || !string.Equals(c.Id, movingId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var byNumber = candidates
            .GroupBy(c => c.Number)
            .ToDictionary(g => g.Key, g => g.First());

        if (!byNumber.ContainsKey(targetNumber))
        {
            return;
        }

        var lastOccupied = targetNumber;
        while (byNumber.ContainsKey(lastOccupied + 1))
        {
            lastOccupied++;
        }

        for (var number = lastOccupied; number >= targetNumber; number--)
        {
            if (byNumber.TryGetValue(number, out var existing))
            {
                existing.Number = number + 1;
            }
        }
    }
}
