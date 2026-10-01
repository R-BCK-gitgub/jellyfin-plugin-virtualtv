using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

public sealed class VirtualTvLiveTvService : ILiveTvService, ISupportsDirectStreamProvider
{
    public const string ServiceName = "Virtual TV";
    private const string ChannelPrefix = "virtualtv-";

    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly VirtualTvContentCatalog _catalog;
    private readonly VirtualTvBootstrapMediaProvider _bootstrapMedia;
    private readonly VirtualTvStandardStreamService _standardTv;
    private readonly ILogger<VirtualTvLiveTvService> _logger;

    public VirtualTvLiveTvService(
        VirtualTvScheduleStore scheduleStore,
        VirtualTvContentCatalog catalog,
        VirtualTvBootstrapMediaProvider bootstrapMedia,
        VirtualTvStandardStreamService standardTv,
        ILogger<VirtualTvLiveTvService> logger)
    {
        _scheduleStore = scheduleStore;
        _catalog = catalog;
        _bootstrapMedia = bootstrapMedia;
        _standardTv = standardTv;
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

        var channel = GetChannel(channelId)
            ?? throw new KeyNotFoundException($"Unknown Virtual TV channel '{channelId}'.");

        var source = VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience)
            ? _standardTv.CreateMenuSource(channel.Id)
            : GetPersonalizedBootstrapSource(channel);

        return Task.FromResult(new List<MediaSourceInfo> { source });
    }

    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = streamId;

        var channel = GetChannel(channelId)
            ?? throw new KeyNotFoundException($"Unknown Virtual TV channel '{channelId}'.");

        if (VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience))
        {
            return Task.FromException<MediaSourceInfo>(
                new NotSupportedException("Standard TV uses Jellyfin's direct live-stream provider path."));
        }

        return Task.FromResult(GetPersonalizedBootstrapSource(channel));
    }

    public Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
        string channelId,
        string streamId,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channel = GetChannel(channelId)
            ?? throw new KeyNotFoundException($"Unknown Virtual TV channel '{channelId}'.");

        if (!VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience))
        {
            return Task.FromException<ILiveStream>(
                new NotSupportedException("Personalized TV uses the already-materialized neutral bootstrap source."));
        }

        if (!string.IsNullOrWhiteSpace(streamId))
        {
            var existing = currentLiveStreams.FirstOrDefault(stream =>
                stream.EnableStreamSharing
                && string.Equals(stream.OriginalStreamId, streamId, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.ConsumerCount++;
                _logger.LogInformation(
                    "Virtual TV reusing Standard TV live stream {StreamId} for channel {ChannelName}; consumer count {ConsumerCount}.",
                    streamId,
                    channel.Name,
                    existing.ConsumerCount);
                return Task.FromResult(existing);
            }
        }

        var created = _standardTv.CreateLiveStream(channel);
        created.OriginalStreamId = streamId ?? string.Empty;

        _logger.LogInformation(
            "Virtual TV opening one new Standard TV live stream for channel {ChannelName}, source {StreamId}.",
            channel.Name,
            streamId);

        return Task.FromResult(created);
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

    private MediaSourceInfo GetPersonalizedBootstrapSource(ChannelConfiguration channel)
    {
        if (VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience))
        {
            throw new InvalidOperationException("Standard TV must not enter the Personalized TV loading bootstrap.");
        }

        var source = _bootstrapMedia.CreateLoadingSource();

        _logger.LogDebug(
            "Virtual TV supplied neutral Personalized TV bootstrap source for channel {ChannelName}.",
            channel.Name);

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
