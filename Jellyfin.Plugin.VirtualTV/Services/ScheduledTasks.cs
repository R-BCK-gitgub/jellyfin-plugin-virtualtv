using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Weekly schedule extension and housekeeping task.
/// </summary>
public sealed class ExtendChannelSchedulesTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly VirtualTvScheduler _scheduler;
    private readonly IGuideManager _guideManager;

    public ExtendChannelSchedulesTask(VirtualTvScheduler scheduler, IGuideManager guideManager)
    {
        _scheduler = scheduler;
        _guideManager = guideManager;
    }

    public string Name => "Virtual TV — Extend Channel Schedules";
    public string Key => "VirtualTVExtendChannelSchedules";
    public string Description => "Keeps Virtual TV channels at least 30 days ahead, retains seven days of history and rotates Smart templates when due.";
    public string Category => "Virtual TV";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Monday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var channels = Plugin.Instance?.Configuration.Channels.ToList() ?? [];
        for (var i = 0; i < channels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _scheduler.Extend(channels[i], DateTime.UtcNow, cancellationToken);
            progress.Report(channels.Count == 0 ? 100 : (i + 1) * 90d / channels.Count);
        }

        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}

/// <summary>
/// Daily future-schedule reconciliation task.
/// </summary>
public sealed class ReconcileChannelSchedulesTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly VirtualTvScheduler _scheduler;
    private readonly IGuideManager _guideManager;

    public ReconcileChannelSchedulesTask(VirtualTvScheduler scheduler, IGuideManager guideManager)
    {
        _scheduler = scheduler;
        _guideManager = guideManager;
    }

    public string Name => "Virtual TV — Reconcile Channel Schedules";
    public string Key => "VirtualTVReconcileChannelSchedules";
    public string Description => "Applies content and configuration changes to the future portion of Virtual TV schedules without altering protected history/current blocks.";
    public string Category => "Virtual TV";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var channels = Plugin.Instance?.Configuration.Channels.ToList() ?? [];
        for (var i = 0; i < channels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (channels[i].NeedsReconcile)
            {
                _scheduler.Reconcile(channels[i], DateTime.UtcNow, cancellationToken);
            }

            progress.Report(channels.Count == 0 ? 100 : (i + 1) * 90d / channels.Count);
        }

        await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
