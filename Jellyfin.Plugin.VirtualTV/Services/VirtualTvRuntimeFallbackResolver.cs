using System;
using System.Linq;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Resolves local, non-destructive substitutes when a materialized item disappears or cannot
/// be opened. It never rewrites the persisted schedule; Reconcile owns structural correction.
/// </summary>
public sealed class VirtualTvRuntimeFallbackResolver
{
    private readonly VirtualTvContentCatalog _catalog;

    public VirtualTvRuntimeFallbackResolver(VirtualTvContentCatalog catalog)
    {
        _catalog = catalog;
    }

    public BaseItem? ResolveTraditionalFallback(
        ChannelConfiguration channel,
        VirtualTvScheduleEntry entry,
        Guid? failedItemId)
    {
        if (string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            var movies = _catalog.GetMovies(channel)
                .Where(item => !failedItemId.HasValue || item.Id != failedItemId.Value)
                .ToArray();

            return movies.Length == 0 ? null : movies[Random.Shared.Next(movies.Length)];
        }

        if (!Guid.TryParse(entry.SourceSeriesId, out var seriesId))
        {
            return null;
        }

        var series = _catalog.GetSeries(channel).FirstOrDefault(item => item.Id == seriesId);
        if (series is null || series.Episodes.Count == 0)
        {
            return null;
        }

        if (string.Equals(channel.ContentMode, VirtualTvModePolicy.Random, StringComparison.OrdinalIgnoreCase))
        {
            var randomPool = series.Episodes
                .Where(item => !failedItemId.HasValue || item.Id != failedItemId.Value)
                .ToArray();

            return randomPool.Length == 0 ? null : randomPool[Random.Shared.Next(randomPool.Length)];
        }

        // Sequential: select the next eligible chronological episode. When the failed item is
        // already absent from Jellyfin, use the persisted season/episode metadata as the cursor.
        var episodes = series.Episodes.ToList();
        if (failedItemId.HasValue)
        {
            var failedIndex = episodes.FindIndex(item => item.Id == failedItemId.Value);
            if (failedIndex >= 0)
            {
                return episodes[(failedIndex + 1) % episodes.Count];
            }
        }

        if (entry.SeasonNumber.HasValue && entry.EpisodeNumber.HasValue)
        {
            var later = episodes.FirstOrDefault(item =>
                item.ParentIndexNumber.GetValueOrDefault() > entry.SeasonNumber.Value
                || (item.ParentIndexNumber.GetValueOrDefault() == entry.SeasonNumber.Value
                    && item.IndexNumber.GetValueOrDefault() > entry.EpisodeNumber.Value));

            if (later is not null)
            {
                return later;
            }
        }

        return episodes[0];
    }

    public BaseItem? ResolveBootstrapFallback(
        ChannelConfiguration channel,
        VirtualTvScheduleEntry entry,
        Guid? failedItemId)
    {
        if (entry.IsDynamicBlock
            && string.Equals(entry.DynamicKind, "Series", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(entry.SourceSeriesId, out var seriesId))
        {
            var series = _catalog.GetSeries(channel).FirstOrDefault(item => item.Id == seriesId);
            return series?.Episodes.FirstOrDefault(item => !failedItemId.HasValue || item.Id != failedItemId.Value);
        }

        if (entry.IsDynamicBlock
            && string.Equals(entry.DynamicKind, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            return _catalog.GetMovies(channel)
                .FirstOrDefault(item => !failedItemId.HasValue || item.Id != failedItemId.Value);
        }

        return ResolveTraditionalFallback(channel, entry, failedItemId);
    }
}
