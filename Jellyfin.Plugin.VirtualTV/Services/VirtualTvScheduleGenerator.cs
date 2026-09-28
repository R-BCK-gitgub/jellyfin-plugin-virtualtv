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
    {
        if (channel.SelectedItemIds.Count == 0)
            throw new InvalidOperationException("Select at least one series or movie before generating a schedule.");

        var nowUtc = DateTime.UtcNow;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
        var localStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, TimeZoneInfo.Local);
        var horizonUtc = nowUtc.AddDays(30);

        IReadOnlyList<PlayableItem> pool = string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
            ? GetMovies(channel)
            : GetSeriesEpisodes(channel);

        if (pool.Count == 0)
            throw new InvalidOperationException("The selected content contains no playable items.");

        var entries = string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
            ? BuildMovieSchedule(channel, pool, startUtc, horizonUtc)
            : BuildSeriesSchedule(channel, pool, startUtc, horizonUtc);

        _store.Save(channel.Id, entries);
        channel.ScheduleGeneratedUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        channel.ScheduleEndUtc = entries.Count == 0 ? string.Empty : entries[^1].EndUtc;
        channel.NeedsReconcile = false;
        return entries;
    }

    private IReadOnlyList<PlayableItem> GetMovies(ChannelConfiguration channel)
    {
        var result = new List<PlayableItem>();
        foreach (var rawId in channel.SelectedItemIds)
        {
            if (!Guid.TryParse(rawId, out var id)) continue;
            var item = _libraryManager.GetItemById(id);
            if (item is null || item.GetBaseItemKind() != BaseItemKind.Movie) continue;
            result.Add(ToPlayable(item, string.Empty, true));
        }
        return result;
    }

    private IReadOnlyList<PlayableItem> GetSeriesEpisodes(ChannelConfiguration channel)
    {
        var result = new List<PlayableItem>();
        foreach (var rawSeriesId in channel.SelectedItemIds)
        {
            if (!Guid.TryParse(rawSeriesId, out var seriesId)) continue;
            var series = _libraryManager.GetItemById(seriesId);
            if (series is null || series.GetBaseItemKind() != BaseItemKind.Series) continue;

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [seriesId],
                IsVirtualItem = false
            })
            .Where(item => item.ParentIndexNumber.GetValueOrDefault() != 0)
            .OrderBy(item => item.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(item => item.IndexNumber ?? int.MaxValue)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => ToPlayable(item, series.Name, false))
            .ToList();

            result.AddRange(episodes);
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

    private List<VirtualTvScheduleEntry> BuildSeriesSchedule(ChannelConfiguration channel, IReadOnlyList<PlayableItem> allEpisodes, DateTime startUtc, DateTime horizonUtc)
    {
        var selectedSeries = channel.SelectedItemIds.Where(id => Guid.TryParse(id, out _)).ToList();
        Shuffle(selectedSeries);

        var episodesBySeries = allEpisodes
            .GroupBy(item => item.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var seriesOrder = selectedSeries
            .Select(id => _libraryManager.GetItemById(Guid.Parse(id)))
            .Where(item => item is not null && episodesBySeries.ContainsKey(item.Name))
            .Select(item => item!.Name)
            .ToList();

        if (seriesOrder.Count == 0) seriesOrder = episodesBySeries.Keys.ToList();

        var episodeIndex = seriesOrder.ToDictionary(name => name, _ => 0, StringComparer.OrdinalIgnoreCase);
        var entries = new List<VirtualTvScheduleEntry>();
        var cursor = startUtc;
        var seriesCursor = 0;

        while (cursor < horizonUtc)
        {
            if (TryAppendOffAir(channel, entries, ref cursor, horizonUtc)) continue;

            var seriesName = seriesOrder[seriesCursor % seriesOrder.Count];
            seriesCursor++;
            var episodes = episodesBySeries[seriesName];
            var index = episodeIndex[seriesName] % episodes.Count;
            episodeIndex[seriesName] = index + 1;
            var item = episodes[index];

            entries.Add(CreateEntry(item, cursor));
            cursor = cursor.Add(item.Duration);
        }

        return entries;
    }

    private List<VirtualTvScheduleEntry> BuildMovieSchedule(ChannelConfiguration channel, IReadOnlyList<PlayableItem> movies, DateTime startUtc, DateTime horizonUtc)
    {
        var entries = new List<VirtualTvScheduleEntry>();
        var cursor = startUtc;
        var cycle = movies.ToList();
        Shuffle(cycle);
        var cycleIndex = 0;
        Guid? lastMovie = null;

        while (cursor < horizonUtc)
        {
            if (TryAppendOffAir(channel, entries, ref cursor, horizonUtc)) continue;

            if (cycleIndex >= cycle.Count)
            {
                cycle = movies.ToList();
                Shuffle(cycle);
                if (lastMovie.HasValue && cycle.Count > 1 && cycle[0].Id == lastMovie.Value)
                    (cycle[0], cycle[1]) = (cycle[1], cycle[0]);
                cycleIndex = 0;
            }

            var movie = cycle[cycleIndex++];
            entries.Add(CreateEntry(movie, cursor));
            cursor = cursor.Add(movie.Duration);
            lastMovie = movie.Id;
        }

        return entries;
    }

    private static VirtualTvScheduleEntry CreateEntry(PlayableItem item, DateTime startUtc)
    {
        var endUtc = startUtc.Add(item.Duration);
        return new VirtualTvScheduleEntry
        {
            SourceItemId = item.Id.ToString("N"),
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

    private static bool TryAppendOffAir(ChannelConfiguration channel, List<VirtualTvScheduleEntry> entries, ref DateTime cursorUtc, DateTime horizonUtc)
    {
        if (channel.Is24Hours) return false;

        var local = TimeZoneInfo.ConvertTimeFromUtc(cursorUtc, TimeZoneInfo.Local);
        var on = ParseClock(channel.OnAirStart, new TimeSpan(7, 0, 0));
        var off = ParseClock(channel.OffAirStart, new TimeSpan(2, 0, 0));
        if (IsOnAir(local.TimeOfDay, on, off)) return false;

        var nextOnLocal = local.Date.Add(on);
        if (nextOnLocal <= local) nextOnLocal = nextOnLocal.AddDays(1);
        var nextOnUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextOnLocal, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        if (nextOnUtc > horizonUtc) nextOnUtc = horizonUtc;

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
        if (on == off) return true;
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
