using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Services;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Tasks;

public sealed class VirtualTvRecoveryTask : IScheduledTask
{
    private readonly VirtualTvScheduleGenerator _generator;
    private readonly IGuideManager _guideManager;
    private readonly VirtualTvVisibilityManager _visibility;
    private readonly ILogger<VirtualTvRecoveryTask> _logger;

    public VirtualTvRecoveryTask(
        VirtualTvScheduleGenerator generator,
        IGuideManager guideManager,
        VirtualTvVisibilityManager visibility,
        ILogger<VirtualTvRecoveryTask> logger)
    {
        _generator = generator;
        _guideManager = guideManager;
        _visibility = visibility;
        _logger = logger;
    }

    public string Name => "Virtual TV — Startup Recovery";
    public string Key => "VirtualTvStartupRecovery";
    public string Description => "Validates channel schedules after Jellyfin starts, repairs downtime gaps and restores the normal future horizon.";
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

            try
            {
                _generator.Recover(channels[index]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Virtual TV startup recovery failed for channel {ChannelName}.", channels[index].Name);
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
            Type = TaskTriggerInfoType.StartupTrigger
        };
    }
}
