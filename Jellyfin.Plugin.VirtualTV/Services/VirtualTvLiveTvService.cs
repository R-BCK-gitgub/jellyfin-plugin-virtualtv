using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Native Jellyfin Live TV service used by Virtual TV.
/// The v1.0.4 implementation exposes only a temporary architecture-proof channel.
/// </summary>
public sealed class VirtualTvLiveTvService : ILiveTvService
{
    /// <summary>
    /// Service name stored on Jellyfin Live TV channel entities.
    /// </summary>
    public const string ServiceName = "Virtual TV";

    /// <summary>
    /// External id of the temporary architecture-proof channel.
    /// </summary>
    public const string ArchitectureTestChannelId = "virtualtv-architecture-test";

    private const string ArchitectureTestProgramId = "virtualtv-architecture-test-program";

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualTvLiveTvService"/> class.
    /// </summary>
    public VirtualTvLiveTvService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <inheritdoc />
    public string Name => ServiceName;

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/R-BCK-gitgub/jellyfin-plugin-virtualtv";

    /// <inheritdoc />
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var item = GetConfiguredSourceItem();
        if (item is null)
        {
            return Task.FromResult<IEnumerable<ChannelInfo>>(Array.Empty<ChannelInfo>());
        }

        IEnumerable<ChannelInfo> result =
        [
            new ChannelInfo
            {
                Id = ArchitectureTestChannelId,
                Name = "Virtual TV Architecture Test",
                Number = "9999",
                ChannelType = ChannelType.TV,
                CallSign = "VTVTEST",
                Tags = ["Virtual TV", "Architecture Test"]
            }
        ];

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(channelId, ArchitectureTestChannelId, StringComparison.Ordinal))
        {
            return Task.FromResult<IEnumerable<ProgramInfo>>(Array.Empty<ProgramInfo>());
        }

        var item = GetConfiguredSourceItem();
        if (item is null)
        {
            return Task.FromResult<IEnumerable<ProgramInfo>>(Array.Empty<ProgramInfo>());
        }

        // Keep one stable current programme around the present moment for the architecture proof.
        var now = DateTime.UtcNow;
        var programStart = now.Date.AddDays(-1);
        var programEnd = now.Date.AddDays(2);

        if (programEnd <= startDateUtc || programStart >= endDateUtc)
        {
            return Task.FromResult<IEnumerable<ProgramInfo>>(Array.Empty<ProgramInfo>());
        }

        IEnumerable<ProgramInfo> result =
        [
            new ProgramInfo
            {
                Id = ArchitectureTestProgramId,
                ChannelId = ArchitectureTestChannelId,
                Name = item.Name,
                Overview = "Temporary Virtual TV Live TV architecture validation.",
                StartDate = programStart,
                EndDate = programEnd,
                IsLive = true,
                IsMovie = true,
                ProductionYear = item.ProductionYear,
                OriginalAirDate = item.PremiereDate
            }
        ];

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
        string channelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var source = GetSource(channelId, null);
        return Task.FromResult(new List<MediaSourceInfo> { source });
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(
        string channelId,
        string streamId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetSource(channelId, streamId));
    }

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV does not provide DVR recording in this architecture test."));

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV does not provide DVR recording in this architecture test."));

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV does not provide DVR recording in this architecture test."));

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV does not provide DVR recording in this architecture test."));

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<TimerInfo>>(Array.Empty<TimerInfo>());

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<SeriesTimerInfo>>(Array.Empty<SeriesTimerInfo>());

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(
        CancellationToken cancellationToken,
        ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo());

    private BaseItem? GetConfiguredSourceItem()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null
            || !config.ArchitectureLiveTvTestEnabled
            || !Guid.TryParse(config.ArchitectureLiveTvTestItemId, out var itemId))
        {
            return null;
        }

        return _libraryManager.GetItemById(itemId);
    }

    private MediaSourceInfo GetSource(string channelId, string? streamId)
    {
        if (!string.Equals(channelId, ArchitectureTestChannelId, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException(
                string.Format(CultureInfo.InvariantCulture, "Unknown Virtual TV channel '{0}'.", channelId));
        }

        var item = GetConfiguredSourceItem()
            ?? throw new InvalidOperationException("The Virtual TV architecture test source is not configured.");

        var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("The configured Jellyfin item has no playable media source.");
        }

        var source = !string.IsNullOrWhiteSpace(streamId)
            ? sources.FirstOrDefault(i => string.Equals(i.Id, streamId, StringComparison.OrdinalIgnoreCase))
            : null;

        source ??= sources[0];

        // Jellyfin will treat the now-playing item as the LiveTvChannel, while this media source
        // supplies the actual bytes from the existing library item.
        source.RequiresOpening = false;
        source.RequiresClosing = false;
        source.Name = item.Name;

        return source;
    }
}
