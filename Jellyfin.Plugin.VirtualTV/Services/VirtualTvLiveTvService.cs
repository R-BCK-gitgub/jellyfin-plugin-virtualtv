using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
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
/// Native Jellyfin Live TV service for configured Virtual TV channels.
/// </summary>
public sealed class VirtualTvLiveTvService : ILiveTvService
{
    public const string ServiceName = "Virtual TV";
    public const string ArchitectureTestChannelId = "virtualtv-architecture-test";
    private const string ArchitectureTestProgramId = "virtualtv-architecture-test-program";

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly VirtualTvScheduler _scheduler;
    private readonly ILogger<VirtualTvLiveTvService> _logger;
    private readonly ConcurrentDictionary<string, PlaybackResolution> _pendingOpen = new(StringComparer.OrdinalIgnoreCase);

    public VirtualTvLiveTvService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IApplicationPaths applicationPaths,
        VirtualTvScheduler scheduler,
        ILogger<VirtualTvLiveTvService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _applicationPaths = applicationPaths;
        _scheduler = scheduler;
        _logger = logger;
    }

    public string Name => ServiceName;
    public string HomePageUrl => "https://github.com/R-BCK-gitgub/jellyfin-plugin-virtualtv";

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Task.FromResult<IEnumerable<ChannelInfo>>(Array.Empty<ChannelInfo>());
        }

        var channels = config.Channels
            .OrderBy(c => c.Number)
            .Select(c => new ChannelInfo
            {
                Id = ExternalId(c),
                Name = c.Name,
                Number = c.Number.ToString(CultureInfo.InvariantCulture),
                ChannelType = ChannelType.TV,
                CallSign = "VTV" + c.Number.ToString(CultureInfo.InvariantCulture),
                Tags = ["Virtual TV"]
            })
            .ToList();

        if (config.ArchitectureLiveTvTestEnabled && GetArchitectureSourceItem() is not null)
        {
            channels.Add(new ChannelInfo
            {
                Id = ArchitectureTestChannelId,
                Name = "Virtual TV Architecture Test",
                Number = "9999",
                ChannelType = ChannelType.TV,
                CallSign = "VTVTEST",
                Tags = ["Virtual TV", "Architecture Test"]
            });
        }

        return Task.FromResult<IEnumerable<ChannelInfo>>(channels);
    }

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(channelId, ArchitectureTestChannelId, StringComparison.Ordinal))
        {
            return Task.FromResult(GetArchitecturePrograms(startDateUtc, endDateUtc));
        }

        var channel = FindChannel(channelId);
        if (channel is null)
        {
            return Task.FromResult<IEnumerable<ProgramInfo>>(Array.Empty<ProgramInfo>());
        }

        var entries = _scheduler.GetEntries(channel, startDateUtc, endDateUtc);
        var programs = entries.Select(entry => ToProgramInfo(channel, entry)).ToList();
        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
        string channelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(channelId, ArchitectureTestChannelId, StringComparison.Ordinal))
        {
            var source = GetArchitectureSource(null, openForPlayback: false);
            return Task.FromResult(new List<MediaSourceInfo> { source });
        }

        var channel = FindChannel(channelId)
            ?? throw new KeyNotFoundException("Unknown Virtual TV channel.");

        var resolution = _scheduler.ResolvePlayback(channel, DateTime.UtcNow);
        if (resolution.Item is null)
        {
            throw new InvalidOperationException(string.IsNullOrEmpty(resolution.UserMessage) ? resolution.Status : resolution.UserMessage);
        }

        var source = GetSourceForResolution(channel, resolution, null, openForPlayback: false);
        _pendingOpen[PendingKey(channelId, source.Id)] = resolution;
        return Task.FromResult(new List<MediaSourceInfo> { source });
    }

    public Task<MediaSourceInfo> GetChannelStream(
        string channelId,
        string streamId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(channelId, ArchitectureTestChannelId, StringComparison.Ordinal))
        {
            return Task.FromResult(GetArchitectureSource(streamId, openForPlayback: true));
        }

        var channel = FindChannel(channelId)
            ?? throw new KeyNotFoundException("Unknown Virtual TV channel.");

        var key = PendingKey(channelId, streamId);
        if (!_pendingOpen.TryRemove(key, out var resolution))
        {
            resolution = _scheduler.ResolvePlayback(channel, DateTime.UtcNow);
        }

        if (resolution.Item is null)
        {
            throw new InvalidOperationException(string.IsNullOrEmpty(resolution.UserMessage) ? resolution.Status : resolution.UserMessage);
        }

        return Task.FromResult(GetSourceForResolution(channel, resolution, streamId, openForPlayback: true));
    }

    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV does not provide DVR recording."));

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV does not provide DVR recording."));

    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV does not provide DVR recording."));

    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV does not provide DVR recording."));

    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<TimerInfo>>(Array.Empty<TimerInfo>());

    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<SeriesTimerInfo>>(Array.Empty<SeriesTimerInfo>());

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo());

    private ChannelConfiguration? FindChannel(string externalId)
    {
        if (!externalId.StartsWith("virtualtv-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = externalId["virtualtv-".Length..];
        return Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExternalId(ChannelConfiguration channel) => "virtualtv-" + channel.Id;

    private ProgramInfo ToProgramInfo(ChannelConfiguration channel, ScheduleEntry entry)
    {
        var start = ParseUtc(entry.StartUtc);
        var end = ParseUtc(entry.EndUtc);
        var isSeries = string.Equals(channel.ChannelType, "Series", StringComparison.OrdinalIgnoreCase);
        var isMovie = string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase);

        string name;
        string? episodeTitle = null;
        if (string.Equals(entry.Kind, "OffAir", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Kind, "ContentNotAvailable", StringComparison.OrdinalIgnoreCase))
        {
            name = entry.ItemName;
        }
        else if (string.Equals(entry.Kind, "DynamicSeries", StringComparison.OrdinalIgnoreCase))
        {
            name = entry.SeriesName;
        }
        else if (isSeries)
        {
            var code = entry.SeasonNumber.HasValue && entry.EpisodeNumber.HasValue
                ? $"S{entry.SeasonNumber.Value:00}E{entry.EpisodeNumber.Value:00}"
                : string.Empty;
            name = string.IsNullOrWhiteSpace(code)
                ? entry.SeriesName
                : $"{entry.SeriesName} — {code}";
            episodeTitle = entry.ItemName;
        }
        else
        {
            name = entry.ItemName;
        }

        return new ProgramInfo
        {
            Id = entry.Id,
            ChannelId = ExternalId(channel),
            Name = name,
            EpisodeTitle = episodeTitle,
            Overview = entry.Overview,
            StartDate = start,
            EndDate = end,
            IsLive = start <= DateTime.UtcNow && end > DateTime.UtcNow,
            IsSeries = isSeries,
            IsMovie = isMovie,
            SeriesId = string.IsNullOrWhiteSpace(entry.SeriesId) ? null : entry.SeriesId,
            SeasonNumber = entry.SeasonNumber,
            EpisodeNumber = entry.EpisodeNumber
        };
    }

    private MediaSourceInfo GetSourceForResolution(
        ChannelConfiguration channel,
        PlaybackResolution resolution,
        string? streamId,
        bool openForPlayback)
    {
        var item = resolution.Item
            ?? throw new InvalidOperationException("No playable item was resolved.");

        var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("The resolved Jellyfin item has no playable media source.");
        }

        var source = !string.IsNullOrWhiteSpace(streamId)
            ? sources.FirstOrDefault(i => string.Equals(i.Id, streamId, StringComparison.OrdinalIgnoreCase))
            : null;
        source ??= sources[0];

        source.RequiresOpening = !openForPlayback;
        source.RequiresClosing = false;
        source.Name = item.Name;
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        source.SupportsTranscoding = true;

        if (openForPlayback)
        {
            var offset = ClampOffset(resolution.SourceOffset, source.RunTimeTicks ?? item.RunTimeTicks);
            var rebasedInput = CreateRebasedInput(source, offset);
            source.EncoderPath = rebasedInput;
            source.EncoderProtocol = MediaProtocol.File;

            _logger.LogInformation(
                "Virtual TV opened channel {ChannelName} source {SourceId} at {OffsetSeconds}s for {ItemName}. Input: {DescriptorPath}",
                channel.Name,
                source.Id,
                offset.TotalSeconds,
                item.Name,
                rebasedInput);
        }

        // A zero-based open-ended player timeline keeps Jellyfin's Live TV wall clock aligned.
        source.RunTimeTicks = null;
        return source;
    }

    private BaseItem? GetArchitectureSourceItem()
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

    private IEnumerable<ProgramInfo> GetArchitecturePrograms(DateTime startDateUtc, DateTime endDateUtc)
    {
        var item = GetArchitectureSourceItem();
        if (item is null)
        {
            return Array.Empty<ProgramInfo>();
        }

        var start = GetArchitectureProgramStartUtc();
        var duration = item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : TimeSpan.FromHours(2);
        var end = start.Add(duration);
        if (end <= startDateUtc || start >= endDateUtc)
        {
            return Array.Empty<ProgramInfo>();
        }

        return
        [
            new ProgramInfo
            {
                Id = ArchitectureTestProgramId,
                ChannelId = ArchitectureTestChannelId,
                Name = item.Name,
                Overview = "Temporary Virtual TV Live TV architecture validation.",
                StartDate = start,
                EndDate = end,
                IsLive = true,
                IsMovie = true,
                ProductionYear = item.ProductionYear,
                OriginalAirDate = item.PremiereDate
            }
        ];
    }

    private MediaSourceInfo GetArchitectureSource(string? streamId, bool openForPlayback)
    {
        var item = GetArchitectureSourceItem()
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

        source.RequiresOpening = !openForPlayback;
        source.RequiresClosing = false;
        source.Name = item.Name;
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        source.SupportsTranscoding = true;

        if (openForPlayback)
        {
            var offset = DateTime.UtcNow - GetArchitectureProgramStartUtc();
            offset = ClampOffset(offset < TimeSpan.Zero ? TimeSpan.Zero : offset, source.RunTimeTicks ?? item.RunTimeTicks);
            var path = CreateRebasedInput(source, offset);
            source.EncoderPath = path;
            source.EncoderProtocol = MediaProtocol.File;
            _logger.LogInformation(
                "Virtual TV architecture source {SourceId} rebased to {OffsetSeconds}s for {ItemName}. Input: {DescriptorPath}",
                source.Id,
                offset.TotalSeconds,
                item.Name,
                path);
        }

        source.RunTimeTicks = null;
        return source;
    }

    private DateTime GetArchitectureProgramStartUtc()
    {
        var raw = Plugin.Instance?.Configuration.ArchitectureLiveTvTestProgramStartUtc;
        if (!string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return DateTime.UtcNow.AddMinutes(-10);
    }

    private string CreateRebasedInput(MediaSourceInfo source, TimeSpan offset)
    {
        if (source.Protocol != MediaProtocol.File || string.IsNullOrWhiteSpace(source.Path))
        {
            throw new NotSupportedException("Virtual TV preview currently requires file-backed Jellyfin media.");
        }

        var runtimeRoot = Path.Combine(_applicationPaths.CachePath, "virtualtv", "live");
        Directory.CreateDirectory(runtimeRoot);
        CleanupOldRuntimeDirectories(runtimeRoot);

        var requestDirectory = Path.Combine(runtimeRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(requestDirectory);

        var extension = Path.GetExtension(source.Path);
        if (string.IsNullOrWhiteSpace(extension)
            || extension.Length > 12
            || extension.Any(ch => ch != '.' && !char.IsLetterOrDigit(ch)))
        {
            extension = ".media";
        }

        var linkedSourceName = "source" + extension.ToLowerInvariant();
        File.CreateSymbolicLink(Path.Combine(requestDirectory, linkedSourceName), source.Path);

        var descriptorPath = Path.Combine(requestDirectory, "source.ffconcat");
        var descriptor =
            "ffconcat version 1.0\n"
            + "file '" + linkedSourceName + "'\n"
            + "inpoint " + offset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "\n";

        File.WriteAllText(descriptorPath, descriptor, new UTF8Encoding(false));
        return descriptorPath;
    }

    private static TimeSpan ClampOffset(TimeSpan offset, long? runtimeTicks)
    {
        if (offset < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (runtimeTicks.HasValue && runtimeTicks.Value > 0)
        {
            var maximum = TimeSpan.FromTicks(Math.Max(0, runtimeTicks.Value - TimeSpan.FromSeconds(1).Ticks));
            if (offset > maximum)
            {
                return maximum;
            }
        }

        return offset;
    }

    private static DateTime ParseUtc(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;

    private static string PendingKey(string channelId, string? mediaSourceId)
        => channelId + "|" + (mediaSourceId ?? string.Empty);

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
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
