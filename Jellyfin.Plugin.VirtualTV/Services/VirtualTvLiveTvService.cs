using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Native Jellyfin Live TV service used by Virtual TV.
/// The current implementation exposes a temporary architecture-proof channel.
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
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<VirtualTvLiveTvService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualTvLiveTvService"/> class.
    /// </summary>
    public VirtualTvLiveTvService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IApplicationPaths applicationPaths,
        ILogger<VirtualTvLiveTvService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _applicationPaths = applicationPaths;
        _logger = logger;
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

        // The architecture-test programme start is persisted when Prepare is pressed so the
        // programme time, source offset and Guide stay anchored to the same wall-clock instant.
        var programStart = GetArchitectureProgramStartUtc();
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

        var source = GetSource(channelId, null, openForPlayback: false);
        return Task.FromResult(new List<MediaSourceInfo> { source });
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(
        string channelId,
        string streamId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetSource(channelId, streamId, openForPlayback: true));
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

    private DateTime GetArchitectureProgramStartUtc()
    {
        var raw = Plugin.Instance?.Configuration.ArchitectureLiveTvTestProgramStartUtc;
        if (!string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return DateTime.UtcNow.AddMinutes(-10);
    }

    private MediaSourceInfo GetSource(string channelId, string? streamId, bool openForPlayback)
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

        // The pre-open source is only a description. Jellyfin must open it before playback so
        // one stable source anchor can be kept for the lifetime of that Live TV session.
        source.RequiresOpening = !openForPlayback;
        source.RequiresClosing = false;
        source.Name = item.Name;

        // Force the architecture proof through Jellyfin's own HLS transcode/remux path.
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        source.SupportsTranscoding = true;

        if (openForPlayback)
        {
            // v1.0.12 proof: create the wall-clock rebase only when Jellyfin actually opens the
            // Live TV stream, while preserving the underlying Jellyfin media-source id.
            //
            // Jellyfin's client-side PGS/VobSub renderer fetches bitmap subtitles through
            // /Videos/{itemId}/{mediaSourceId}/Subtitles/.... The subtitle encoder resolves that
            // mediaSourceId against the item's normal playback sources, so replacing it with a
            // Virtual TV-only id makes the subtitle request fail. Keeping the original id lets
            // Jellyfin resolve and extract the source subtitle without burning it into the video.
            var sourceRuntimeTicks = source.RunTimeTicks ?? item.RunTimeTicks;
            var rebasedInput = CreateRebasedInput(source, sourceRuntimeTicks);
            source.EncoderPath = rebasedInput.DescriptorPath;
            source.EncoderProtocol = MediaProtocol.File;

            _logger.LogInformation(
                "Virtual TV opened stable architecture source {SourceId} rebased to {OffsetSeconds} seconds for {ItemName}. FFmpeg input: {DescriptorPath}",
                source.Id,
                rebasedInput.Offset.TotalSeconds,
                item.Name,
                rebasedInput.DescriptorPath);
        }

        // Keep the player-side stream timeline open-ended. The Guide programme keeps the real
        // programme StartDate/EndDate; player position zero represents the instant this session tuned in.
        source.RunTimeTicks = null;

        return source;
    }

    private (string DescriptorPath, TimeSpan Offset) CreateRebasedInput(
        MediaSourceInfo source,
        long? sourceRuntimeTicks)
    {
        if (source.Protocol != MediaProtocol.File || string.IsNullOrWhiteSpace(source.Path))
        {
            throw new NotSupportedException(
                "The Virtual TV source-rebase architecture proof currently requires a file-backed Jellyfin media source.");
        }

        var now = DateTime.UtcNow;
        var programStart = GetArchitectureProgramStartUtc();
        var offset = now > programStart ? now - programStart : TimeSpan.Zero;

        if (sourceRuntimeTicks.HasValue && sourceRuntimeTicks.Value > 0)
        {
            var maximumOffsetTicks = Math.Max(0, sourceRuntimeTicks.Value - TimeSpan.FromSeconds(1).Ticks);
            if (offset.Ticks > maximumOffsetTicks)
            {
                offset = TimeSpan.FromTicks(maximumOffsetTicks);
            }
        }

        var runtimeRoot = Path.Combine(_applicationPaths.CachePath, "virtualtv", "architecture-live");
        Directory.CreateDirectory(runtimeRoot);
        CleanupOldRuntimeDirectories(runtimeRoot);

        var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var requestDirectory = Path.Combine(runtimeRoot, token);
        Directory.CreateDirectory(requestDirectory);

        var extension = Path.GetExtension(source.Path);
        if (string.IsNullOrWhiteSpace(extension)
            || extension.Length > 12
            || extension.Any(ch => ch != '.' && !char.IsLetterOrDigit(ch)))
        {
            extension = ".media";
        }

        var linkedSourceName = "source" + extension.ToLowerInvariant();
        var linkedSourcePath = Path.Combine(requestDirectory, linkedSourceName);
        File.CreateSymbolicLink(linkedSourcePath, source.Path);

        var descriptorPath = Path.Combine(requestDirectory, "source.ffconcat");
        var descriptor =
            "ffconcat version 1.0\n"
            + "file '" + linkedSourceName + "'\n"
            + "inpoint " + offset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "\n";

        File.WriteAllText(descriptorPath, descriptor, new UTF8Encoding(false));

        return (descriptorPath, offset);
    }

    private static void CleanupOldRuntimeDirectories(string runtimeRoot)
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);

        foreach (var directory in Directory.EnumerateDirectories(runtimeRoot))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(directory) < cutoff)
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (IOException)
            {
                // A currently active ffmpeg process may still have the source open.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best-effort and must never block channel playback.
            }
        }
    }
}
