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

        // For the architecture proof, create a realistic currently-airing programme that began
        // ten minutes before the guide refresh. The playback coordinator must derive the live
        // entry offset from these programme timestamps rather than from a hard-coded seek value.
        var now = DateTime.UtcNow;
        var programStart = now.AddMinutes(-10);
        var programDuration = item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : TimeSpan.FromHours(2);
        var programEnd = programStart.Add(programDuration);

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

        // A normal Live TV source is normalized by Jellyfin as an infinite stream. When the
        // underlying library file is direct-played, LG webOS cannot seek that stream and changing
        // subtitles reopens it at the beginning. Force the architecture proof through Jellyfin's
        // transcoding/remux pipeline instead. That pipeline receives StartTimeTicks on every
        // playback-info request, so wall-clock entry, manual seek and track changes can reopen the
        // source at the requested position while the now-playing identity remains the LiveTvChannel.
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        source.SupportsTranscoding = true;

        // Architecture proof v1.0.9: expose the channel media as an open-ended live source
        // instead of a finite VOD source. Jellyfin's HLS pipeline otherwise preserves the
        // absolute file position after a mid-file seek, which makes the Live TV OSD add that
        // file offset to the wall-clock playback start time. Clearing RunTimeTicks keeps the
        // Guide programme duration separate while allowing the emitted live stream timeline
        // to be rebased around the point where the viewer joined the channel.
        source.RunTimeTicks = null;

        return source;
    }
}
