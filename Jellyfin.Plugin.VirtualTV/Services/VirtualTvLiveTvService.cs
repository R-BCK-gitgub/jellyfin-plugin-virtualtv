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
    private readonly ILogger<VirtualTvLiveTvService> _logger;

    public VirtualTvLiveTvService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        VirtualTvScheduleStore scheduleStore,
        ILogger<VirtualTvLiveTvService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _scheduleStore = scheduleStore;
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
                Name = channel.Name,
                Number = channel.Number.ToString(CultureInfo.InvariantCulture),
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

        var programs = _scheduleStore.Load(channel.Id)
            .Where(entry => entry.GetEndUtc() > startDateUtc && entry.GetStartUtc() < endDateUtc)
            .Select(entry => ToProgram(channelId, entry))
            .ToArray();

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
        var entry = _scheduleStore.Load(channel.Id)
            .FirstOrDefault(item => !item.IsOffAir && item.GetStartUtc() <= now && item.GetEndUtc() > now)
            ?? throw new InvalidOperationException("No playable Virtual TV programme is scheduled for the current time.");

        if (!Guid.TryParse(entry.SourceItemId, out var itemId))
            throw new InvalidOperationException("The scheduled source item id is invalid.");

        var item = _libraryManager.GetItemById(itemId)
            ?? throw new InvalidOperationException("The scheduled Jellyfin item is no longer available.");

        var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
        if (sources.Count == 0)
            throw new InvalidOperationException("The scheduled Jellyfin item has no playable media source.");

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

        if (entry.IsOffAir)
        {
            return new ProgramInfo
            {
                Id = "virtualtv-offair-" + entry.Id,
                ChannelId = channelId,
                Name = "Off Air",
                Overview = "This Virtual TV channel is currently off air.",
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
            EpisodeTitle = entry.IsMovie ? null : FormatEpisodeTitle(entry),
            Overview = entry.Overview,
            StartDate = start,
            EndDate = end,
            IsLive = false,
            IsMovie = entry.IsMovie,
            IsSeries = !entry.IsMovie,
            SeasonNumber = entry.SeasonNumber,
            EpisodeNumber = entry.EpisodeNumber,
            ProductionYear = entry.ProductionYear,
            OriginalAirDate = premiereDate
        };
    }

    private static string FormatEpisodeTitle(VirtualTvScheduleEntry entry)
    {
        if (entry.SeasonNumber.HasValue && entry.EpisodeNumber.HasValue)
        {
            return $"S{entry.SeasonNumber.Value:00}E{entry.EpisodeNumber.Value:00} {entry.Name}";
        }

        return entry.Name;
    }

    private static string ToExternalId(string channelId) => ChannelPrefix + channelId;

    private static string? FromExternalId(string externalId)
        => externalId.StartsWith(ChannelPrefix, StringComparison.OrdinalIgnoreCase)
            ? externalId[ChannelPrefix.Length..]
            : null;
}
