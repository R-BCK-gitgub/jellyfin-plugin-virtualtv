using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

public sealed class VirtualTvLiveTvService : ILiveTvService, ISupportsDirectStreamProvider, ISupportsNewTimerIds
{
    public const string ServiceName = "Virtual TV";
    private const string ChannelPrefix = "virtualtv-";
    private const string ProgramPrefix = "virtualtv-program-";

    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly VirtualTvContentCatalog _catalog;
    private readonly VirtualTvBootstrapMediaProvider _bootstrapMedia;
    private readonly VirtualTvStandardStreamService _standardTv;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<VirtualTvLiveTvService> _logger;

    public VirtualTvLiveTvService(
        VirtualTvScheduleStore scheduleStore,
        VirtualTvContentCatalog catalog,
        VirtualTvBootstrapMediaProvider bootstrapMedia,
        VirtualTvStandardStreamService standardTv,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        IAuthorizationContext authorizationContext,
        IHttpContextAccessor httpContextAccessor,
        ILogger<VirtualTvLiveTvService> logger)
    {
        _scheduleStore = scheduleStore;
        _catalog = catalog;
        _bootstrapMedia = bootstrapMedia;
        _standardTv = standardTv;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _authorizationContext = authorizationContext;
        _httpContextAccessor = httpContextAccessor;
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

    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channel = GetChannel(channelId)
            ?? throw new KeyNotFoundException($"Unknown Virtual TV channel '{channelId}'.");

        if (await IsHiddenPersonalizedAndroidTvRequestAsync(channel).ConfigureAwait(false))
        {
            throw new NotSupportedException(
                $"Personalized TV channel '{channel.Name}' is hidden from Jellyfin for Android TV.");
        }

        var source = VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience)
            ? _standardTv.CreateMenuSource(channel)
            : GetPersonalizedBootstrapSource(channel);

        return new List<MediaSourceInfo> { source };
    }

    public async Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = streamId;

        var channel = GetChannel(channelId)
            ?? throw new KeyNotFoundException($"Unknown Virtual TV channel '{channelId}'.");

        if (VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience))
        {
            throw new NotSupportedException("Standard TV uses Jellyfin's direct live-stream provider path.");
        }

        if (await IsHiddenPersonalizedAndroidTvRequestAsync(channel).ConfigureAwait(false))
        {
            throw new NotSupportedException(
                $"Personalized TV channel '{channel.Name}' is hidden from Jellyfin for Android TV.");
        }

        return GetPersonalizedBootstrapSource(channel);
    }

    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
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
            throw new NotSupportedException(
                "Personalized TV uses the already-materialized neutral bootstrap source.");
        }

        var androidMobileHlsCompatibility = await IsAndroidMobileRequestAsync().ConfigureAwait(false);

        // Android's Integrated Player treats HTTP Direct Play as HLS. Virtual TV's Standard
        // source is a continuous raw MPEG-TS stream, so Android mobile must be kept on Jellyfin's
        // HLS-compatible playback path. Keep a separate sharing key so a Web/webOS/Android TV
        // tune-in can never donate incompatible MediaSource flags to Android mobile, or vice versa.
        var sharingStreamId = string.IsNullOrWhiteSpace(streamId)
            ? string.Empty
            : androidMobileHlsCompatibility
                ? streamId + "|android-mobile-hls"
                : streamId;

        if (!string.IsNullOrWhiteSpace(sharingStreamId))
        {
            var existing = currentLiveStreams.FirstOrDefault(stream =>
                stream.EnableStreamSharing
                && string.Equals(stream.OriginalStreamId, sharingStreamId, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.ConsumerCount++;
                _logger.LogInformation(
                    "Virtual TV reusing Standard TV live stream {StreamId} for channel {ChannelName}; Android mobile HLS compatibility {AndroidMobileHlsCompatibility}; consumer count {ConsumerCount}.",
                    streamId,
                    channel.Name,
                    androidMobileHlsCompatibility,
                    existing.ConsumerCount);
                return existing;
            }
        }

        var created = _standardTv.CreateLiveStream(channel, androidMobileHlsCompatibility);
        created.OriginalStreamId = sharingStreamId;

        _logger.LogInformation(
            "Virtual TV opening one new Standard TV live stream for channel {ChannelName}, source {StreamId}; Android mobile HLS compatibility {AndroidMobileHlsCompatibility}.",
            channel.Name,
            streamId,
            androidMobileHlsCompatibility);

        return created;
    }

    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => PlayFromBeginningFromRecordActionAsync(
            info.ChannelId,
            info.ProgramId,
            cancellationToken,
            "Record / Just this once");

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => PlayFromBeginningFromRecordActionAsync(
            info.ChannelId,
            info.ProgramId,
            cancellationToken,
            "Record series");

    public async Task<string> CreateTimer(TimerInfo info, CancellationToken cancellationToken)
    {
        await PlayFromBeginningFromRecordActionAsync(
            info.ChannelId,
            info.ProgramId,
            cancellationToken,
            "Record / Just this once").ConfigureAwait(false);

        return CreateSyntheticRecordActionId("program");
    }

    public async Task<string> CreateSeriesTimer(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        await PlayFromBeginningFromRecordActionAsync(
            info.ChannelId,
            info.ProgramId,
            cancellationToken,
            "Record series").ConfigureAwait(false);

        return CreateSyntheticRecordActionId("series");
    }

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

    /// <summary>
    /// Standard TV already owns the content, so its native Jellyfin Record action is repurposed
    /// as a zero-UI shortcut to open the currently airing library item from the beginning.
    /// Both the single-program and Android TV "Record series" paths arrive here.
    /// ISupportsNewTimerIds returns a short-lived synthetic id so native clients receive a valid
    /// TimerCreated/SeriesTimerCreated event, but no DVR timer is stored and Personalized TV is never affected.
    /// </summary>
    private async Task PlayFromBeginningFromRecordActionAsync(
        string? channelId,
        string? programId,
        CancellationToken cancellationToken,
        string clientAction)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new InvalidOperationException("Virtual TV Record shortcut did not include a channel id.");
        }

        var channel = GetChannel(channelId)
            ?? throw new KeyNotFoundException($"Unknown Virtual TV channel '{channelId}'.");

        if (!VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience))
        {
            throw new NotSupportedException(
                "Virtual TV only repurposes Record as Play from Beginning on Standard TV channels.");
        }

        var entry = ResolveRecordActionEntry(channel.Id, programId, DateTime.UtcNow)
            ?? throw new InvalidOperationException(
                $"Virtual TV could not resolve the currently airing programme for channel '{channel.Name}'.");

        if (!Guid.TryParse(entry.SourceItemId, out var sourceItemId))
        {
            throw new InvalidOperationException(
                $"Virtual TV programme '{entry.Id}' does not point to a concrete Jellyfin library item.");
        }

        var sourceItem = _libraryManager.GetItemById(sourceItemId);
        if (sourceItem is null || sourceItem.IsFolder)
        {
            throw new InvalidOperationException(
                $"Virtual TV source item '{sourceItemId}' is no longer a playable Jellyfin item.");
        }

        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "Virtual TV Record shortcut requires the active Jellyfin client request.");

        var auth = await _authorizationContext.GetAuthorizationInfo(httpContext).ConfigureAwait(false);
        if (!auth.IsAuthenticated || string.IsNullOrWhiteSpace(auth.DeviceId))
        {
            throw new InvalidOperationException(
                "Virtual TV could not identify the Jellyfin device that invoked Record.");
        }

        var candidateSessions = _sessionManager.Sessions
            .Where(session => string.Equals(session.DeviceId, auth.DeviceId, StringComparison.OrdinalIgnoreCase));

        if (auth.User is not null && auth.User.Id != Guid.Empty)
        {
            candidateSessions = candidateSessions.Where(session => session.UserId == auth.User.Id);
        }

        var targetSession = candidateSessions
            .OrderByDescending(session => session.LastPlaybackCheckIn)
            .ThenByDescending(session => session.LastActivityDate)
            .FirstOrDefault();

        if (targetSession is null || targetSession.SessionControllers.Count == 0)
        {
            throw new InvalidOperationException(
                $"Virtual TV could not find an active controllable Jellyfin session for device '{auth.DeviceId}'.");
        }

        var controllingUserId = auth.User?.Id ?? targetSession.UserId;
        var request = new PlayRequest
        {
            ItemIds = new[] { sourceItemId },
            StartPositionTicks = 0L,
            StartIndex = 0,
            PlayCommand = PlayCommand.PlayNow,
            ControllingUserId = controllingUserId
        };

        _logger.LogInformation(
            "Virtual TV Standard TV Record shortcut: {ClientAction} on channel {ChannelName} opens item {ItemId} from 00:00 in session {SessionId} / device {DeviceId}. No DVR timer is created.",
            clientAction,
            channel.Name,
            sourceItemId,
            targetSession.Id,
            auth.DeviceId);

        // Deliberately use Jellyfin's normal PlayNow path rather than the isolated Personalized TV
        // queue. From this point onward the item behaves exactly like normal library playback,
        // including the user's standard watched/resume/next-episode behaviour.
        await _sessionManager.SendPlayCommand(
            targetSession.Id,
            targetSession.Id,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreateSyntheticRecordActionId(string kind)
        => "virtualtv-playfrombeginning-" + kind + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private VirtualTvScheduleEntry? ResolveRecordActionEntry(
        string channelId,
        string? programId,
        DateTime nowUtc)
    {
        var schedule = _scheduleStore.Load(channelId);

        if (!string.IsNullOrWhiteSpace(programId)
            && programId.StartsWith(ProgramPrefix, StringComparison.OrdinalIgnoreCase)
            && programId.Length > ProgramPrefix.Length)
        {
            var entryId = programId[ProgramPrefix.Length..];
            var requested = schedule.FirstOrDefault(entry =>
                string.Equals(entry.Id, entryId, StringComparison.OrdinalIgnoreCase)
                && IsPlayableAt(entry, nowUtc));

            if (requested is not null)
            {
                return requested;
            }
        }

        // At an exact schedule boundary a client can briefly submit the previous ProgramId.
        // Falling back to the active entry guarantees that Record always opens what is on air now.
        return schedule.FirstOrDefault(entry => IsPlayableAt(entry, nowUtc));
    }

    private static bool IsPlayableAt(VirtualTvScheduleEntry entry, DateTime nowUtc)
        => !entry.IsOffAir
            && !entry.IsContentUnavailable
            && !entry.IsScheduleUnavailable
            && entry.GetStartUtc() <= nowUtc
            && entry.GetEndUtc() > nowUtc
            && !string.IsNullOrWhiteSpace(entry.SourceItemId);

    private async Task<bool> IsHiddenPersonalizedAndroidTvRequestAsync(ChannelConfiguration channel)
    {
        if (!channel.HideFromAndroidTv || VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience))
        {
            return false;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return false;
        }

        var auth = await _authorizationContext.GetAuthorizationInfo(httpContext).ConfigureAwait(false);
        return VirtualTvClientPolicy.IsAndroidTv(auth.Client);
    }

    private async Task<bool> IsAndroidMobileRequestAsync()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return false;
        }

        var auth = await _authorizationContext.GetAuthorizationInfo(httpContext).ConfigureAwait(false);
        return VirtualTvClientPolicy.IsAndroidMobile(auth.Client);
    }

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
