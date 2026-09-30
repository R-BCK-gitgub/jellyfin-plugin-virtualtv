using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

public sealed class VirtualTvLiveTvService : ILiveTvService
{
    public const string ServiceName = "Virtual TV";
    private const string ChannelPrefix = "virtualtv-";

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly VirtualTvContentCatalog _catalog;
    private readonly VirtualTvRuntimeFallbackResolver _runtimeFallback;
    private readonly ILogger<VirtualTvLiveTvService> _logger;

    public VirtualTvLiveTvService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IApplicationPaths applicationPaths,
        VirtualTvScheduleStore scheduleStore,
        VirtualTvContentCatalog catalog,
        VirtualTvRuntimeFallbackResolver runtimeFallback,
        ILogger<VirtualTvLiveTvService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _applicationPaths = applicationPaths;
        _scheduleStore = scheduleStore;
        _catalog = catalog;
        _runtimeFallback = runtimeFallback;
        _logger = logger;
    }

    public string Name => ServiceName;
    public string HomePageUrl => "https://github.com/R-BCK-gitgub/jellyfin-plugin-virtualtv";

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channels = Plugin.Instance?.Configuration.Channels ?? [];
        var result = channels
            .OrderBy(channel => channel.Number)
            .Select(channel => new ChannelInfo
            {
                Id = ToExternalId(channel.Id),
                Name = GetFrontendChannelName(channel),
                Number = GetFrontendChannelNumber(channel),
                ChannelType = ChannelType.TV,
                CallSign = "VTV" + channel.Number.ToString(CultureInfo.InvariantCulture),
                Tags = ["Virtual TV"]
            })
            .ToArray();

        return Task.FromResult<IEnumerable<ChannelInfo>>(result);
    }

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channel = GetChannel(channelId);
        if (channel is null)
            return Task.FromResult<IEnumerable<ProgramInfo>>(Array.Empty<ProgramInfo>());

        var schedule = _scheduleStore.Load(channel.Id);
        var programs = schedule
            .Where(entry => entry.GetEndUtc() > startDateUtc && entry.GetStartUtc() < endDateUtc)
            .Select(entry => ToProgram(channelId, entry))
            .ToArray();

        if (programs.Length == 0 && schedule.Count == 0)
        {
            var hasContent = string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
                ? _catalog.GetMovies(channel).Count > 0
                : _catalog.GetSeries(channel).Count > 0;

            var status = new VirtualTvScheduleEntry
            {
                Name = hasContent ? "Schedule Not Available" : "Content Not Available",
                Overview = hasContent
                    ? "Schedule needs to be generated."
                    : "This Virtual TV channel currently has no eligible content.",
                IsScheduleUnavailable = hasContent,
                IsContentUnavailable = !hasContent,
                StartUtc = startDateUtc.ToString("O", CultureInfo.InvariantCulture),
                EndUtc = endDateUtc.ToString("O", CultureInfo.InvariantCulture)
            };

            programs = [ToProgram(channelId, status)];
        }

        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new List<MediaSourceInfo> { GetSource(channelId, null, false) });
    }

    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetSource(channelId, streamId, true));
    }

    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV DVR recording is not implemented."));
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV DVR recording is not implemented."));
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV DVR recording is not implemented."));
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("Virtual TV DVR recording is not implemented."));
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<TimerInfo>>(Array.Empty<TimerInfo>());
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<SeriesTimerInfo>>(Array.Empty<SeriesTimerInfo>());
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo());

    private ChannelConfiguration? GetChannel(string externalId)
    {
        var id = FromExternalId(externalId);
        return id is null ? null : Plugin.Instance?.Configuration.Channels.FirstOrDefault(
            channel => string.Equals(channel.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private MediaSourceInfo GetSource(string channelId, string? streamId, bool openForPlayback)
    {
        var channel = GetChannel(channelId)
            ?? throw new KeyNotFoundException($"Unknown Virtual TV channel '{channelId}'.");

        var now = DateTime.UtcNow;
        var schedule = _scheduleStore.Load(channel.Id);
        var entry = schedule
            .FirstOrDefault(item => item.GetStartUtc() <= now && item.GetEndUtc() > now);

        if (entry is null)
        {
            var hasContent = string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
                ? _catalog.GetMovies(channel).Count > 0
                : _catalog.GetSeries(channel).Count > 0;

            throw new InvalidOperationException(hasContent
                ? "Schedule needs to be generated."
                : "Content Not Available.");
        }

        if (entry.IsOffAir)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(entry.Overview)
                ? "This Virtual TV channel is Off Air."
                : entry.Overview);

        if (entry.IsContentUnavailable)
            throw new InvalidOperationException("Content Not Available.");

        if (entry.IsScheduleUnavailable)
            throw new InvalidOperationException("Schedule needs to be generated.");

        Guid? scheduledItemId = Guid.TryParse(entry.SourceItemId, out var parsedItemId)
            ? parsedItemId
            : null;

        var item = scheduledItemId.HasValue
            ? _libraryManager.GetItemById(scheduledItemId.Value)
            : null;

        var sources = item is null
            ? new List<MediaSourceInfo>()
            : _mediaSourceManager.GetStaticMediaSources(item, false);

        if (item is null || sources.Count == 0)
        {
            var fallback = _runtimeFallback.ResolveBootstrapFallback(channel, entry, scheduledItemId);
            if (fallback is null)
            {
                throw new InvalidOperationException("The scheduled content is not available and no runtime fallback is eligible.");
            }

            item = fallback;
            sources = _mediaSourceManager.GetStaticMediaSources(item, false);
            if (sources.Count == 0)
            {
                throw new InvalidOperationException("The fallback content has no playable media source.");
            }

            _logger.LogWarning(
                "Virtual TV used local bootstrap fallback {FallbackItemId} for unavailable scheduled item {ScheduledItemId} on channel {ChannelName}; persisted Guide remains unchanged.",
                item.Id,
                scheduledItemId,
                channel.Name);
        }

        var source = !string.IsNullOrWhiteSpace(streamId)
            ? sources.FirstOrDefault(candidate => string.Equals(candidate.Id, streamId, StringComparison.OrdinalIgnoreCase))
            : null;
        source ??= sources[0];

        source.RequiresOpening = !openForPlayback;
        source.RequiresClosing = false;
        source.Name = item.Name;

        // Keep Virtual TV inside Jellyfin's normal HLS transcode/remux pipeline. The opened
        // source is rebased below so clients see a clean stream timeline that begins at 00:00.
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        source.SupportsTranscoding = true;

        if (openForPlayback)
        {
            try
            {
                var sourceRuntimeTicks = source.RunTimeTicks ?? item.RunTimeTicks;
                var rebasedInput = CreateRebasedInput(source, entry, sourceRuntimeTicks, now);

                // Preserve the original MediaSourceId so Jellyfin subtitle endpoints can still
                // resolve source subtitle tracks. Only the encoder input is replaced.
                source.EncoderPath = rebasedInput.DescriptorPath;
                source.EncoderProtocol = MediaProtocol.File;

                _logger.LogInformation(
                    "Virtual TV opened rebased source {SourceId} for channel {ChannelName}, item {ItemName}: source offset {OffsetSeconds:F3}s is exposed to the client as player 00:00. FFmpeg input: {DescriptorPath}",
                    source.Id,
                    channel.Name,
                    item.Name,
                    rebasedInput.Offset.TotalSeconds,
                    rebasedInput.DescriptorPath);
            }
            catch (NotSupportedException ex)
            {
                // Non-file sources cannot use the ffconcat rebase. Preserve the previous
                // full-timeline fallback rather than making such channels completely unusable.
                _logger.LogWarning(
                    ex,
                    "Virtual TV could not create a rebased file source for channel {ChannelName}, item {ItemName}; using the original source timeline.",
                    channel.Name,
                    item.Name);
            }
        }

        // A Virtual TV tune is a new stream timeline. Guide StartDate/EndDate retain the real
        // schedule clock; player position zero represents the instant the opened source was
        // rebased. This is the critical webOS compatibility behavior.
        source.RunTimeTicks = null;
        return source;
    }

    private (string DescriptorPath, TimeSpan Offset) CreateRebasedInput(
        MediaSourceInfo source,
        VirtualTvScheduleEntry entry,
        long? sourceRuntimeTicks,
        DateTime nowUtc)
    {
        if (source.Protocol != MediaProtocol.File || string.IsNullOrWhiteSpace(source.Path))
        {
            throw new NotSupportedException(
                "Virtual TV source rebasing requires a file-backed Jellyfin media source.");
        }

        var offset = nowUtc > entry.GetStartUtc()
            ? nowUtc - entry.GetStartUtc()
            : TimeSpan.Zero;

        if (sourceRuntimeTicks.HasValue && sourceRuntimeTicks.Value > 0)
        {
            var maximumOffsetTicks = Math.Max(0, sourceRuntimeTicks.Value - TimeSpan.FromSeconds(1).Ticks);
            if (offset.Ticks > maximumOffsetTicks)
            {
                offset = TimeSpan.FromTicks(maximumOffsetTicks);
            }
        }

        var runtimeRoot = Path.Combine(_applicationPaths.CachePath, "virtualtv", "live-rebase");
        Directory.CreateDirectory(runtimeRoot);
        CleanupOldRuntimeDirectories(runtimeRoot);

        var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var requestDirectory = Path.Combine(runtimeRoot, token);
        Directory.CreateDirectory(requestDirectory);

        var extension = Path.GetExtension(source.Path);
        if (string.IsNullOrWhiteSpace(extension)
            || extension.Length > 12
            || extension.Any(character => character != '.' && !char.IsLetterOrDigit(character)))
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
                // An active FFmpeg process may still have the source open.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best-effort and must never block channel playback.
            }
        }
    }

    private static ProgramInfo ToProgram(string channelId, VirtualTvScheduleEntry entry)
    {
        var start = entry.GetStartUtc();
        var end = entry.GetEndUtc();

        if (entry.IsOffAir || entry.IsContentUnavailable || entry.IsScheduleUnavailable)
        {
            var name = entry.IsOffAir
                ? "Off Air"
                : entry.IsContentUnavailable
                    ? "Content Not Available"
                    : "Schedule Not Available";

            return new ProgramInfo
            {
                Id = "virtualtv-status-" + entry.Id,
                ChannelId = channelId,
                Name = name,
                Overview = string.IsNullOrWhiteSpace(entry.Overview) ? name : entry.Overview,
                StartDate = start,
                EndDate = end,
                IsLive = false
            };
        }

        DateTime? premiereDate = null;
        if (!string.IsNullOrWhiteSpace(entry.PremiereDateUtc)
            && DateTime.TryParse(entry.PremiereDateUtc, null, DateTimeStyles.RoundtripKind, out var parsed))
            premiereDate = parsed.ToUniversalTime();

        return new ProgramInfo
        {
            Id = "virtualtv-program-" + entry.Id,
            ChannelId = channelId,
            Name = entry.IsMovie ? entry.Name : entry.SeriesName,
            EpisodeTitle = entry.IsMovie || entry.IsDynamicBlock ? null : entry.Name,
            Overview = entry.Overview,
            StartDate = start,
            EndDate = end,
            IsLive = false,
            IsMovie = entry.IsMovie,
            IsSeries = !entry.IsMovie,
            SeasonNumber = entry.IsDynamicBlock ? null : entry.SeasonNumber,
            EpisodeNumber = entry.IsDynamicBlock ? null : entry.EpisodeNumber,
            ProductionYear = entry.ProductionYear,
            OriginalAirDate = entry.IsDynamicBlock ? null : premiereDate
        };
    }

    private static string GetFrontendChannelNumber(ChannelConfiguration channel)
        => "Channel " + channel.Number.ToString(CultureInfo.InvariantCulture);

    private static string GetFrontendChannelName(ChannelConfiguration channel)
        => "- " + channel.Name;

    private static string ToExternalId(string channelId) => ChannelPrefix + channelId;

    internal static bool TryGetConfigurationChannelId(string? externalId, out string channelId)
    {
        channelId = string.Empty;

        if (string.IsNullOrWhiteSpace(externalId)
            || !externalId.StartsWith(ChannelPrefix, StringComparison.OrdinalIgnoreCase)
            || externalId.Length <= ChannelPrefix.Length)
        {
            return false;
        }

        channelId = externalId[ChannelPrefix.Length..];
        return true;
    }

    private static string? FromExternalId(string externalId)
        => TryGetConfigurationChannelId(externalId, out var channelId)
            ? channelId
            : null;
}
