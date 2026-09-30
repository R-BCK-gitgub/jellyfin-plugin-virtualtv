using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Central source of content eligibility for schedules and watched-dependent resolvers.
/// Keeping the rules here prevents Sequential, Random and Unwatched modes from drifting apart.
/// </summary>
public sealed class VirtualTvContentCatalog
{
    private readonly ILibraryManager _libraryManager;

    public VirtualTvContentCatalog(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public IReadOnlyList<SeriesContent> GetSeries(ChannelConfiguration channel)
    {
        var result = new List<SeriesContent>();

        foreach (var rawSeriesId in channel.SelectedItemIds)
        {
            if (!Guid.TryParse(rawSeriesId, out var seriesId))
            {
                continue;
            }

            var series = _libraryManager.GetItemById(seriesId);
            if (series is null || series.GetBaseItemKind() != BaseItemKind.Series)
            {
                continue;
            }

            var options = GetSeriesOptions(channel, seriesId);
            var orderedMode =
                string.Equals(channel.ContentMode, VirtualTvModePolicy.Sequential, StringComparison.OrdinalIgnoreCase)
                || string.Equals(channel.ContentMode, VirtualTvModePolicy.NextUnwatched, StringComparison.OrdinalIgnoreCase);

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [seriesId],
                IsVirtualItem = false
            })
            .OfType<Episode>()
            .Where(episode => IsEligibleEpisode(episode, options, orderedMode))
            .ToList();

            episodes = OrderEpisodes(episodes).ToList();

            if (episodes.Count == 0)
            {
                continue;
            }

            result.Add(new SeriesContent(
                series.Id,
                series.Name ?? string.Empty,
                series.Overview ?? string.Empty,
                series.ProductionYear,
                episodes));
        }

        return result;
    }

    public IReadOnlyList<BaseItem> GetMovies(ChannelConfiguration channel)
    {
        var result = new List<BaseItem>();

        foreach (var rawId in channel.SelectedItemIds)
        {
            if (!Guid.TryParse(rawId, out var id))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(id);
            if (item is not null && item.GetBaseItemKind() == BaseItemKind.Movie)
            {
                result.Add(item);
            }
        }

        return result;
    }

    public IReadOnlyList<int> GetAvailableSeasonNumbers(Guid seriesId)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesId],
            IsVirtualItem = false
        })
        .Where(item => item.ParentIndexNumber.HasValue && item.ParentIndexNumber.Value > 0)
        .Select(item => item.ParentIndexNumber!.Value)
        .Distinct()
        .OrderBy(value => value)
        .ToArray();
    }

    public static SeriesSelectionConfiguration GetSeriesOptions(
        ChannelConfiguration channel,
        Guid seriesId)
    {
        var value = channel.SeriesSelections.FirstOrDefault(item =>
            Guid.TryParse(item.SeriesId, out var parsed) && parsed == seriesId);

        return value ?? new SeriesSelectionConfiguration
        {
            SeriesId = seriesId.ToString("N"),
            AllSeasons = true,
            IncludeSpecials = false
        };
    }

    private static bool IsEligibleEpisode(
        Episode episode,
        SeriesSelectionConfiguration options,
        bool orderedMode)
    {
        var seasonNumber = episode.ParentIndexNumber.GetValueOrDefault();

        if (seasonNumber == 0)
        {
            if (!options.IncludeSpecials)
            {
                return false;
            }

            if (!orderedMode)
            {
                return true;
            }

            return HasReliableChronologicalPosition(episode);
        }

        if (seasonNumber < 0)
        {
            return false;
        }

        return options.AllSeasons || options.SelectedSeasonNumbers.Contains(seasonNumber);
    }

    private static bool HasReliableChronologicalPosition(Episode episode)
        => episode.AirsBeforeSeasonNumber.HasValue
            || episode.AirsAfterSeasonNumber.HasValue
            || episode.AirsBeforeEpisodeNumber.HasValue
            || episode.PremiereDate.HasValue;

    private static IEnumerable<Episode> OrderEpisodes(IReadOnlyList<Episode> episodes)
    {
        var regular = episodes
            .Where(item => item.ParentIndexNumber.GetValueOrDefault() > 0)
            .OrderBy(item => item.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(item => item.IndexNumber ?? int.MaxValue)
            .ThenBy(item => item.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var specials = episodes
            .Where(item => item.ParentIndexNumber.GetValueOrDefault() == 0)
            .ToList();

        if (specials.Count == 0)
        {
            return regular;
        }

        var positioned = new List<(Episode Item, double Key, DateTime Date)>();

        foreach (var episode in regular)
        {
            var season = episode.ParentIndexNumber ?? int.MaxValue / 1000;
            var number = episode.IndexNumber ?? 0;
            positioned.Add((episode, (season * 10000d) + number, episode.PremiereDate ?? DateTime.MaxValue));
        }

        foreach (var special in specials)
        {
            double key;

            if (special.AirsBeforeSeasonNumber.HasValue)
            {
                var beforeEpisode = special.AirsBeforeEpisodeNumber.GetValueOrDefault(1);
                key = (special.AirsBeforeSeasonNumber.Value * 10000d) + beforeEpisode - 0.5d;
            }
            else if (special.AirsAfterSeasonNumber.HasValue)
            {
                key = (special.AirsAfterSeasonNumber.Value * 10000d) + 9999d;
            }
            else if (special.PremiereDate.HasValue)
            {
                var next = regular.FirstOrDefault(item =>
                    item.PremiereDate.HasValue && item.PremiereDate.Value >= special.PremiereDate.Value);

                key = next is null
                    ? (regular.Count == 0 ? 0d : positioned.Max(item => item.Key) + 0.5d)
                    : positioned.First(item => item.Item.Id == next.Id).Key - 0.25d;
            }
            else
            {
                // Random modes can include undated Specials. Put them after regular episodes;
                // ordering is irrelevant to Random but remains deterministic for diagnostics.
                key = (regular.Count == 0 ? 0d : positioned.Max(item => item.Key)) + 10000d;
            }

            positioned.Add((special, key, special.PremiereDate ?? DateTime.MaxValue));
        }

        return positioned
            .OrderBy(item => item.Key)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Item);
    }

    public sealed record SeriesContent(
        Guid Id,
        string Name,
        string Overview,
        int? ProductionYear,
        IReadOnlyList<Episode> Episodes);
}
