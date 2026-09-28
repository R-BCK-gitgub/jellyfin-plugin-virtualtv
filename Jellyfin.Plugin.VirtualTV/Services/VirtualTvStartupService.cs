using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Validates persisted Virtual TV schedules when Jellyfin starts.
/// </summary>
public sealed class VirtualTvStartupService : IHostedService
{
    private readonly VirtualTvScheduler _scheduler;
    private readonly IGuideManager _guideManager;
    private readonly ILogger<VirtualTvStartupService> _logger;

    public VirtualTvStartupService(
        VirtualTvScheduler scheduler,
        IGuideManager guideManager,
        ILogger<VirtualTvStartupService> logger)
    {
        _scheduler = scheduler;
        _guideManager = guideManager;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var channels = Plugin.Instance?.Configuration.Channels.ToList() ?? [];
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _scheduler.Ensure(channel, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Virtual TV startup recovery failed for channel {ChannelName}", channel.Name);
            }
        }

        if (channels.Count > 0)
        {
            try
            {
                await _guideManager.RefreshGuide(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Virtual TV startup schedule recovery succeeded but Guide refresh failed.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
