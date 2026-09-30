using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Tasks;

public sealed class VirtualTvReconcileSchedulesTask : IScheduledTask
{
    private readonly VirtualTvScheduleGenerator _generator;
    private readonly IGuideManager _guideManager;
    private readonly VirtualTvVisibilityManager _visibility;
    private readonly ILogger<VirtualTvReconcileSchedulesTask> _logger;

    public VirtualTvReconcileSchedulesTask(
        VirtualTvScheduleGenerator generator,
        IGuideManager guideManager,
        VirtualTvVisibilityManager visibility,
        ILogger<VirtualTvReconcileSchedulesTask> logger)
    {
        _generator = generator;
        _guideManager = guideManager;
        _visibility = visibility;
        _logger = logger;
    }

    public string Name => "Virtual TV — Reconcile Channel Schedules";
    public string Key => "VirtualTvReconcileChannelSchedules";
    public string Description => "Applies changed content and channel options to future schedule without altering history or the current block.";
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

            if (channel.NeedsReconcile || _generator.HasReconcileChanges(channel))
            {
                try
                {
                    _generator.Reconcile(channel);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Virtual TV failed to reconcile channel {ChannelName}.", channel.Name);
                    throw;
                }
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
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
        };
    }
}
