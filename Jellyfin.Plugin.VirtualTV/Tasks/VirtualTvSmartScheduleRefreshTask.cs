using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Tasks;

/// <summary>
/// Refreshes Smart Schedule templates before their configured rotation window expires.
/// The task is also visible in Jellyfin Scheduled Tasks for manual execution.
/// </summary>
public sealed class VirtualTvSmartScheduleRefreshTask : IScheduledTask
{
    private static readonly TimeSpan RefreshLeadTime = TimeSpan.FromHours(12);

    private readonly VirtualTvScheduleGenerator _generator;
    private readonly IGuideManager _guideManager;
    private readonly ILogger<VirtualTvSmartScheduleRefreshTask> _logger;

    public VirtualTvSmartScheduleRefreshTask(
        VirtualTvScheduleGenerator generator,
        IGuideManager guideManager,
        ILogger<VirtualTvSmartScheduleRefreshTask> logger)
    {
        _generator = generator;
        _guideManager = guideManager;
        _logger = logger;
    }

    public string Name => "Refresh Virtual TV Smart Schedules";

    public string Key => "VirtualTvRefreshSmartSchedules";

    public string Description
        => "Regenerates Smart Schedule weekly templates when their configured rotation period is about to expire.";

    public string Category => "Live TV";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        if (plugin is null)
        {
            progress.Report(100);
            return;
        }

        var channels = plugin.Configuration.Channels
            .Where(channel => string.Equals(
                VirtualTvModePolicy.NormalizeSchedulingMethod(channel.SchedulingMethod),
                VirtualTvModePolicy.SmartSchedule,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (channels.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var refreshed = 0;
        var nowUtc = DateTime.UtcNow;

        for (var index = 0; index < channels.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = channels[index];

            if (!TryGetEndUtc(channel.ScheduleEndUtc, out var scheduleEndUtc)
                || channel.NeedsReconcile
                || scheduleEndUtc <= nowUtc.Add(RefreshLeadTime))
            {
                var startUtc = scheduleEndUtc > nowUtc ? scheduleEndUtc : nowUtc;

                try
                {
                    _generator.Generate(channel, startUtc, preserveBeforeStart: scheduleEndUtc > nowUtc);
                    refreshed++;

                    _logger.LogInformation(
                        "Virtual TV regenerated Smart Schedule for channel {ChannelName}; new horizon ends {EndUtc}.",
                        channel.Name,
                        channel.ScheduleEndUtc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Virtual TV failed to regenerate Smart Schedule for channel {ChannelName}.",
                        channel.Name);
                }
            }

            progress.Report((index + 1) * 90d / channels.Count);
        }

        if (refreshed > 0)
        {
            plugin.SaveConfiguration();

            try
            {
                await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Virtual TV Smart Schedules were refreshed, but the Jellyfin Guide refresh failed.");
            }
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }

    private static bool TryGetEndUtc(string value, out DateTime endUtc)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            endUtc = parsed.ToUniversalTime();
            return true;
        }

        endUtc = DateTime.MinValue;
        return false;
    }
}
