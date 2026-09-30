using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Materializes and maintains Virtual TV schedules. Playback consumes only persisted entries;
/// schedule lifecycle changes therefore do not alter the validated 1.9.1 player paths.
/// </summary>
public sealed class VirtualTvScheduleGenerator
{
    private static readonly TimeSpan FallbackEpisodeDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FallbackMovieDuration = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan FutureHorizon = TimeSpan.FromDays(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly VirtualTvContentCatalog _catalog;
    private readonly VirtualTvUserContextService _userContext;
    private readonly VirtualTvScheduleStore _store;

    public VirtualTvScheduleGenerator(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        VirtualTvContentCatalog catalog,
        VirtualTvUserContextService userContext,
        VirtualTvScheduleStore store)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _catalog = catalog;
        _userContext = userContext;
        _store = store;
    }

    /// <summary>
    /// Generate New Schedule. On an existing channel the past/current block is preserved;
    /// Smart channels preserve the complete current Sunday-to-Saturday week.
    /// </summary>
    public IReadOnlyList<VirtualTvScheduleEntry> Generate(ChannelConfiguration channel)
        => GenerateNewSchedule(channel);

    /// <summary>
    /// Compatibility overload used by maintenance paths.
    /// </summary>
    public IReadOnlyList<VirtualTvScheduleEntry> Generate(
        ChannelConfiguration channel,
        DateTime? explicitStartUtc,
        bool preserveBeforeStart)
    {
        if (!explicitStartUtc.HasValue)
        {
            return GenerateNewSchedule(channel);
        }

        Normalize(channel);

        var nowUtc = DateTime.UtcNow;
        var existing = _store.Load(channel.Id).OrderBy(item => item.GetStartUtc()).ToList();
        var startUtc = explicitStartUtc.Value.ToUniversalTime();
        var prefix = preserveBeforeStart
            ? existing.Where(item => item.GetStartUtc() < startUtc).ToList()
            : new List<VirtualTvScheduleEntry>();

        var targetUtc = GetTargetHorizonUtc(channel, nowUtc);
        if (targetUtc <= startUtc)
        {
            targetUtc = string.Equals(channel.SchedulingMethod, VirtualTvModePolicy.SmartSchedule, StringComparison.OrdinalIgnoreCase)
                ? GetNextSundayUtc(startUtc.AddDays(30))
                : startUtc.Add(FutureHorizon);
        }

        var generated = BuildRange(channel, startUtc, targetUtc, prefix);
        var combined = CombineAndTrim(prefix, generated, nowUtc);
        Save(channel, combined, nowUtc);
        return combined;
    }

    public IReadOnlyList<VirtualTvScheduleEntry> GenerateNewSchedule(ChannelConfiguration channel)
    {
        Normalize(channel);
        EnsureSelectedContent(channel);

        var nowUtc = DateTime.UtcNow;
        var existing = _store.Load(channel.Id).OrderBy(item => item.GetStartUtc()).ToList();
        var isSmart = IsSmart(channel);

        DateTime startUtc;
        List<VirtualTvScheduleEntry> prefix;

        if (existing.Count == 0)
        {
            startUtc = isSmart ? GetSundayStartUtc(nowUtc) : AlignStartToHour(nowUtc);
            prefix = [];
        }
        else
        {
            startUtc = isSmart
                ? GetNextSundayUtc(nowUtc)
                : GetNextMutableBoundary(existing, nowUtc);

            prefix = existing
                .Where(item => item.GetStartUtc() < startUtc)
                .ToList();
        }

        if (string.Equals(channel.SchedulingMethod, VirtualTvModePolicy.RepeatingOrder, StringComparison.OrdinalIgnoreCase))
        {
            channel.RepeatingSeriesOrder.Clear();
        }

        if (isSmart)
        {
            channel.SmartTemplateSeed = Random.Shared.Next(1, int.MaxValue);
            channel.SmartTemplateCreatedUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        }

        var targetUtc = GetTargetHorizonUtc(channel, nowUtc);
        var generated = BuildRange(channel, startUtc, targetUtc, prefix);
        var combined = CombineAndTrim(prefix, generated, nowUtc);
        Save(channel, combined, nowUtc);
        return combined;
    }

    /// <summary>
    /// Append-only normal maintenance. Keeps seven days of history and tops future coverage up
    /// to at least 30 days; Smart channels end on a Saturday boundary.
    /// </summary>
    public IReadOnlyList<VirtualTvScheduleEntry> Extend(ChannelConfiguration channel)
    {
        Normalize(channel);
        var nowUtc = DateTime.UtcNow;
        var existing = _store.Load(channel.Id).OrderBy(item => item.GetStartUtc()).ToList();

        if (existing.Count == 0)
        {
            return GenerateNewSchedule(channel);
        }

        var retained = existing
            .Where(item => item.GetEndUtc() > nowUtc.Subtract(HistoryRetention))
            .ToList();

        var targetUtc = GetTargetHorizonUtc(channel, nowUtc);
        var lastEndUtc = retained.Count > 0 ? retained[^1].GetEndUtc() : DateTime.MinValue;

        if (lastEndUtc < nowUtc)
        {
            // Downtime/gap recovery: reconstruct continuously from the last persisted boundary.
            var recoveryStart = lastEndUtc == DateTime.MinValue
                ? (IsSmart(channel) ? GetSundayStartUtc(nowUtc) : AlignStartToHour(nowUtc))
                : lastEndUtc;

            var recovery = BuildRange(channel, recoveryStart, targetUtc, retained);
            var repaired = CombineAndTrim(retained, recovery, nowUtc);
            Save(channel, repaired, nowUtc);
            return repaired;
        }

        if (lastEndUtc >= targetUtc)
        {
            Save(channel, retained, nowUtc);
            return retained;
        }

        var append = BuildRange(channel, lastEndUtc, targetUtc, retained);
        var combined = CombineAndTrim(retained, append, nowUtc);
        Save(channel, combined, nowUtc);
        return combined;
    }

    /// <summary>
    /// Applies changed content/configuration without modifying history or the current block.
    /// Smart channels defer changes until the next Sunday.
    /// </summary>
    public IReadOnlyList<VirtualTvScheduleEntry> Reconcile(ChannelConfiguration channel)
    {
        Normalize(channel);
        var nowUtc = DateTime.UtcNow;
        var existing = _store.Load(channel.Id).OrderBy(item => item.GetStartUtc()).ToList();

        if (existing.Count == 0)
        {
            return GenerateNewSchedule(channel);
        }

        var cutoffUtc = IsSmart(channel)
            ? GetNextSundayUtc(nowUtc)
            : GetNextMutableBoundary(existing, nowUtc);

        var prefix = existing
            .Where(item =>
                item.GetEndUtc() > nowUtc.Subtract(HistoryRetention)
                && item.GetStartUtc() < cutoffUtc)
            .ToList();

        // Repeating Order changes only when the eligible series set changes or Generate New
        // explicitly cleared the order.
        EnsureRepeatingOrder(channel, _catalog.GetSeries(channel).Select(item => item.Id).ToArray());

        var targetUtc = GetTargetHorizonUtc(channel, nowUtc);
        var generated = BuildRange(channel, cutoffUtc, targetUtc, prefix);
        var combined = CombineAndTrim(prefix, generated, nowUtc);
        Save(channel, combined, nowUtc);
        return combined;
    }

    /// <summary>
    /// Startup recovery is intentionally idempotent: Extend fills missing wall-clock time and
    /// restores the normal future horizon without touching already materialized decisions.
    /// </summary>
    public IReadOnlyList<VirtualTvScheduleEntry> Recover(ChannelConfiguration channel)
        => Extend(channel);

    private IReadOnlyList<VirtualTvScheduleEntry> BuildRange(
        ChannelConfiguration channel,
        DateTime startUtc,
        DateTime endUtc,
        IReadOnlyList<VirtualTvScheduleEntry> prefix)
    {
        if (endUtc <= startUtc)
        {
            return [];
        }

        if (string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            var movies = _catalog.GetMovies(channel);
            if (movies.Count == 0)
            {
                return [CreateUnavailableEntry(startUtc, endUtc)];
            }

            return string.Equals(channel.ContentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase)
                ? BuildDynamicMovieSchedule(channel, movies, startUtc, endUtc, prefix)
                : BuildMovieShuffleSchedule(channel, movies, startUtc, endUtc, prefix);
        }

        var series = _catalog.GetSeries(channel);
        if (series.Count == 0)
        {
            return [CreateUnavailableEntry(startUtc, endUtc)];
        }

        EnsureRepeatingOrder(channel, series.Select(item => item.Id).ToArray());

        return VirtualTvModePolicy.IsDynamicUnwatched(channel.ContentMode)
            ? BuildDynamicSeriesSchedule(channel, series, startUtc, endUtc, prefix)
            : BuildConcreteSeriesSchedule(channel, series, startUtc, endUtc, prefix);
    }

    private IReadOnlyList<VirtualTvScheduleEntry> BuildDynamicSeriesSchedule(
        ChannelConfiguration channel,
        IReadOnlyList<VirtualTvContentCatalog.SeriesContent> series,
        DateTime startUtc,
        DateTime endUtc,
        IReadOnlyList<VirtualTvScheduleEntry> prefix)
    {
        var entries = new List<VirtualTvScheduleEntry>();
        var cursor = startUtc;
        var blockDuration = TimeSpan.FromMinutes(channel.BlockMinutes);
        var picker = new SeriesPicker(channel, series, prefix, this);
        var currentSeries = default(VirtualTvContentCatalog.SeriesContent);
        var remainingTurns = 0;

        while (cursor < endUtc)
        {
            if (TryAppendOffAir(channel, entries, ref cursor, endUtc))
            {
                continue;
            }

            if (currentSeries is null || remainingTurns <= 0)
            {
                currentSeries = picker.Next(cursor);
                remainingTurns = channel.EpisodesPerTurn;
            }

            var blockEnd = cursor.Add(blockDuration);
            var offAirBoundary = GetNextOffAirBoundaryUtc(channel, cursor);
            if (offAirBoundary.HasValue && offAirBoundary.Value > cursor && blockEnd > offAirBoundary.Value)
            {
                blockEnd = offAirBoundary.Value;
            }

            if (blockEnd > endUtc)
            {
                blockEnd = endUtc;
            }

            var bootstrap = currentSeries.Episodes[0];
            entries.Add(new VirtualTvScheduleEntry
            {
                SourceItemId = bootstrap.Id.ToString("N"),
                SourceSeriesId = currentSeries.Id.ToString("N"),
                PlaybackMode = channel.ContentMode,
                IsDynamicBlock = true,
                DynamicKind = "Series",
                BlockMinutes = channel.BlockMinutes,
                Name = currentSeries.Name,
                SeriesName = currentSeries.Name,
                Overview = currentSeries.Overview,
                ProductionYear = currentSeries.ProductionYear,
                StartUtc = cursor.ToString("O", CultureInfo.InvariantCulture),
                EndUtc = blockEnd.ToString("O", CultureInfo.InvariantCulture)
            });

            remainingTurns--;
            cursor = blockEnd;
        }

        return entries;
    }

    private IReadOnlyList<VirtualTvScheduleEntry> BuildConcreteSeriesSchedule(
        ChannelConfiguration channel,
        IReadOnlyList<VirtualTvContentCatalog.SeriesContent> series,
        DateTime startUtc,
        DateTime endUtc,
        IReadOnlyList<VirtualTvScheduleEntry> prefix)
    {
        var entries = new List<VirtualTvScheduleEntry>();
        var cursor = startUtc;
        var picker = new SeriesPicker(channel, series, prefix, this);
        var sequentialCursors = BuildSequentialCursors(series, prefix);
        var randomBags = series.ToDictionary(
            item => item.Id,
            item => BuildEpisodeBag(item, prefix));

        while (cursor < endUtc)
        {
            if (TryAppendOffAir(channel, entries, ref cursor, endUtc))
            {
                continue;
            }

            var selectedSeries = picker.Next(cursor);

            for (var turn = 0; turn < channel.EpisodesPerTurn && cursor < endUtc; turn++)
            {
                if (!channel.Is24Hours && !IsOnAirAt(channel, cursor))
                {
                    break;
                }

                Episode episode;
                if (string.Equals(channel.ContentMode, VirtualTvModePolicy.Random, StringComparison.OrdinalIgnoreCase))
                {
                    episode = randomBags[selectedSeries.Id].Next();
                }
                else
                {
                    var index = sequentialCursors[selectedSeries.Id] % selectedSeries.Episodes.Count;
                    episode = selectedSeries.Episodes[index];
                    sequentialCursors[selectedSeries.Id] = (index + 1) % selectedSeries.Episodes.Count;
                }

                var duration = GetDuration(episode, FallbackEpisodeDuration);
                var itemEnd = cursor.Add(duration);
                entries.Add(CreateConcreteEntry(
                    episode,
                    selectedSeries.Id,
                    selectedSeries.Name,
                    cursor,
                    itemEnd,
                    channel.ContentMode,
                    isMovie: false));

                cursor = itemEnd;
            }
        }

        return entries;
    }

    private IReadOnlyList<VirtualTvScheduleEntry> BuildMovieShuffleSchedule(
        ChannelConfiguration channel,
        IReadOnlyList<BaseItem> movies,
        DateTime startUtc,
        DateTime endUtc,
        IReadOnlyList<VirtualTvScheduleEntry> prefix)
    {
        var entries = new List<VirtualTvScheduleEntry>();
        var cursor = startUtc;
        var bag = BuildMovieBag(movies, prefix);

        while (cursor < endUtc)
        {
            if (TryAppendOffAir(channel, entries, ref cursor, endUtc))
            {
                continue;
            }

            var movie = bag.Next();
            var itemEnd = cursor.Add(GetDuration(movie, FallbackMovieDuration));
            entries.Add(CreateConcreteEntry(
                movie,
                Guid.Empty,
                string.Empty,
                cursor,
                itemEnd,
                VirtualTvModePolicy.Random,
                isMovie: true));

            cursor = itemEnd;
        }

        return entries;
    }

    private IReadOnlyList<VirtualTvScheduleEntry> BuildDynamicMovieSchedule(
        ChannelConfiguration channel,
        IReadOnlyList<BaseItem> movies,
        DateTime startUtc,
        DateTime endUtc,
        IReadOnlyList<VirtualTvScheduleEntry> prefix)
    {
        var ownerId = _userContext.ResolveOwnerUserId(channel);
        var owner = ownerId == Guid.Empty ? null : _userManager.GetUserById(ownerId);

        var unwatched = owner is null
            ? movies.ToList()
            : movies.Where(item => _userDataManager.GetUserData(owner, item)?.Played != true).ToList();

        var pool = unwatched.Count > 0 ? unwatched : movies.ToList();
        var bag = new ShuffleBag<BaseItem>(
            pool,
            item => item.Id,
            GetLastConcreteItemId(prefix),
            GetRecentlyUsedIds(prefix, pool.Select(item => item.Id).ToHashSet()));

        var entries = new List<VirtualTvScheduleEntry>();
        var cursor = startUtc;
        var blockDuration = TimeSpan.FromMinutes(channel.BlockMinutes);

        while (cursor < endUtc)
        {
            if (TryAppendOffAir(channel, entries, ref cursor, endUtc))
            {
                continue;
            }

            var movie = bag.Next();
            var blockEnd = cursor.Add(blockDuration);
            var offAirBoundary = GetNextOffAirBoundaryUtc(channel, cursor);
            if (offAirBoundary.HasValue && offAirBoundary.Value > cursor && blockEnd > offAirBoundary.Value)
            {
                blockEnd = offAirBoundary.Value;
            }

            if (blockEnd > endUtc)
            {
                blockEnd = endUtc;
            }

            entries.Add(new VirtualTvScheduleEntry
            {
                SourceItemId = movie.Id.ToString("N"),
                PlaybackMode = VirtualTvModePolicy.RandomUnwatched,
                IsDynamicBlock = true,
                DynamicKind = "Movie",
                BlockMinutes = channel.BlockMinutes,
                Name = movie.Name ?? string.Empty,
                Overview = movie.Overview ?? string.Empty,
                ProductionYear = movie.ProductionYear,
                PremiereDateUtc = movie.PremiereDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                IsMovie = true,
                StartUtc = cursor.ToString("O", CultureInfo.InvariantCulture),
                EndUtc = blockEnd.ToString("O", CultureInfo.InvariantCulture)
            });

            cursor = blockEnd;
        }

        return entries;
    }

    private Dictionary<Guid, int> BuildSequentialCursors(
        IReadOnlyList<VirtualTvContentCatalog.SeriesContent> series,
        IReadOnlyList<VirtualTvScheduleEntry> prefix)
    {
        var result = series.ToDictionary(item => item.Id, _ => 0);

        foreach (var item in series)
        {
            var last = prefix
                .Where(entry => !entry.IsDynamicBlock && !entry.IsMovie && GetEntrySeriesId(entry) == item.Id)
                .LastOrDefault();

            if (last is null || !Guid.TryParse(last.SourceItemId, out var lastId))
            {
                continue;
            }

            var index = item.Episodes.ToList().FindIndex(episode => episode.Id == lastId);
            if (index >= 0)
            {
                result[item.Id] = (index + 1) % item.Episodes.Count;
            }
        }

        return result;
    }

    private ShuffleBag<Episode> BuildEpisodeBag(
        VirtualTvContentCatalog.SeriesContent series,
        IReadOnlyList<VirtualTvScheduleEntry> prefix)
    {
        var eligible = series.Episodes.ToList();
        var ids = eligible.Select(item => item.Id).ToHashSet();
        var used = GetRecentlyUsedIds(
            prefix.Where(entry => GetEntrySeriesId(entry) == series.Id).ToList(),
            ids);

        var lastId = prefix
            .Where(entry => GetEntrySeriesId(entry) == series.Id)
            .Select(entry => Guid.TryParse(entry.SourceItemId, out var id) ? id : Guid.Empty)
            .LastOrDefault(id => id != Guid.Empty);

        return new ShuffleBag<Episode>(
            eligible,
            item => item.Id,
            lastId == Guid.Empty ? null : lastId,
            used);
    }

    private ShuffleBag<BaseItem> BuildMovieBag(
        IReadOnlyList<BaseItem> movies,
        IReadOnlyList<VirtualTvScheduleEntry> prefix)
    {
        var ids = movies.Select(item => item.Id).ToHashSet();
        return new ShuffleBag<BaseItem>(
            movies,
            item => item.Id,
            GetLastConcreteItemId(prefix),
            GetRecentlyUsedIds(prefix, ids));
    }

    private static HashSet<Guid> GetRecentlyUsedIds(
        IReadOnlyList<VirtualTvScheduleEntry> entries,
        HashSet<Guid> eligibleIds)
    {
        var used = new HashSet<Guid>();

        for (var index = entries.Count - 1; index >= 0 && used.Count < eligibleIds.Count; index--)
        {
            if (!Guid.TryParse(entries[index].SourceItemId, out var id) || !eligibleIds.Contains(id))
            {
                continue;
            }

            if (!used.Add(id))
            {
                break;
            }
        }

        return used;
    }

    private static Guid? GetLastConcreteItemId(IReadOnlyList<VirtualTvScheduleEntry> prefix)
    {
        for (var index = prefix.Count - 1; index >= 0; index--)
        {
            if (Guid.TryParse(prefix[index].SourceItemId, out var id))
            {
                return id;
            }
        }

        return null;
    }

    private Guid GetEntrySeriesId(VirtualTvScheduleEntry entry)
    {
        if (Guid.TryParse(entry.SourceSeriesId, out var configured))
        {
            return configured;
        }

        if (!Guid.TryParse(entry.SourceItemId, out var itemId))
        {
            return Guid.Empty;
        }

        return _libraryManager.GetItemById(itemId) is Episode episode
            ? episode.SeriesId
            : Guid.Empty;
    }

    private void EnsureRepeatingOrder(ChannelConfiguration channel, IReadOnlyList<Guid> eligibleSeriesIds)
    {
        if (!string.Equals(channel.SchedulingMethod, VirtualTvModePolicy.RepeatingOrder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var eligible = eligibleSeriesIds.Select(id => id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = channel.RepeatingSeriesOrder
            .Where(id => eligible.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = eligible.Where(id => !current.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0 && current.Count == eligible.Count)
        {
            channel.RepeatingSeriesOrder = current;
            return;
        }

        var all = eligible.ToList();
        Shuffle(all);
        channel.RepeatingSeriesOrder = all;
    }

    private static VirtualTvScheduleEntry CreateConcreteEntry(
        BaseItem item,
        Guid seriesId,
        string seriesName,
        DateTime startUtc,
        DateTime endUtc,
        string playbackMode,
        bool isMovie)
    {
        return new VirtualTvScheduleEntry
        {
            SourceItemId = item.Id.ToString("N"),
            SourceSeriesId = seriesId == Guid.Empty ? string.Empty : seriesId.ToString("N"),
            PlaybackMode = playbackMode,
            Name = item.Name ?? string.Empty,
            SeriesName = seriesName,
            Overview = item.Overview ?? string.Empty,
            SeasonNumber = item.ParentIndexNumber,
            EpisodeNumber = item.IndexNumber,
            ProductionYear = item.ProductionYear,
            PremiereDateUtc = item.PremiereDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            IsMovie = isMovie,
            StartUtc = startUtc.ToString("O", CultureInfo.InvariantCulture),
            EndUtc = endUtc.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private static VirtualTvScheduleEntry CreateUnavailableEntry(DateTime startUtc, DateTime endUtc)
        => new()
        {
            Name = "Content Not Available",
            Overview = "This Virtual TV channel currently has no eligible content.",
            IsContentUnavailable = true,
            StartUtc = startUtc.ToString("O", CultureInfo.InvariantCulture),
            EndUtc = endUtc.ToString("O", CultureInfo.InvariantCulture)
        };

    private static TimeSpan GetDuration(BaseItem item, TimeSpan fallback)
        => item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : fallback;

    private void Normalize(ChannelConfiguration channel)
    {
        if (string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            channel.ContentMode = string.Equals(channel.ContentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase)
                ? VirtualTvModePolicy.RandomUnwatched
                : VirtualTvModePolicy.Random;
            channel.SchedulingMethod = string.Empty;
        }
        else
        {
            channel.ContentMode = VirtualTvModePolicy.NormalizeContentMode(channel.ContentMode);
            channel.SchedulingMethod = VirtualTvModePolicy.NormalizeSchedulingMethod(channel.SchedulingMethod);
        }

        channel.BlockMinutes = VirtualTvModePolicy.NormalizeBlockMinutes(channel.BlockMinutes);
        channel.EpisodesPerTurn = VirtualTvModePolicy.NormalizeEpisodesPerTurn(channel.EpisodesPerTurn);
        channel.SmartRotationMonths = VirtualTvModePolicy.NormalizeSmartRotationMonths(channel.SmartRotationMonths);

        channel.SeriesSelections ??= [];
        channel.RepeatingSeriesOrder ??= [];
        channel.VisibleUserIds ??= [];
        channel.SelectedItemIds ??= [];
        channel.SelectedLibraryIds ??= [];
    }

    private static void EnsureSelectedContent(ChannelConfiguration channel)
    {
        if (channel.SelectedItemIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one series or movie before generating a schedule.");
        }
    }

    private void Save(
        ChannelConfiguration channel,
        IReadOnlyList<VirtualTvScheduleEntry> entries,
        DateTime nowUtc)
    {
        _store.Save(channel.Id, entries);
        channel.ScheduleGeneratedUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        channel.ScheduleEndUtc = entries.Count == 0 ? string.Empty : entries[^1].EndUtc;
        channel.NeedsReconcile = false;
    }

    private static List<VirtualTvScheduleEntry> CombineAndTrim(
        IEnumerable<VirtualTvScheduleEntry> prefix,
        IEnumerable<VirtualTvScheduleEntry> generated,
        DateTime nowUtc)
    {
        var keepAfter = nowUtc.Subtract(HistoryRetention);
        return prefix
            .Concat(generated)
            .Where(entry => entry.GetEndUtc() > keepAfter)
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(entry => entry.GetStartUtc())
            .ToList();
    }

    private static DateTime GetNextMutableBoundary(
        IReadOnlyList<VirtualTvScheduleEntry> schedule,
        DateTime nowUtc)
    {
        var active = schedule.FirstOrDefault(entry =>
            entry.GetStartUtc() <= nowUtc && entry.GetEndUtc() > nowUtc);

        if (active is not null)
        {
            return active.GetEndUtc();
        }

        var future = schedule.FirstOrDefault(entry => entry.GetStartUtc() > nowUtc);
        return future?.GetStartUtc() ?? AlignStartToHour(nowUtc);
    }

    private static DateTime GetTargetHorizonUtc(ChannelConfiguration channel, DateTime nowUtc)
        => IsSmart(channel)
            ? GetNextSundayUtc(nowUtc.Add(FutureHorizon))
            : nowUtc.Add(FutureHorizon);

    private static bool IsSmart(ChannelConfiguration channel)
        => string.Equals(channel.SchedulingMethod, VirtualTvModePolicy.SmartSchedule, StringComparison.OrdinalIgnoreCase);

    private static DateTime AlignStartToHour(DateTime nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
        var localStart = new DateTime(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            localNow.Hour,
            0,
            0,
            DateTimeKind.Unspecified);

        return TimeZoneInfo.ConvertTimeToUtc(localStart, TimeZoneInfo.Local);
    }

    private static DateTime GetSundayStartUtc(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        var dayOffset = (int)local.DayOfWeek;
        var sunday = local.Date.AddDays(-dayOffset);
        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(sunday, DateTimeKind.Unspecified),
            TimeZoneInfo.Local);
    }

    private static DateTime GetNextSundayUtc(DateTime utc)
    {
        var start = GetSundayStartUtc(utc);
        if (start <= utc)
        {
            start = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(
                    TimeZoneInfo.ConvertTimeFromUtc(start, TimeZoneInfo.Local).AddDays(7),
                    DateTimeKind.Unspecified),
                TimeZoneInfo.Local);
        }

        return start;
    }

    private static bool TryAppendOffAir(
        ChannelConfiguration channel,
        List<VirtualTvScheduleEntry> entries,
        ref DateTime cursorUtc,
        DateTime horizonUtc)
    {
        if (channel.Is24Hours)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(cursorUtc, TimeZoneInfo.Local);
        var on = ParseClock(channel.OnAirStart, new TimeSpan(7, 0, 0));
        var off = ParseClock(channel.OffAirStart, new TimeSpan(2, 0, 0));

        if (IsOnAir(local.TimeOfDay, on, off))
        {
            return false;
        }

        var nextOnLocal = local.Date.Add(on);
        if (nextOnLocal <= local)
        {
            nextOnLocal = nextOnLocal.AddDays(1);
        }

        var nextOnUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(nextOnLocal, DateTimeKind.Unspecified),
            TimeZoneInfo.Local);

        if (nextOnUtc > horizonUtc)
        {
            nextOnUtc = horizonUtc;
        }

        entries.Add(new VirtualTvScheduleEntry
        {
            Name = "Off Air",
            Overview = "This Virtual TV channel is currently off air. Back on air at " + channel.OnAirStart + ".",
            IsOffAir = true,
            StartUtc = cursorUtc.ToString("O", CultureInfo.InvariantCulture),
            EndUtc = nextOnUtc.ToString("O", CultureInfo.InvariantCulture)
        });

        cursorUtc = nextOnUtc;
        return true;
    }

    private static DateTime? GetNextOffAirBoundaryUtc(ChannelConfiguration channel, DateTime cursorUtc)
    {
        if (channel.Is24Hours)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(cursorUtc, TimeZoneInfo.Local);
        var on = ParseClock(channel.OnAirStart, new TimeSpan(7, 0, 0));
        var off = ParseClock(channel.OffAirStart, new TimeSpan(2, 0, 0));

        if (on == off)
        {
            return null;
        }

        if (!IsOnAir(local.TimeOfDay, on, off))
        {
            return cursorUtc;
        }

        DateTime offLocal;
        if (on < off)
        {
            offLocal = local.Date.Add(off);
            if (offLocal <= local)
            {
                offLocal = offLocal.AddDays(1);
            }
        }
        else
        {
            offLocal = local.TimeOfDay >= on
                ? local.Date.AddDays(1).Add(off)
                : local.Date.Add(off);
        }

        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(offLocal, DateTimeKind.Unspecified),
            TimeZoneInfo.Local);
    }

    private static bool IsOnAirAt(ChannelConfiguration channel, DateTime utc)
    {
        if (channel.Is24Hours)
        {
            return true;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        return IsOnAir(
            local.TimeOfDay,
            ParseClock(channel.OnAirStart, new TimeSpan(7, 0, 0)),
            ParseClock(channel.OffAirStart, new TimeSpan(2, 0, 0)));
    }

    private static TimeSpan ParseClock(string value, TimeSpan fallback)
        => TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool IsOnAir(TimeSpan time, TimeSpan on, TimeSpan off)
    {
        if (on == off)
        {
            return true;
        }

        return on < off ? time >= on && time < off : time >= on || time < off;
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (var index = list.Count - 1; index > 0; index--)
        {
            var swap = Random.Shared.Next(index + 1);
            (list[index], list[swap]) = (list[swap], list[index]);
        }
    }

    private sealed class SeriesPicker
    {
        private readonly ChannelConfiguration _channel;
        private readonly IReadOnlyList<VirtualTvContentCatalog.SeriesContent> _all;
        private readonly Dictionary<Guid, VirtualTvContentCatalog.SeriesContent> _byId;
        private readonly VirtualTvScheduleGenerator _owner;
        private readonly ShuffleBag<VirtualTvContentCatalog.SeriesContent>? _randomBag;
        private readonly List<VirtualTvContentCatalog.SeriesContent> _fixedOrder;
        private int _cursor;

        public SeriesPicker(
            ChannelConfiguration channel,
            IReadOnlyList<VirtualTvContentCatalog.SeriesContent> all,
            IReadOnlyList<VirtualTvScheduleEntry> prefix,
            VirtualTvScheduleGenerator owner)
        {
            _channel = channel;
            _all = all;
            _owner = owner;
            _byId = all.ToDictionary(item => item.Id);

            if (string.Equals(channel.SchedulingMethod, VirtualTvModePolicy.RandomizedRotation, StringComparison.OrdinalIgnoreCase))
            {
                var ids = all.Select(item => item.Id).ToHashSet();
                var used = new HashSet<Guid>();
                for (var index = prefix.Count - 1; index >= 0 && used.Count < ids.Count; index--)
                {
                    var id = owner.GetEntrySeriesId(prefix[index]);
                    if (id == Guid.Empty || !ids.Contains(id))
                    {
                        continue;
                    }

                    if (!used.Add(id))
                    {
                        break;
                    }
                }

                var last = prefix.Select(owner.GetEntrySeriesId).LastOrDefault(id => id != Guid.Empty);
                _randomBag = new ShuffleBag<VirtualTvContentCatalog.SeriesContent>(
                    all,
                    item => item.Id,
                    last == Guid.Empty ? null : last,
                    used);
                _fixedOrder = [];
                return;
            }

            IEnumerable<Guid> orderedIds;
            if (string.Equals(channel.SchedulingMethod, VirtualTvModePolicy.ManualOrder, StringComparison.OrdinalIgnoreCase))
            {
                orderedIds = channel.SelectedItemIds
                    .Select(raw => Guid.TryParse(raw, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty);
            }
            else if (string.Equals(channel.SchedulingMethod, VirtualTvModePolicy.RepeatingOrder, StringComparison.OrdinalIgnoreCase))
            {
                orderedIds = channel.RepeatingSeriesOrder
                    .Select(raw => Guid.TryParse(raw, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty);
            }
            else
            {
                orderedIds = all.Select(item => item.Id);
            }

            _fixedOrder = orderedIds
                .Where(_byId.ContainsKey)
                .Select(id => _byId[id])
                .DistinctBy(item => item.Id)
                .ToList();

            foreach (var item in all)
            {
                if (_fixedOrder.All(existing => existing.Id != item.Id))
                {
                    _fixedOrder.Add(item);
                }
            }

            var lastSeriesId = prefix.Select(owner.GetEntrySeriesId).LastOrDefault(id => id != Guid.Empty);
            if (lastSeriesId != Guid.Empty)
            {
                var lastIndex = _fixedOrder.FindIndex(item => item.Id == lastSeriesId);
                _cursor = lastIndex >= 0 ? lastIndex + 1 : 0;
            }
        }

        public VirtualTvContentCatalog.SeriesContent Next(DateTime slotStartUtc)
        {
            if (_randomBag is not null)
            {
                return _randomBag.Next();
            }

            if (string.Equals(_channel.SchedulingMethod, VirtualTvModePolicy.SmartSchedule, StringComparison.OrdinalIgnoreCase))
            {
                return PickSmart(slotStartUtc);
            }

            var result = _fixedOrder[_cursor % _fixedOrder.Count];
            _cursor++;
            return result;
        }

        private VirtualTvContentCatalog.SeriesContent PickSmart(DateTime slotStartUtc)
        {
            if (_channel.SmartTemplateSeed == 0)
            {
                _channel.SmartTemplateSeed = Random.Shared.Next(1, int.MaxValue);
            }

            if (string.IsNullOrWhiteSpace(_channel.SmartTemplateCreatedUtc))
            {
                _channel.SmartTemplateCreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            }

            var local = TimeZoneInfo.ConvertTimeFromUtc(slotStartUtc, TimeZoneInfo.Local);
            var on = ParseClock(_channel.OnAirStart, new TimeSpan(7, 0, 0));
            var minutesFromOn = _channel.Is24Hours
                ? (int)local.TimeOfDay.TotalMinutes
                : (int)((local.TimeOfDay - on + TimeSpan.FromDays(1)).Ticks % TimeSpan.FromDays(1).Ticks / TimeSpan.TicksPerMinute);

            var nominalMinutes = VirtualTvModePolicy.IsDynamicUnwatched(_channel.ContentMode)
                ? _channel.BlockMinutes
                : 30;

            var slotOrdinal = Math.Max(0, minutesFromOn / Math.Max(1, nominalMinutes));
            var day = (int)local.DayOfWeek;
            var seed = unchecked(_channel.SmartTemplateSeed + (day * 7919));
            var order = _all.ToList();
            var random = new Random(seed);

            for (var index = order.Count - 1; index > 0; index--)
            {
                var swap = random.Next(index + 1);
                (order[index], order[swap]) = (order[swap], order[index]);
            }

            return order[slotOrdinal % order.Count];
        }
    }

    private sealed class ShuffleBag<T>
    {
        private readonly IReadOnlyList<T> _all;
        private readonly Func<T, Guid> _id;
        private readonly HashSet<Guid> _initialUsed;
        private List<T> _remaining = [];
        private Guid? _last;
        private bool _firstCycle = true;

        public ShuffleBag(
            IReadOnlyList<T> all,
            Func<T, Guid> id,
            Guid? last,
            HashSet<Guid>? initialUsed = null)
        {
            _all = all;
            _id = id;
            _last = last;
            _initialUsed = initialUsed ?? [];
        }

        public T Next()
        {
            if (_remaining.Count == 0)
            {
                var source = _firstCycle && _initialUsed.Count > 0
                    ? _all.Where(item => !_initialUsed.Contains(_id(item))).ToList()
                    : _all.ToList();

                if (source.Count == 0)
                {
                    source = _all.ToList();
                }

                Shuffle(source);

                if (_last.HasValue && source.Count > 1 && _id(source[0]) == _last.Value)
                {
                    var alternate = source.FindIndex(1, item => _id(item) != _last.Value);
                    if (alternate > 0)
                    {
                        (source[0], source[alternate]) = (source[alternate], source[0]);
                    }
                }

                _remaining = source;
                _firstCycle = false;
            }

            var result = _remaining[0];
            _remaining.RemoveAt(0);
            _last = _id(result);
            return result;
        }
    }
}
