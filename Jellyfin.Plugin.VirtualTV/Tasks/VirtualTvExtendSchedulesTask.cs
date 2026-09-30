using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Tasks;

public sealed class VirtualTvExtendSchedulesTask : IScheduledTask
{
    private readonly VirtualTvScheduleGenerator _generator;
    private readonly IGuideManager _guideManager;
    private readonly VirtualTvVisibilityManager _visibility;
    private readonly ILogger<VirtualTvExtendSchedulesTask> _logger;

    public VirtualTvExtendSchedulesTask(
        VirtualTvScheduleGenerator generator,
        IGuideManager guideManager,
        VirtualTvVisibilityManager visibility,
        ILogger<VirtualTvExtendSchedulesTask> logger)
    {
        _generator = generator;
        _guideManager = guideManager;
        _visibility = visibility;
        _logger = logger;
    }

    public string Name => "Virtual TV — Extend Channel Schedules";
    public string Key => "VirtualTvExtendChannelSchedules";
    public string Description => "Maintains at least 30 days of future Virtual TV schedule and keeps seven days of history.";
    public string Category => "Live TV";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        if (plugin is null)
        {
            progress.Report(100);
            return;
        }

        var channels = plugin.Configuration.Channels;
        for (var index = 0; index < channels.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = channels[index];

            try
            {
                _generator.Extend(channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Virtual TV failed to extend channel {ChannelName}.", channel.Name);
                throw;
            }

            progress.Report((index + 1) * 85d / Math.Max(1, channels.Count));
        }

        plugin.SaveConfiguration();
        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        await _visibility.ApplyAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Monday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}
