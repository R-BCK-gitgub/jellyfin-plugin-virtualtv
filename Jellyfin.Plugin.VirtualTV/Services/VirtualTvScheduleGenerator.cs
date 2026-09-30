using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Services;

public sealed class VirtualTvScheduleGenerator
{
    private static readonly TimeSpan FallbackEpisodeDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FallbackMovieDuration = TimeSpan.FromMinutes(90);

    private readonly ILibraryManager _libraryManager;
    private readonly VirtualTvScheduleStore _store;

    public VirtualTvScheduleGenerator(ILibraryManager libraryManager, VirtualTvScheduleStore store)
    {
        _libraryManager = libraryManager;
        _store = store;
    }

    public IReadOnlyList<VirtualTvScheduleEntry> Generate(ChannelConfiguration channel)
        => Generate(channel, null, preserveBeforeStart: false);

    /// <summary>
    /// Generates a schedule from an optional explicit UTC start. Smart Schedule refresh uses
    /// this overload to replace only the future schedule at the rotation boundary.
    /// </summary>
    public IReadOnlyList<VirtualTvScheduleEntry> Generate(
        ChannelConfiguration channel,
        DateTime? explicitStartUtc,
        bool preserveBeforeStart)
    {
        if (channel.SelectedItemIds.Count == 0)
            throw new InvalidOperationException("Select at least one series or movie before generating a schedule.");

        Normalize(channel);

        var nowUtc = DateTime.UtcNow;
        var startUtc = explicitStartUtc?.ToUniversalTime() ?? AlignStartToHour(nowUtc);
        var horizonUtc = string.Equals(channel.SchedulingMethod, VirtualTvModePolicy.SmartSchedule, StringComparison.OrdinalIgnoreCase)
            ? startUtc.AddMonths(channel.SmartRotationMonths)
            : startUtc.AddDays(30);

        IReadOnlyList<VirtualTvScheduleEntry> entries;

        if (string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            var movies = GetMovies(channel);
            if (movies.Count == 0)
                throw new InvalidOperationException("The selected content contains no playable movies.");

            entries = BuildMovieSchedule(channel, movies, startUtc, horizonUtc);
        }
        else
        {
            var series = GetSeries(channel);
            if (series.Count == 0)
                throw new InvalidOperationException("The selected content contains no playable series episodes.");

            entries = VirtualTvModePolicy.IsDynamicUnwatched(channel.ContentMode)
                ? BuildDynamicSeriesBlockSchedule(channel, series, startUtc, horizonUtc)
                : BuildConcreteSeriesSchedule(channel, series, startUtc, horizonUtc);
        }

        if (preserveBeforeStart)
        {
            // Keep enough history for Guide continuity without allowing a Smart Schedule file
            // to grow forever across repeated month-based rotations.
            var keepHistoryFromUtc = nowUtc.AddDays(-1);
            var prefix = _store.Load(channel.Id)
                .Where(entry =>
                    entry.GetEndUtc() > keepHistoryFromUtc
                    && entry.GetStartUtc() < startUtc)
                .ToList();

            // If an older entry crosses the regeneration boundary, clip it so the new template
            // becomes authoritative exactly at the requested start.
            if (prefix.Count > 0 && prefix[^1].GetEndUtc() > startUtc)
            {
                prefix[^1].EndUtc = startUtc.ToString("O", CultureInfo.InvariantCulture);
            }

            entries = prefix
                .Concat(entries)
                .OrderBy(entry => entry.GetStartUtc())
                .ToArray();
        }

        _store.Save(channel.Id, entries);
        channel.ScheduleGeneratedUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        channel.ScheduleEndUtc = entries.Count == 0 ? string.Empty : entries[^1].EndUtc;
        channel.NeedsReconcile = false;
        return entries;
    }

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

    private static void Normalize(ChannelConfiguration channel)
    {
        channel.ContentMode = string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
            ? "RandomShuffleCycle"
            : VirtualTvModePolicy.NormalizeContentMode(channel.ContentMode);

        channel.SchedulingMethod = string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : VirtualTvModePolicy.NormalizeSchedulingMethod(channel.SchedulingMethod);

        channel.BlockMinutes = VirtualTvModePolicy.NormalizeBlockMinutes(channel.BlockMinutes);
        channel.EpisodesPerTurn = VirtualTvModePolicy.NormalizeEpisodesPerTurn(channel.EpisodesPerTurn);
        channel.SmartRotationMonths = VirtualTvModePolicy.NormalizeSmartRotationMonths(channel.SmartRotationMonths);
    }

    private IReadOnlyList<PlayableItem> GetMovies(ChannelConfiguration channel)
    {
        var result = new List<PlayableItem>();
        foreach (var rawId in channel.SelectedItemIds)
        {
            if (!Guid.TryParse(rawId, out var id))
                continue;

            var item = _libraryManager.GetItemById(id);
            if (item is null || item.GetBaseItemKind() != BaseItemKind.Movie)
                continue;

            result.Add(ToPlayable(item, string.Empty, true));
        }

        return result;
    }

    private IReadOnlyList<SeriesPool> GetSeries(ChannelConfiguration channel)
    {
        var result = new List<SeriesPool>();

        foreach (var rawSeriesId in channel.SelectedItemIds)
        {
            if (!Guid.TryParse(rawSeriesId, out var seriesId))
                continue;

            var series = _libraryManager.GetItemById(seriesId);
            if (series is null || series.GetBaseItemKind() != BaseItemKind.Series)
                continue;

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [seriesId],
                IsVirtualItem = false
            })
            .Where(item => item.ParentIndexNumber.GetValueOrDefault() != 0)
            .OrderBy(item => item.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(item => item.IndexNumber ?? int.MaxValue)
            .ThenBy(item => item.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => ToPlayable(item, series.Name, false))
            .ToList();

            if (episodes.Count == 0)
                continue;

            result.Add(new SeriesPool(
                series.Id,
                series.Name,
                series.Overview ?? string.Empty,
                series.ProductionYear,
                episodes));
        }

        return result;
    }

    private static PlayableItem ToPlayable(BaseItem item, string seriesName, bool isMovie)
        => new(
            item.Id,
            item.Name ?? string.Empty,
            seriesName,
            item.Overview ?? string.Empty,
            item.ParentIndexNumber,
            item.IndexNumber,
            item.ProductionYear,
            item.PremiereDate,
            item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0
                ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
                : (isMovie ? FallbackMovieDuration : FallbackEpisodeDuration),
            isMovie);

    private List<VirtualTvScheduleEntry> BuildDynamicSeriesBlockSchedule(
        ChannelConfiguration channel,
        IReadOnlyList<SeriesPool> series,
        DateTime startUtc,
        DateTime horizonUtc)
    {
        var entries = new List<VirtualTvScheduleEntry>();
        var cursor = startUtc;
        var blockDuration = TimeSpan.FromMinutes(channel.BlockMinutes);
        var rotation = new SeriesRotation(channel, series);
        var smartDayCounters = new Dictionary<DateOnly, int>();
        var smartPatterns = string.Equals(channel.SchedulingMethod, VirtualTvModePolicy.SmartSchedule, StringComparison.OrdinalIgnoreCase)
            ? BuildSmartDayPatterns(channel, series)
            : null;
        SeriesPool? currentRotationSeries = null;
        var remainingRotationBlocks = 0;

        while (cursor < horizonUtc)
        {
            if (TryAppendOffAir(channel, entries, ref cursor, horizonUtc))
                continue;

            var localCursor = TimeZoneInfo.ConvertTimeFromUtc(cursor, TimeZoneInfo.Local);
            SeriesPool selected;

            if (smartPatterns is not null)
            {
                var date = DateOnly.FromDateTime(localCursor);
                var ordinal = smartDayCounters.TryGetValue(date, out var value) ? value : 0;
                smartDayCounters[date] = ordinal + 1;

                var pattern = smartPatterns[localCursor.DayOfWeek];
                selected = pattern[ordinal % pattern.Count];
            }
            else
            {
                if (currentRotationSeries is null || remainingRotationBlocks <= 0)
                {
                    currentRotationSeries = rotation.Next();
                    remainingRotationBlocks = channel.EpisodesPerTurn;
                }

                selected = currentRotationSeries;
                remainingRotationBlocks--;
            }

            var endUtc = cursor.Add(blockDuration);
            var offAirBoundary = GetNextOffAirBoundaryUtc(channel, cursor);
            if (offAirBoundary.HasValue && offAirBoundary.Value > cursor && endUtc > offAirBoundary.Value)
            {
                endUtc = offAirBoundary.Value;
            }

            if (endUtc > horizonUtc)
                endUtc = horizonUtc;

            entries.Add(CreateDynamicBlockEntry(channel, selected, cursor, endUtc));
            cursor = endUtc;
        }

        return entries;
    }

    private List<VirtualTvScheduleEntry> BuildConcreteSeriesSchedule(
        ChannelConfiguration channel,
        IReadOnlyList<SeriesPool> series,
        DateTime startUtc,
        DateTime horizonUtc)
    {
        var entries = new List<VirtualTvScheduleEntry>();
        var cursor = startUtc;
        var rotation = new SeriesRotation(channel, series);
        var nextEpisodeIndex = series.ToDictionary(item => item.Id, _ => 0);
        var lastEpisodeBySeries = new Dictionary<Guid, Guid>();

        while (cursor < horizonUtc)
        {
            if (TryAppendOffAir(channel, entries, ref cursor, horizonUtc))
                continue;

            var selected = rotation.Next();

            for (var turn = 0; turn < channel.EpisodesPerTurn && cursor < horizonUtc; turn++)
            {
                if (!channel.Is24Hours && !IsOnAir(TimeZoneInfo.ConvertTimeFromUtc(cursor, TimeZoneInfo.Local).TimeOfDay, ParseClock(channel.OnAirStart, new TimeSpan(7, 0, 0)), ParseClock(channel.OffAirStart, new TimeSpan(2, 0, 0))))
                {
                    break;
                }

                PlayableItem episode;
                if (string.Equals(channel.ContentMode, VirtualTvModePolicy.Random, StringComparison.OrdinalIgnoreCase))
                {
                    episode = PickRandomEpisode(selected.Episodes, lastEpisodeBySeries.TryGetValue(selected.Id, out var last) ? last : null);
                }
                else
                {
                    var index = nextEpisodeIndex[selected.Id] % selected.Episodes.Count;
                    episode = selected.Episodes[index];
                    nextEpisodeIndex[selected.Id] = index + 1;
                }

                entries.Add(CreateConcreteEntry(episode, cursor, channel.ContentMode));
                cursor = cursor.Add(episode.Duration);
                lastEpisodeBySeries[selected.Id] = episode.Id;
            }
        }

        return entries;
    }

    private List<VirtualTvScheduleEntry> BuildMovieSchedule(
        ChannelConfiguration channel,
        IReadOnlyList<PlayableItem> movies,
        DateTime startUtc,
        DateTime horizonUtc)
    {
        var entries = new List<VirtualTvScheduleEntry>();
        var cursor = startUtc;
        var cycle = movies.ToList();
        Shuffle(cycle);
        var cycleIndex = 0;
        Guid? lastMovie = null;

        while (cursor < horizonUtc)
        {
            if (TryAppendOffAir(channel, entries, ref cursor, horizonUtc))
                continue;

            if (cycleIndex >= cycle.Count)
            {
                cycle = movies.ToList();
                Shuffle(cycle);
                if (lastMovie.HasValue && cycle.Count > 1 && cycle[0].Id == lastMovie.Value)
                    (cycle[0], cycle[1]) = (cycle[1], cycle[0]);
                cycleIndex = 0;
            }

            var movie = cycle[cycleIndex++];
            entries.Add(CreateConcreteEntry(movie, cursor, "RandomShuffleCycle"));
            cursor = cursor.Add(movie.Duration);
            lastMovie = movie.Id;
        }

        return entries;
    }

    private static VirtualTvScheduleEntry CreateConcreteEntry(
        PlayableItem item,
        DateTime startUtc,
        string playbackMode)
    {
        var endUtc = startUtc.Add(item.Duration);
        return new VirtualTvScheduleEntry
        {
            SourceItemId = item.Id.ToString("N"),
            PlaybackMode = playbackMode,
            Name = item.Name,
            SeriesName = item.SeriesName,
            Overview = item.Overview,
            SeasonNumber = item.SeasonNumber,
            EpisodeNumber = item.EpisodeNumber,
            ProductionYear = item.ProductionYear,
            PremiereDateUtc = item.PremiereDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            IsMovie = item.IsMovie,
            StartUtc = startUtc.ToString("O", CultureInfo.InvariantCulture),
            EndUtc = endUtc.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private static VirtualTvScheduleEntry CreateDynamicBlockEntry(
        ChannelConfiguration channel,
        SeriesPool series,
        DateTime startUtc,
        DateTime endUtc)
    {
        var bootstrap = series.Episodes[0];
        return new VirtualTvScheduleEntry
        {
            SourceItemId = bootstrap.Id.ToString("N"),
            SourceSeriesId = series.Id.ToString("N"),
            PlaybackMode = channel.ContentMode,
            IsDynamicBlock = true,
            BlockMinutes = channel.BlockMinutes,
            Name = series.Name,
            SeriesName = series.Name,
            Overview = series.Overview,
            ProductionYear = series.ProductionYear,
            IsMovie = false,
            StartUtc = startUtc.ToString("O", CultureInfo.InvariantCulture),
            EndUtc = endUtc.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private static PlayableItem PickRandomEpisode(IReadOnlyList<PlayableItem> episodes, Guid? lastEpisodeId)
    {
        if (episodes.Count == 1)
            return episodes[0];

        PlayableItem selected;
        do
        {
            selected = episodes[Random.Shared.Next(episodes.Count)];
        }
        while (lastEpisodeId.HasValue && selected.Id == lastEpisodeId.Value);

        return selected;
    }

    private static Dictionary<DayOfWeek, IReadOnlyList<SeriesPool>> BuildSmartDayPatterns(
        ChannelConfiguration channel,
        IReadOnlyList<SeriesPool> series)
    {
        var result = new Dictionary<DayOfWeek, IReadOnlyList<SeriesPool>>();
        var slotsPerDay = Math.Max(1, (int)Math.Ceiling(GetOnAirMinutes(channel) / (double)channel.BlockMinutes));

        for (var day = 0; day < 7; day++)
        {
            var sequence = new List<SeriesPool>(slotsPerDay);
            var baseOrder = series.ToList();

            // Different but stable-in-this-generation starting order for every weekday.
            // All selected series are used before the next cycle starts.
            var shift = (day * 2) % baseOrder.Count;
            baseOrder = baseOrder.Skip(shift).Concat(baseOrder.Take(shift)).ToList();
            if (day % 2 == 1)
                baseOrder.Reverse();

            SeriesPool? previous = null;
            var cursor = 0;

            while (sequence.Count < slotsPerDay)
            {
                var candidate = baseOrder[cursor % baseOrder.Count];
                cursor++;

                if (previous is not null && candidate.Id == previous.Id && baseOrder.Count > 1)
                    continue;

                for (var repeat = 0; repeat < channel.EpisodesPerTurn && sequence.Count < slotsPerDay; repeat++)
                    sequence.Add(candidate);

                previous = candidate;
            }

            result[(DayOfWeek)day] = sequence;
        }

        return result;
    }

    private static int GetOnAirMinutes(ChannelConfiguration channel)
    {
        if (channel.Is24Hours)
            return 24 * 60;

        var on = ParseClock(channel.OnAirStart, new TimeSpan(7, 0, 0));
        var off = ParseClock(channel.OffAirStart, new TimeSpan(2, 0, 0));

        if (on == off)
            return 24 * 60;

        var minutes = off > on
            ? (off - on).TotalMinutes
            : (TimeSpan.FromDays(1) - on + off).TotalMinutes;

        return Math.Max(1, (int)Math.Round(minutes));
    }

    private static DateTime? GetNextOffAirBoundaryUtc(ChannelConfiguration channel, DateTime cursorUtc)
    {
        if (channel.Is24Hours)
            return null;

        var local = TimeZoneInfo.ConvertTimeFromUtc(cursorUtc, TimeZoneInfo.Local);
        var on = ParseClock(channel.OnAirStart, new TimeSpan(7, 0, 0));
        var off = ParseClock(channel.OffAirStart, new TimeSpan(2, 0, 0));

        if (on == off)
            return null;

        if (!IsOnAir(local.TimeOfDay, on, off))
            return cursorUtc;

        DateTime offLocal;
        if (on < off)
        {
            offLocal = local.Date.Add(off);
            if (offLocal <= local)
                offLocal = offLocal.AddDays(1);
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

    private static bool TryAppendOffAir(
        ChannelConfiguration channel,
        List<VirtualTvScheduleEntry> entries,
        ref DateTime cursorUtc,
        DateTime horizonUtc)
    {
        if (channel.Is24Hours)
            return false;

        var local = TimeZoneInfo.ConvertTimeFromUtc(cursorUtc, TimeZoneInfo.Local);
        var on = ParseClock(channel.OnAirStart, new TimeSpan(7, 0, 0));
        var off = ParseClock(channel.OffAirStart, new TimeSpan(2, 0, 0));
        if (IsOnAir(local.TimeOfDay, on, off))
            return false;

        var nextOnLocal = local.Date.Add(on);
        if (nextOnLocal <= local)
            nextOnLocal = nextOnLocal.AddDays(1);

        var nextOnUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(nextOnLocal, DateTimeKind.Unspecified),
            TimeZoneInfo.Local);

        if (nextOnUtc > horizonUtc)
            nextOnUtc = horizonUtc;

        entries.Add(new VirtualTvScheduleEntry
        {
            Name = "Off Air",
            IsOffAir = true,
            StartUtc = cursorUtc.ToString("O", CultureInfo.InvariantCulture),
            EndUtc = nextOnUtc.ToString("O", CultureInfo.InvariantCulture)
        });

        cursorUtc = nextOnUtc;
        return true;
    }

    private static TimeSpan ParseClock(string value, TimeSpan fallback)
        => TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool IsOnAir(TimeSpan time, TimeSpan on, TimeSpan off)
    {
        if (on == off)
            return true;

        return on < off ? time >= on && time < off : time >= on || time < off;
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private sealed class SeriesRotation
    {
        private readonly ChannelConfiguration _channel;
        private readonly List<SeriesPool> _manualOrder;
        private readonly List<SeriesPool> _alphabeticalOrder;
        private readonly List<SeriesPool> _smartSequence;
        private List<SeriesPool> _randomCycle = [];
        private int _cursor;
        private SeriesPool? _previousRandom;

        public SeriesRotation(ChannelConfiguration channel, IReadOnlyList<SeriesPool> series)
        {
            _channel = channel;
            _manualOrder = series.ToList();
            _alphabeticalOrder = series.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _smartSequence = BuildStaticSmartSequence(series);
        }

        public SeriesPool Next()
            => _channel.SchedulingMethod switch
            {
                VirtualTvModePolicy.RandomizedRotation => NextRandom(),
                VirtualTvModePolicy.ManualOrder => NextFrom(_manualOrder),
                VirtualTvModePolicy.SmartSchedule => NextFrom(_smartSequence),
                _ => NextFrom(_alphabeticalOrder)
            };

        private SeriesPool NextFrom(IReadOnlyList<SeriesPool> source)
        {
            var value = source[_cursor % source.Count];
            _cursor++;
            return value;
        }

        private SeriesPool NextRandom()
        {
            if (_randomCycle.Count == 0 || _cursor >= _randomCycle.Count)
            {
                _randomCycle = _manualOrder.ToList();
                Shuffle(_randomCycle);

                if (_previousRandom is not null
                    && _randomCycle.Count > 1
                    && _randomCycle[0].Id == _previousRandom.Id)
                {
                    (_randomCycle[0], _randomCycle[1]) = (_randomCycle[1], _randomCycle[0]);
                }

                _cursor = 0;
            }

            var selected = _randomCycle[_cursor++];
            _previousRandom = selected;
            return selected;
        }

        private static List<SeriesPool> BuildStaticSmartSequence(
            IReadOnlyList<SeriesPool> source)
        {
            var result = new List<SeriesPool>();
            for (var day = 0; day < 7; day++)
            {
                var order = source.ToList();
                var shift = (day * 2) % order.Count;
                order = order.Skip(shift).Concat(order.Take(shift)).ToList();
                if (day % 2 == 1)
                    order.Reverse();

                foreach (var item in order)
                {
                    // SeriesRotation itself applies EpisodesPerTurn. Do not duplicate here.
                    result.Add(item);
                }
            }

            return result;
        }
    }

    private sealed record SeriesPool(
        Guid Id,
        string Name,
        string Overview,
        int? ProductionYear,
        IReadOnlyList<PlayableItem> Episodes);

    private sealed record PlayableItem(
        Guid Id,
        string Name,
        string SeriesName,
        string Overview,
        int? SeasonNumber,
        int? EpisodeNumber,
        int? ProductionYear,
        DateTime? PremiereDate,
        TimeSpan Duration,
        bool IsMovie);
}
