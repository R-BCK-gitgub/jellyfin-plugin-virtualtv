using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

public sealed class VirtualTvLiveTvService : ILiveTvService
{
    public const string ServiceName = "Virtual TV";
    private const string ChannelPrefix = "virtualtv-";

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly VirtualTvContentCatalog _catalog;
    private readonly VirtualTvRuntimeFallbackResolver _runtimeFallback;
    private readonly ILogger<VirtualTvLiveTvService> _logger;

    public VirtualTvLiveTvService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        VirtualTvScheduleStore scheduleStore,
        VirtualTvContentCatalog catalog,
        VirtualTvRuntimeFallbackResolver runtimeFallback,
        ILogger<VirtualTvLiveTvService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
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
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        source.SupportsTranscoding = true;

        if (openForPlayback)
        {
            _logger.LogInformation(
                "Virtual TV opened full-timeline source {SourceId} for channel {ChannelName}, item {ItemName}.",
                source.Id, channel.Name, item.Name);
        }

        source.RunTimeTicks = null;
        return source;
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
            && DateTime.TryParse(entry.PremiereDateUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
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
